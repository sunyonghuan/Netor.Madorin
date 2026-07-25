using System.Diagnostics;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneCliProcessStage7Tests
{
    private readonly TestContext _testContext;

    public StandaloneCliProcessStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task ServeProcesses_WorkspaceIsolationTerminationAndLockRelease_RemainScoped()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstWorkspace = Path.Combine(root, "workspace-a");
            var secondWorkspace = Path.Combine(root, "workspace-b");
            var firstData = Path.Combine(root, "data-a");
            var secondData = Path.Combine(root, "data-b");
            Directory.CreateDirectory(firstWorkspace);
            Directory.CreateDirectory(secondWorkspace);

            await using var first = await CliServeProcess.StartAsync(
                firstWorkspace,
                firstData,
                _testContext.CancellationToken);
            await using var second = await CliServeProcess.StartAsync(
                secondWorkspace,
                secondData,
                _testContext.CancellationToken);

            Assert.IsFalse(first.HasExited);
            Assert.IsFalse(second.HasExited);

            var competitor = await RunServeToExitAsync(
                firstWorkspace,
                Path.Combine(root, "data-a-competitor"),
                _testContext.CancellationToken);
            Assert.AreEqual(ExitCodes.WorkspaceError, competitor.ExitCode, competitor.CombinedOutput);
            Assert.Contains("active Runtime instance", competitor.CombinedOutput, StringComparison.Ordinal);
            Assert.Contains("runtimeInstanceId=", competitor.CombinedOutput, StringComparison.Ordinal);
            Assert.IsFalse(first.HasExited);
            Assert.IsFalse(second.HasExited);

            await first.TerminateAsync();
            Assert.IsTrue(first.HasExited);
            Assert.IsFalse(second.HasExited);

            await using var replacement = await CliServeProcess.StartAsync(
                firstWorkspace,
                Path.Combine(root, "data-a-replacement"),
                _testContext.CancellationToken);
            Assert.IsFalse(replacement.HasExited);
            Assert.IsFalse(second.HasExited);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task PathDiscovery_NewCommandShell_ExecutesMadorinByName()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("PATH command discovery is a Windows stage 7 acceptance test.");
        }

        var root = CreateTemporaryDirectory();
        try
        {
            var executableDirectory = AppContext.BaseDirectory;
            var executablePath = Path.Combine(executableDirectory, "madorin.exe");
            Assert.IsTrue(File.Exists(executablePath), $"CLI apphost not found: {executablePath}");

            var startInfo = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("madorin version --json");
            startInfo.Environment["PATH"] = string.Join(
                Path.PathSeparator,
                executableDirectory,
                Environment.GetEnvironmentVariable("PATH"));

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The PATH discovery shell could not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(_testContext.CancellationToken);
            var output = await standardOutput;
            var error = await standardError;

            Assert.AreEqual(ExitCodes.Success, process.ExitCode, $"{output}\n{error}");
            Assert.AreEqual(string.Empty, error);
            using var document = JsonDocument.Parse(output);
            var rootElement = document.RootElement;
            Assert.IsTrue(rootElement.GetProperty("success").GetBoolean());
            var data = rootElement.GetProperty("data");
            Assert.AreEqual("Madorin.AI.Runtime", data.GetProperty("product").GetString());
            Assert.AreEqual("1.1", data.GetProperty("protocolVersion").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task<ProcessResult> RunServeToExitAsync(
        string workspace,
        string dataDirectory,
        CancellationToken ct)
    {
        using var process = StartServeProcess(workspace, dataDirectory);
        var standardOutput = process.StandardOutput.ReadToEndAsync(ct);
        var standardError = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteLineAsync(Guid.NewGuid().ToString("N").AsMemory(), ct);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static Process StartServeProcess(string workspace, string dataDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workspace
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "madorin.dll"));
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(Guid.NewGuid().ToString("N"));
        startInfo.ArgumentList.Add("--pipe-prefix");
        startInfo.ArgumentList.Add($"madorin.ai.runtime.e2e.{Guid.NewGuid():N}");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The madorin serve process could not start.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-stage7-cli-process",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => $"{StandardOutput}\n{StandardError}";
    }

    private sealed class CliServeProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;
        private int _terminated;

        private CliServeProcess(Process process)
        {
            _process = process;
            _standardOutput = process.StandardOutput.ReadToEndAsync();
            _standardError = process.StandardError.ReadToEndAsync();
        }

        public bool HasExited => _process.HasExited;

        public static async Task<CliServeProcess> StartAsync(
            string workspace,
            string dataDirectory,
            CancellationToken ct)
        {
            var process = StartServeProcess(workspace, dataDirectory);
            var instance = new CliServeProcess(process);
            try
            {
                await process.StandardInput.WriteLineAsync(
                    Guid.NewGuid().ToString("N").AsMemory(),
                    ct);
                process.StandardInput.Close();
                await instance.WaitUntilReadyAsync(dataDirectory, ct);
                return instance;
            }
            catch
            {
                await instance.DisposeAsync();
                throw;
            }
        }

        public async Task TerminateAsync()
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
            {
                return;
            }

            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync();
            await Task.WhenAll(_standardOutput, _standardError);
        }

        public async ValueTask DisposeAsync()
        {
            await TerminateAsync();
            _process.Dispose();
        }

        private async Task WaitUntilReadyAsync(string dataDirectory, CancellationToken ct)
        {
            var pidPath = Path.Combine(dataDirectory, "runtime.pid");
            while (!File.Exists(pidPath))
            {
                if (_process.HasExited)
                {
                    var output = await _standardOutput;
                    var error = await _standardError;
                    throw new InvalidOperationException(
                        $"madorin serve exited before becoming ready ({_process.ExitCode}).\n{output}\n{error}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }
    }
}
