using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Native;

namespace Netor.Cortana.Plugin.Process;

/// <summary>
/// 进程通道的插件宿主。
/// 直接启动插件 exe（JIT 或 AOT），通过 stdin/stdout JSON 协议通信。
/// 插件无需 Native AOT 发布，以普通 exe 形式分发即可。
/// </summary>
public sealed class ProcessPluginHost(
    string pluginDirectory,
    PluginManifest manifest,
    ILogger<ProcessPluginHost> logger,
    IPluginRuntimeConfigProvider runtimeConfigProvider)
    : ExternalProcessPluginHostBase(pluginDirectory, manifest, logger, runtimeConfigProvider)
{
    protected override ProcessStartInfo CreateProcessStartInfo()
    {
        var exePath = Path.Combine(PluginDirectory, Manifest.Command!);

        if (!File.Exists(exePath))
            throw new FileNotFoundException($"插件可执行文件不存在：{exePath}", exePath);

        return new ProcessStartInfo
        {
            FileName = exePath,
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
}
