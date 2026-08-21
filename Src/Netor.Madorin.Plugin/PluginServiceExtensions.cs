using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Netor.Madorin.Plugin.BuiltIn.FileBrowser;
using Netor.Madorin.Plugin.BuiltIn.PowerShell;
using Netor.Madorin.Plugin.Voice;

namespace Netor.Madorin.Plugin;

/// <summary>
/// Plugin 模块的 DI 扩展方法
/// </summary>
public static class PluginServiceExtensions
{
    /// <summary>
    /// 注册 Plugin 模块的所有服务
    /// </summary>
    public static IServiceCollection AddMadorinPlugin(this IServiceCollection services)
    {
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginManifestRegistry>();
        services.AddSingleton<IPluginConfigValueProtector, PluginConfigValueProtector>();
        services.AddSingleton<IPluginRuntimeConfigProvider, PluginRuntimeConfigProvider>();
        services.AddTransient<VoiceCapabilityRegistry>();

        // 内置 Provider
        services.AddSingleton<AIContextProvider, PowerShellProvider>();
        services.AddSingleton<AIContextProvider, FileBrowserProvider>();
        services.AddSingleton<AIContextProvider, FileOperationProvider>();

        // 辅助服务
        services.AddSingleton<PowerShellExecutor>();
        services.AddSingleton<PowerShellOutputBridge>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<FileBrowser>();
        services.AddSingleton<FileOperator>();

        return services;
    }
}
