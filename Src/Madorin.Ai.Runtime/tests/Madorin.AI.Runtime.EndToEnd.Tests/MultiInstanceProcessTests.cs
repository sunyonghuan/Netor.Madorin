using System.Diagnostics;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class MultiInstanceProcessTests
{
    [TestMethod]
    [DoNotParallelize]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task TwoHostsWithTwoRuntimes_IsolatedAndCrossConnectionsFail()
    {
        var root = CreateTemporaryDirectory();
        var runtimePath = FindArtifact("src", "Madorin.AI.Runtime.Cli", "madorin.dll");
        var sampleHostPath = FindArtifact("samples", "Madorin.AI.Runtime.SampleHost", "Madorin.AI.Runtime.SampleHost.dll");
        var hostA = CreateHostConfiguration(root, "a");
        var hostB = CreateHostConfiguration(root, "b");
        await using var processA = new HostProcess(sampleHostPath, runtimePath, hostA);
        await using var processB = new HostProcess(sampleHostPath, runtimePath, hostB);

        try
        {
            await Task.WhenAll(processA.StartAsync(), processB.StartAsync()).ConfigureAwait(false);
            await processA.ReconnectAsync(0).ConfigureAwait(false);
            await processA.ReconnectAsync(1).ConfigureAwait(false);
            await processB.ReconnectAsync(0).ConfigureAwait(false);
            await processB.ReconnectAsync(1).ConfigureAwait(false);

            await AssertConnectionFailsAsync(
                hostA.Runtimes[0] with { Secret = hostB.Runtimes[0].Secret },
                "wrong secret").ConfigureAwait(false);
            await AssertConnectionFailsAsync(
                hostA.Runtimes[0] with
                {
                    PipeName = hostB.Runtimes[0].PipeName
                },
                "swapped endpoint").ConfigureAwait(false);
            await AssertConnectionFailsAsync(
                hostA.Runtimes[0] with { ExpectedRuntimeInstanceId = hostA.Runtimes[1].InstanceId },
                "swapped runtime instance").ConfigureAwait(false);

            await AssertConnectionFailsAsync(
                hostA.Runtimes[0] with { ExpectedServerProcessId = hostA.Runtimes[1].ExpectedServerProcessId },
                "swapped server PID").ConfigureAwait(false);

            KillProcess(hostA.Runtimes[0].ExpectedServerProcessId);
            await WaitForExitAsync(hostA.Runtimes[0].ExpectedServerProcessId).ConfigureAwait(false);

            await AssertConnectionSucceedsAsync(hostA.Runtimes[1], "after sibling shutdown").ConfigureAwait(false);
            await AssertConnectionSucceedsAsync(hostB.Runtimes[0], "host B runtime 1").ConfigureAwait(false);
            await AssertConnectionSucceedsAsync(hostB.Runtimes[1], "host B runtime 2").ConfigureAwait(false);
        }
        finally
        {
            await processA.StopAsync().ConfigureAwait(false);
            await processB.StopAsync().ConfigureAwait(false);
            TryDelete(root);
        }
    }

    private static async Task AssertConnectionFailsAsync(
        RuntimeEndpoint endpoint,
        string scenario)
    {
        await using var client = CreateClient(endpoint, $"negative-{Guid.NewGuid():N}");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await client.ConnectAsync().ConfigureAwait(false),
            scenario).ConfigureAwait(false);
    }

    private static async Task AssertConnectionSucceedsAsync(
        RuntimeEndpoint endpoint,
        string scenario)
    {
        await using var client = CreateClient(endpoint, $"positive-{Guid.NewGuid():N}");
        await client.ConnectAsync().ConfigureAwait(false);
        Assert.AreEqual(RuntimeClientState.Connected, client.State, scenario);
    }

    private static RuntimeClient CreateClient(RuntimeEndpoint endpoint, string hostInstanceId) =>
        new(new RuntimeClientOptions(
            endpoint.PipeName,
            hostInstanceId,
            ConnectTimeout: TimeSpan.FromSeconds(3),
            ExpectedRuntimeInstanceId: endpoint.ExpectedRuntimeInstanceId,
            HandshakeSecret: endpoint.Secret,
            ExpectedServerProcessId: endpoint.ExpectedServerProcessId));

    private static HostConfiguration CreateHostConfiguration(string root, string name)
    {
        var hostInstanceId = $"host-{name}-{Guid.NewGuid():N}";
        var runtimes = new RuntimeEndpoint[2];
        for (var index = 0; index < runtimes.Length; index++)
        {
            var instanceId = Guid.NewGuid().ToString("N");
            var pipePrefix = $"madorin.ai.runtime.e2e.{name}{index}.{Guid.NewGuid():N}";
            var workspace = Path.Combine(root, $"host-{name}", $"runtime-{index}");
            Directory.CreateDirectory(workspace);
            runtimes[index] = new RuntimeEndpoint(
                workspace,
                instanceId,
                pipePrefix,
                $"{pipePrefix}.{instanceId[..8]}",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                instanceId,
                0);
        }

        return new HostConfiguration(hostInstanceId, runtimes);
    }

    private static string FindArtifact(params string[] pathParts)
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = outputDirectory.Parent?.Name
            ?? throw new InvalidOperationException("The test build configuration could not be resolved.");
        var runtimeRoot = outputDirectory.Parent?.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("The Runtime repository root could not be resolved.");
        var parts = new List<string> { runtimeRoot };
        parts.AddRange(pathParts[..^1]);
        parts.Add("bin");
        parts.Add(configuration);
        parts.Add("net10.0");
        parts.Add(pathParts[^1]);
        var artifact = Path.Combine([.. parts]);
        return File.Exists(artifact)
            ? artifact
            : throw new FileNotFoundException("The process test artifact was not built.", artifact);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-stage2-multi-process",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void KillProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
    }

    private static async Task WaitForExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record HostConfiguration(
        string HostInstanceId,
        RuntimeEndpoint[] Runtimes);

    private sealed record RuntimeEndpoint(
        string Workspace,
        string InstanceId,
        string PipePrefix,
        string PipeName,
        string Secret,
        string ExpectedRuntimeInstanceId,
        int ExpectedServerProcessId);

    private sealed class HostProcess(
        string sampleHostPath,
        string runtimePath,
        HostConfiguration configuration) : IAsyncDisposable
    {
        private Process? _process;
        private Task<string>? _standardError;
        private int _stopped;

        public async Task StartAsync()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(sampleHostPath)
                    ?? throw new InvalidOperationException("The SampleHost directory is unavailable.")
            };
            startInfo.ArgumentList.Add(sampleHostPath);
            startInfo.ArgumentList.Add("multi-instance");
            startInfo.ArgumentList.Add("--runtime");
            startInfo.ArgumentList.Add(runtimePath);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(configuration.HostInstanceId);
            for (var index = 0; index < configuration.Runtimes.Length; index++)
            {
                var suffix = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var runtime = configuration.Runtimes[index];
                startInfo.ArgumentList.Add("--workspace-" + suffix);
                startInfo.ArgumentList.Add(runtime.Workspace);
                startInfo.ArgumentList.Add("--instance-" + suffix);
                startInfo.ArgumentList.Add(runtime.InstanceId);
                startInfo.ArgumentList.Add("--pipe-prefix-" + suffix);
                startInfo.ArgumentList.Add(runtime.PipePrefix);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The SampleHost process could not start.");
            _standardError = _process.StandardError.ReadToEndAsync();
            foreach (var runtime in configuration.Runtimes)
            {
                await _process.StandardInput.WriteLineAsync(runtime.Secret).ConfigureAwait(false);
            }

            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            var ready = await ReadLineAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var parts = ready.Split('|');
            if (parts is not ["READY", var hostId, var firstPid, var secondPid]
                || !string.Equals(hostId, configuration.HostInstanceId, StringComparison.Ordinal)
                || !int.TryParse(firstPid, out var processId1)
                || !int.TryParse(secondPid, out var processId2))
            {
                throw new InvalidDataException($"Unexpected SampleHost readiness line: {ready}");
            }

            configuration.Runtimes[0] = configuration.Runtimes[0] with
            {
                ExpectedServerProcessId = processId1
            };
            configuration.Runtimes[1] = configuration.Runtimes[1] with
            {
                ExpectedServerProcessId = processId2
            };
        }

        public async Task ReconnectAsync(int index)
        {
            var process = _process
                ?? throw new InvalidOperationException("The SampleHost process has not started.");
            await process.StandardInput.WriteLineAsync($"reconnect {index}").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            var response = await ReadLineAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Assert.AreEqual($"OK|reconnect|{index}", response);
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0 || _process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("exit").ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (_standardError is not null)
                {
                    _ = await _standardError.ConfigureAwait(false);
                }

                _process.Dispose();
                _process = null;
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

        private async Task<string> ReadLineAsync(TimeSpan timeout)
        {
            var process = _process
                ?? throw new InvalidOperationException("The SampleHost process has not started.");
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                return await process.StandardOutput.ReadLineAsync(cancellation.Token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException(
                        $"SampleHost exited before responding. stderr: {await GetStandardErrorAsync().ConfigureAwait(false)}");
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException("SampleHost did not respond within the expected time.", ex);
            }
        }

        private async Task<string> GetStandardErrorAsync()
        {
            if (_standardError is null || !_standardError.IsCompleted)
            {
                return string.Empty;
            }

            return await _standardError.ConfigureAwait(false);
        }
    }
}
