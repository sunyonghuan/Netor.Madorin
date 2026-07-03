using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Voice;

/// <summary>
/// Voice 模块 DI 注册扩展方法。
/// </summary>
public static class VoiceServiceExtensions
{
    /// <summary>
    /// 注册 Voice 模块所有服务到 DI 容器。
    /// </summary>
    public static IServiceCollection AddCortanaVoice(this IServiceCollection services)
    {
        services.AddSingleton<TtsPluginAdapter>();
        services.AddSingleton<ITtsPluginAdapter>(sp => sp.GetRequiredService<TtsPluginAdapter>());
        services.AddSingleton<SttPluginAdapter>();
        services.AddSingleton<ISttPluginAdapter>(sp => sp.GetRequiredService<SttPluginAdapter>());
        services.AddSingleton<KwsPluginAdapter>();
        services.AddSingleton<IKwsPluginAdapter>(sp => sp.GetRequiredService<KwsPluginAdapter>());
        services.AddSingleton<TtsPluginOutputChannel>();
        services.AddSingleton<IAiOutputChannel>(sp => sp.GetRequiredService<TtsPluginOutputChannel>());

        services.AddSingleton<IVoiceCoordinator, VoiceCoordinator>();
        services.AddSingleton<VoicePipelineCoordinator>();

        services.AddSingleton<VoiceInputChannel>();
        services.AddSingleton<IAiInputChannel>(sp => sp.GetRequiredService<VoiceInputChannel>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<VoiceInputChannel>());

        return services;
    }
}
