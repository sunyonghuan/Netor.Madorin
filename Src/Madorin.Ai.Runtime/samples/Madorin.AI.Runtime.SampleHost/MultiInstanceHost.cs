using System.Diagnostics;
using Madorin.AI.Runtime.Client;

internal static class MultiInstanceHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configuration = await HostConfiguration.ParseAsync(args).ConfigureAwait(false);
            await using var host = new HostSession(configuration);
            await host.StartAsync().ConfigureAwait(false);
            Console.WriteLine($"READY|{configuration.HostInstanceId}|{host.GetProcessId(0)}|{host.GetProcessId(1)}");
            Console.Out.Flush();
            await host.ProcessCommandsAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"ERROR|{ex.Message}");
            return 1;
        }
    }

    private sealed class HostSession(HostConfiguration configuration) : IAsyncDisposable
    {
        private readonly RuntimeProcess[] _processes =
        [
            new(configuration.Runtimes[0]),
            new(configuration.Runtimes[1])
        ];
        private readonly RuntimeClient?[] _clients = new RuntimeClient?[2];
        private int _disposed;

        public int GetProcessId(int index) => _processes[index].ProcessId;

        public async Task StartAsync()
        {
            foreach (var process in _processes)
            {
                await process.StartAsync().ConfigureAwait(false);
            }

            for (var index = 0; index < _processes.Length; index++)
            {
                _clients[index] = await ConnectWithRetryAsync(index).ConfigureAwait(false);
            }
        }

        public async Task ProcessCommandsAsync()
        {
            while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var command = line.Trim();
                if (command.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("BYE");
                    Console.Out.Flush();
                    return;
                }

                if (command.StartsWith("reconnect ", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(command[10..], out var index)
                    && index is >= 0 and <= 1)
                {
                    await ReconnectAsync(index).ConfigureAwait(false);
                    Console.WriteLine($"OK|reconnect|{index}");
                    Console.Out.Flush();
                    continue;
                }

                Console.WriteLine("ERROR|unknown-command");
                Console.Out.Flush();
            }
        }

        private async Task ReconnectAsync(int index)
        {
            if (_clients[index] is { } oldClient)
            {
                await oldClient.DisposeAsync().ConfigureAwait(false);
                _clients[index] = null;
            }

            _clients[index] = await ConnectWithRetryAsync(index).ConfigureAwait(false);
        }

        private async Task<RuntimeClient> ConnectWithRetryAsync(int index)
        {
            var runtime = _processes[index];
            Exception? lastError = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (runtime.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Runtime {index} exited before authentication (code {runtime.ExitCode}): "
                        + await runtime.GetExitDiagnosticAsync().ConfigureAwait(false));
                }

                var client = new RuntimeClient(new RuntimeClientOptions(
                    runtime.PipeName,
                    configuration.HostInstanceId,
                    ConnectTimeout: TimeSpan.FromMilliseconds(500),
                    ExpectedRuntimeInstanceId: runtime.InstanceId,
                    HandshakeSecret: runtime.Secret,
                    ExpectedServerProcessId: OperatingSystem.IsWindows() ? runtime.ProcessId : null));
                try
                {
                    await client.ConnectAsync().ConfigureAwait(false);
                    return client;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException)
                {
                    lastError = ex;
                    await client.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            throw new TimeoutException(
                $"Runtime {index} did not authenticate within the startup window: {lastError?.Message}");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var client in _clients)
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            foreach (var process in _processes)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class RuntimeProcess(RuntimeConfiguration configuration) : IAsyncDisposable
    {
        private Process? _process;
        private Task<string>? _standardOutput;
        private Task<string>? _standardError;

        public string InstanceId => configuration.InstanceId;

        public string PipeName =>
            $"{configuration.PipePrefix}.{configuration.InstanceId[..Math.Min(8, configuration.InstanceId.Length)]}";

        public string Secret => configuration.Secret;

        public int ProcessId => _process?.Id
            ?? throw new InvalidOperationException("The Runtime process has not started.");

        public bool HasExited => _process?.HasExited ?? true;

        public int ExitCode => _process?.ExitCode
            ?? throw new InvalidOperationException("The Runtime process has not started.");

        public Task StartAsync()
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("The Runtime process was already started.");
            }

            var runtimePath = Path.GetFullPath(configuration.RuntimePath);
            if (!File.Exists(runtimePath))
            {
                throw new FileNotFoundException("The Runtime CLI was not found.", runtimePath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = runtimePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? "dotnet"
                    : runtimePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = configuration.Workspace
            };
            if (runtimePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(runtimePath);
            }

            startInfo.ArgumentList.Add("--workspace");
            startInfo.ArgumentList.Add(configuration.Workspace);
            startInfo.ArgumentList.Add("serve");
            startInfo.ArgumentList.Add("--instance");
            startInfo.ArgumentList.Add(configuration.InstanceId);
            startInfo.ArgumentList.Add("--pipe-prefix");
            startInfo.ArgumentList.Add(configuration.PipePrefix);
            startInfo.ArgumentList.Add("--max-runs");
            startInfo.ArgumentList.Add("1");
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Runtime process could not be started.");
            _process.StandardInput.WriteLine(configuration.Secret);
            _process.StandardInput.Flush();
            _process.StandardInput.Close();
            _standardOutput = _process.StandardOutput.ReadToEndAsync();
            _standardError = _process.StandardError.ReadToEndAsync();
            return Task.CompletedTask;
        }

        public async Task<string> GetExitDiagnosticAsync()
        {
            var standardOutput = _standardOutput is null
                ? string.Empty
                : await _standardOutput.ConfigureAwait(false);
            var standardError = _standardError is null
                ? string.Empty
                : await _standardError.ConfigureAwait(false);
            return $"stdout={standardOutput.Trim()} stderr={standardError.Trim()}";
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            _ = await GetExitDiagnosticAsync().ConfigureAwait(false);

            _process.Dispose();
            _process = null;
        }

    }

    private sealed record RuntimeConfiguration(
        string RuntimePath,
        string Workspace,
        string InstanceId,
        string PipePrefix,
        string Secret);

    private sealed record HostConfiguration(
        string HostInstanceId,
        RuntimeConfiguration[] Runtimes)
    {
        public static async Task<HostConfiguration> ParseAsync(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal)
                    || index + 1 >= args.Length
                    || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Invalid multi-instance argument at position {index}.");
                }

                values[args[index]] = args[++index];
            }

            var runtimePath = GetRequired(values, "--runtime");
            var hostInstanceId = GetRequired(values, "--host");
            var runtimes = new RuntimeConfiguration[2];
            for (var index = 0; index < runtimes.Length; index++)
            {
                var suffix = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var secret = await Console.In.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(secret))
                {
                    throw new ArgumentException(
                        $"Runtime secret {suffix} was not supplied on the controlled stdin handle.");
                }

                runtimes[index] = new RuntimeConfiguration(
                    runtimePath,
                    GetRequired(values, "--workspace-" + suffix),
                    GetRequired(values, "--instance-" + suffix),
                    GetRequired(values, "--pipe-prefix-" + suffix),
                    secret);
            }

            return new HostConfiguration(hostInstanceId, runtimes);
        }

        private static string GetRequired(Dictionary<string, string> values, string name)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{name} is required.");
            }

            return value;
        }
    }
}
