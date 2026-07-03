using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Plugin;

namespace Netor.Cortana.Plugin.Process.Hosting;

/// <summary>
/// 用户在 <c>[Plugin] partial class</c> 中可选实现的 DI 配置方法。
/// <para>
/// 示例：
/// <code>
/// static partial void Configure(IServiceCollection services, PluginSettings settings)
/// {
///     services.AddHttpClient();
///     services.AddSingleton&lt;IMyService&gt;(new MyService(settings.DataDirectory));
/// }
/// </code>
/// </para>
/// 由 Generator 在 <c>Program.g.cs</c> 中以如下形式调用：
/// <code>
/// ConfigurePluginServices configure = MyPlugin.Configure;
/// configure?.Invoke(services, settings);
/// </code>
/// <para>
/// 为兼容旧插件，Generator 仍支持 <c>Configure(IServiceCollection)</c>，
/// 并自动包装成忽略 <see cref="PluginSettings"/> 的委托。
/// </para>
/// </summary>
public delegate void ConfigurePluginServices(IServiceCollection services, PluginSettings settings);
