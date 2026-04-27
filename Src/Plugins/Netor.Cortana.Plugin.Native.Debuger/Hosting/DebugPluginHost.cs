using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Native.Debugger.Discovery;
using Netor.Cortana.Plugin.Native.Debugger.Invocation;

using System.Reflection;
using System.Text.Json.Nodes;

namespace Netor.Cortana.Plugin.Native.Debugger.Hosting;

/// <summary>
/// 调试宿主 - 负责加载插件、初始化上下文、提供工具调用能力
/// </summary>
public class DebugPluginHost : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ToolInvoker _toolInvoker;
    private readonly List<IHostedService> _hostedServices = [];
    private bool _disposed;

    /// <summary>插件元数据</summary>
    public PluginMetadata PluginMetadata { get; }

    /// <summary>工具注册表</summary>
    public ToolRegistry ToolRegistry { get; }

    /// <summary>插件上下文</summary>
    public IPluginContext Context { get; }

    /// <summary>
    /// 创建调试宿主
    /// </summary>
    public DebugPluginHost(Assembly pluginAssembly, DebugOptions? options = null, Action<IServiceCollection>? configureServices = null)
    {
        options ??= new DebugOptions();

        var context = new DebugPluginContext(
            options.DataDirectory,
            options.WorkspaceDirectory,
            options.WsPort);
        Context = context;

        PluginMetadata = PluginScanner.Scan(pluginAssembly);
        ToolRegistry = ToolScanner.Scan(pluginAssembly);

        var services = new ServiceCollection();

        services.AddSingleton<IPluginContext>(Context);
        services.AddSingleton(Context.LoggerFactory);
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient();

        // Auto-register PluginSettings from DebugOptions
        RegisterPluginSettings(services, options, context);

        // Call plugin's Configure method
        var configureMethod = PluginMetadata.PluginType.GetMethod("Configure",
            BindingFlags.Public | BindingFlags.Static);
        if (configureMethod != null)
            configureMethod.Invoke(null, [services]);

        // User-provided services
        configureServices?.Invoke(services);
        options.ConfigureServices?.Invoke(services);

        // Register all tool types as singletons
        var toolTypes = ToolRegistry.Tools
            .Select(t => t.Value.DeclaringType)
            .Distinct()
            .Where(t => t != null);

        foreach (var type in toolTypes!)
            services.AddSingleton(type!);

        _serviceProvider = services.BuildServiceProvider();
        _toolInvoker = new ToolInvoker(_serviceProvider, ToolRegistry);

        // Start all IHostedService instances registered by the plugin
        foreach (var hostedService in _serviceProvider.GetServices<IHostedService>())
        {
            _hostedServices.Add(hostedService);
            hostedService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static void RegisterPluginSettings(
        IServiceCollection services, DebugOptions options, DebugPluginContext context)
    {
        var json = new JsonObject
        {
            ["dataDirectory"] = options.DataDirectory ?? context.DataDirectory,
            ["workspaceDirectory"] = options.WorkspaceDirectory ?? context.WorkspaceDirectory,
            ["pluginDirectory"] = options.PluginDirectory ?? AppContext.BaseDirectory,
            ["wsPort"] = options.WsPort
        }.ToJsonString();

        services.AddSingleton(PluginSettings.FromJson(json));
    }

    /// <summary>从字符串参数调用工具</summary>
    public async Task<string> InvokeToolAsync(string toolName, string? argsOrJson = null)
    {
        EnsureNotDisposed();
        return await _toolInvoker.InvokeAsync(toolName, argsOrJson);
    }

    /// <summary>从预绑定参数调用工具（供交互模式使用）</summary>
    public async Task<string> InvokeToolAsync(string toolName, object[] boundArgs)
    {
        EnsureNotDisposed();
        return await _toolInvoker.InvokeAsync(toolName, boundArgs);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DebugPluginHost));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop all hosted services
        foreach (var hostedService in _hostedServices)
        {
            try { await hostedService.StopAsync(CancellationToken.None); }
            catch { /* ignore stop errors */ }
        }

        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}