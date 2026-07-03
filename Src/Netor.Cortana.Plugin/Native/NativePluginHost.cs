using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Plugin;

namespace Netor.Cortana.Plugin.Native;

/// <summary>
/// 原生通道的插件宿主。
/// 启动 NativeHost 子进程加载原生 DLL，通过 stdin/stdout JSON 协议通信。
/// </summary>
public sealed class NativePluginHost(
    string pluginDirectory,
    PluginManifest manifest,
    ILogger<NativePluginHost> logger,
    IPluginRuntimeConfigProvider runtimeConfigProvider)
    : ExternalProcessPluginHostBase(pluginDirectory, manifest, logger, runtimeConfigProvider)
{
    protected override ProcessStartInfo CreateProcessStartInfo()
    {
        var libraryPath = Path.Combine(PluginDirectory, Manifest.LibraryName!);

        if (!File.Exists(libraryPath))
            throw new FileNotFoundException($"原生 DLL 不存在：{libraryPath}", libraryPath);

        var hostExePath = GetHostExePath();

        if (!File.Exists(hostExePath))
            throw new FileNotFoundException($"NativeHost 可执行文件不存在：{hostExePath}", hostExePath);

        return new ProcessStartInfo
        {
            FileName = hostExePath,
            Arguments = $"\"{libraryPath}\"",
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

    /// <summary>
    /// 获取 NativeHost 可执行文件路径。
    /// 使用 Environment.ProcessPath 确保单文件发布模式下也能正确定位。
    /// </summary>
    private string GetHostExePath()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var exeName = OperatingSystem.IsWindows()
            ? AppBranding.NativeHostExecutableFileName
            : AppBranding.NativeHostAssemblyName;
        return Path.Combine(exeDir, exeName);
    }
}
