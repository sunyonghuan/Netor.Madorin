using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Process.Settings;

namespace Netor.Cortana.Plugin.Process.Logging;

/// <summary>
/// 文件日志注册扩展。
/// </summary>
public static class FileLoggerExtensions
{
    /// <summary>
    /// 注册插件文件日志，日志写入 <c>{DataDirectory}/logs/plugin.log</c>。
    /// <para>
    /// 由 Generator 生成的 <c>Program.g.cs</c> 默认调用此方法，
    /// 用户无需显式注册。<see cref="PluginSettings"/> 可用前的日志会被丢弃。
    /// </para>
    /// </summary>
    public static ILoggingBuilder AddProcessPluginFileLogger(
        this ILoggingBuilder builder,
        LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new FileLoggerProvider(
                sp.GetRequiredService<PluginSettingsAccessor>(),
                minimumLevel));

        return builder;
    }
}
