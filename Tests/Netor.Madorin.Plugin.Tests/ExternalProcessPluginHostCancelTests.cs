using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Madorin.Plugin.Native;

namespace Netor.Madorin.Plugin.Tests;

[TestClass]
public sealed class ExternalProcessPluginHostCancelTests
{
    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task SendRequestAsync_WhenCancelled_KillsProcess_AndNextCallDoesNotReadStaleLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "madorin-plugin-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "slow-echo.ps1");
            await File.WriteAllTextAsync(scriptPath, """
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                  Start-Sleep -Seconds 8
                  [Console]::Out.WriteLine('{"success":true,"data":"late"}')
                  [Console]::Out.Flush()
                }
                """);

            var manifest = new PluginManifest
            {
                Id = "slow.echo",
                Name = "Slow Echo",
                Version = "1.0.0",
                Runtime = PluginRuntime.Process
            };
            using var host = new ScriptProcessHost(root, manifest, scriptPath);

            host.StartAttachedProcess();
            Assert.IsTrue(host.IsProcessAlive);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => host.SendRequestAsync(
                    new NativeHostRequest { Method = NativeHostMethods.Invoke, ToolName = "echo", Args = "{}" },
                    cts.Token));

            Assert.IsFalse(host.IsProcessAlive);

            var second = await host.SendRequestAsync(
                new NativeHostRequest { Method = NativeHostMethods.Invoke, ToolName = "echo", Args = "{}" });
            Assert.IsFalse(second.Success);
            Assert.AreNotEqual("late", second.Data);
            StringAssert.Contains(second.Error, "已退出");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ScriptProcessHost : ExternalProcessPluginHostBase
    {
        private readonly string _scriptPath;

        public ScriptProcessHost(string pluginDirectory, PluginManifest manifest, string scriptPath)
            : base(pluginDirectory, manifest, NullLogger<ScriptProcessHost>.Instance, new StubConfig())
        {
            _scriptPath = scriptPath;
        }

        public new void StartAttachedProcess() => base.StartAttachedProcess();

        protected override ProcessStartInfo CreateProcessStartInfo() => new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -File \"{_scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = PluginDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private sealed class StubConfig : IPluginRuntimeConfigProvider
    {
        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest) => "{}";

        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest) => "{}";
    }
}
