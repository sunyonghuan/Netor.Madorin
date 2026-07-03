using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.Plugin.PluginBus;
using Netor.Cortana.Plugin.Process.Logging;
using Netor.Cortana.Plugin.Process.Protocol;
using Netor.Cortana.Plugin.Process.Settings;

namespace Netor.Cortana.Plugin.Process.Hosting;

/// <summary>
/// Process 通道插件的消息循环执行器。
/// <para>
/// 由 Generator 生成的 <c>Program.g.cs</c> 调用 <see cref="RunAsync"/> 启动。
/// 核心职责：
/// <list type="bullet">
///   <item>构建 DI 容器（注入 <see cref="PluginSettingsAccessor"/>、文件日志、用户工具类）</item>
///   <item>从 stdin 读取单行 JSON 请求</item>
///   <item>按 <c>method</c> 分派到 get_info / init / invoke / destroy 四种处理器</item>
///   <item>捕获所有异常并以 <see cref="HostResponse.Fail"/> 形式返回给宿主</item>
///   <item>通过 stdout 写回响应，stderr 记录内部诊断</item>
/// </list>
/// </para>
/// </summary>
public static class ProcessPluginHost
{
    /// <summary>
    /// 启动消息循环。
    /// </summary>
    /// <param name="info">插件静态元数据，由 Generator 从 <c>[Plugin]</c> 和 <c>[Tool]</c> 提取。</param>
    /// <param name="invokers">工具路由字典，键为工具名，值为 Generator 生成的调用委托。</param>
    /// <param name="configure">可选的用户 DI 配置委托（对应 <c>MyPlugin.Configure</c>）。</param>
    /// <param name="registerTools">
    /// Generator 生成的工具类注册委托。内部调用 <c>services.AddScoped&lt;T&gt;()</c>，
    /// 使用泛型保证 AOT 安全（每个工具类的构造函数会被 trimmer 保留）。
    /// </param>
    /// <param name="input">输入流（默认 <see cref="Console.In"/>，测试时可注入）。</param>
    /// <param name="output">输出流（默认 <see cref="Console.Out"/>，测试时可注入）。</param>
    /// <returns>循环结束后完成（收到 destroy 或 stdin 关闭）。</returns>
    public static async Task RunAsync(
        PluginInfoData info,
        IReadOnlyDictionary<string, ToolInvoker> invokers,
        ConfigurePluginServices? configure,
        Action<IServiceCollection> registerTools,
        TextReader? input = null,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(invokers);
        ArgumentNullException.ThrowIfNull(registerTools);

        input ??= Console.In;
        output ??= Console.Out;

        var runtime = new PluginRuntimeState(info, configure, registerTools);

        // 序列化锁：stdout 每次只允许写一行，避免交错
        var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteStderrAsync($"读取 stdin 失败: {ex.Message}").ConfigureAwait(false);
                    break;
                }

                if (line is null)
                    break; // stdin 关闭，宿主已断开

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var shouldExit = await HandleLineAsync(
                    line, info, invokers, runtime, output, writeLock)
                    .ConfigureAwait(false);

                if (shouldExit)
                    break;
            }
        }
        finally
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ... (处理器在后续分段中添加)

    /// <summary>
    /// 构建 DI 容器：注册 <see cref="PluginSettingsAccessor"/>、
    /// 文件日志、工具类和用户自定义依赖。
    /// </summary>
    private static ServiceProvider BuildServiceProvider(
        PluginInfoData info,
        ConfigurePluginServices? configure,
        Action<IServiceCollection> registerTools,
        PluginSettings settings)
    {
        var services = new ServiceCollection();

        var accessor = new PluginSettingsAccessor();
        accessor.Set(settings);
        services.AddSingleton(accessor);
        services.AddSingleton(settings);
        services.AddSingleton(info);
        services.TryAddSingleton(new ProcessPluginRuntimeContext
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            Settings = settings
        });
        services.TryAddSingleton<ProcessPluginHostApplicationLifetime>();
        services.TryAddSingleton<IHostApplicationLifetime>(
            static provider => provider.GetRequiredService<ProcessPluginHostApplicationLifetime>());

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProcessPluginFileLogger(LogLevel.Information);
        });

        services.AddSingleton(static sp => new PluginBusClient(
            sp.GetRequiredService<PluginSettings>(),
            sp.GetRequiredService<PluginInfoData>().Id,
            sp.GetRequiredService<ILogger<PluginBusClient>>()));
        services.AddSingleton<IHostedService>(static sp => sp.GetRequiredService<PluginBusClient>());

        registerTools(services);

        configure?.Invoke(services, settings);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 处理单行请求。返回 <c>true</c> 表示应退出循环。
    /// </summary>
    private static async Task<bool> HandleLineAsync(
        string line,
        PluginInfoData info,
        IReadOnlyDictionary<string, ToolInvoker> invokers,
        PluginRuntimeState runtime,
        TextWriter output,
        SemaphoreSlim writeLock)
    {
        HostRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, ProcessProtocolJsonContext.Default.HostRequest);
        }
        catch (Exception ex)
        {
            runtime.Logger.LogError(ex, "解析请求 JSON 失败: {Line}", line);
            await WriteResponseAsync(output, writeLock, HostResponse.Fail($"parse error: {ex.Message}"))
                .ConfigureAwait(false);
            return false;
        }

        if (request is null)
        {
            await WriteResponseAsync(output, writeLock, HostResponse.Fail("empty request"))
                .ConfigureAwait(false);
            return false;
        }

        HostResponse response;
        bool exitAfter = false;

        try
        {
            switch (request.Method)
            {
                case "get_info":
                    response = HandleGetInfo(info);
                    break;

                case "init":
                    response = await runtime.InitializeAsync(request.Args).ConfigureAwait(false);
                    break;

                case "invoke":
                    response = await HandleInvokeAsync(request, invokers, runtime.Services, runtime.Logger)
                        .ConfigureAwait(false);
                    break;

                case "destroy":
                    response = HostResponse.Ok(null);
                    exitAfter = true;
                    runtime.Logger.LogInformation("收到 destroy 请求，插件准备退出");
                    break;

                default:
                    response = HostResponse.Fail($"unknown method: {request.Method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            runtime.Logger.LogError(ex, "处理 {Method} 时发生未捕获异常", request.Method);
            response = HostResponse.Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        await WriteResponseAsync(output, writeLock, response).ConfigureAwait(false);
        return exitAfter;
    }

    /// <summary>
    /// 处理 <c>get_info</c>：将静态元数据序列化为 JSON 放入 <see cref="HostResponse.Data"/>。
    /// </summary>
    private static HostResponse HandleGetInfo(PluginInfoData info)
    {
        var json = JsonSerializer.Serialize(info, ProcessProtocolJsonContext.Default.PluginInfoData);
        return HostResponse.Ok(json);
    }

    /// <summary>
    /// 处理 <c>init</c>：反序列化 <see cref="InitConfig"/>，注入到 <see cref="PluginSettingsAccessor"/>。
    /// </summary>
    private static HostResponse TryCreateSettings(string? argsJson, out PluginSettings? settings)
    {
        settings = null;

        if (string.IsNullOrEmpty(argsJson))
            return HostResponse.Fail("init args 缺失");

        var config = JsonSerializer.Deserialize(argsJson, ProcessProtocolJsonContext.Default.InitConfig);
        if (config is null)
            return HostResponse.Fail("init args 反序列化失败");

        settings = new PluginSettings(
            dataDirectory: config.DataDirectory,
            workspaceDirectory: config.WorkspaceDirectory,
            pluginDirectory: config.PluginDirectory,
            wsPort: config.WsPort,
            chatWsEndpoint: GetExtension(config.Extensions, "pluginBusEndpoint", "chatWsEndpoint"),
            conversationFeedEndpoint: GetExtension(config.Extensions, "pluginBusEndpoint", "conversationFeedEndpoint"),
            conversationFeedProtocol: GetExtension(config.Extensions, "pluginBusProtocol", "conversationFeedProtocol"),
            conversationFeedVersion: GetExtension(config.Extensions, "pluginBusVersion", "conversationFeedVersion"),
            conversationFeedPort: GetExtensionInt32(config.Extensions, "pluginBusPort", "conversationFeedPort"),
            extensions: config.Extensions);

        return HostResponse.Ok(null);
    }

    private static string GetExtension(IReadOnlyDictionary<string, string>? extensions, params string[] names)
    {
        if (extensions is null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (extensions.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static int GetExtensionInt32(IReadOnlyDictionary<string, string>? extensions, params string[] names)
    {
        if (extensions is null)
        {
            return 0;
        }

        foreach (var name in names)
        {
            if (extensions.TryGetValue(name, out var value) && int.TryParse(value, out var number))
            {
                return number;
            }
        }

        return 0;
    }

    /// <summary>
    /// 处理 <c>invoke</c>：按工具名查路由，解析参数 JSON，调用委托。
    /// 每次调用创建一个 Scope，确保工具类实例的生命周期独立。
    /// </summary>
    private static async ValueTask<HostResponse> HandleInvokeAsync(
        HostRequest request,
        IReadOnlyDictionary<string, ToolInvoker> invokers,
        IServiceProvider? rootServices,
        ILogger logger)
    {
        if (rootServices is null)
            return HostResponse.Fail("插件尚未初始化，请先调用 init");

        if (string.IsNullOrEmpty(request.ToolName))
            return HostResponse.Fail("invoke 缺少 toolName");

        if (!invokers.TryGetValue(request.ToolName, out var invoker))
            return HostResponse.Fail($"未知工具: {request.ToolName}");

        JsonElement args;
        if (string.IsNullOrEmpty(request.Args))
        {
            using var empty = JsonDocument.Parse("{}");
            args = empty.RootElement.Clone();
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(request.Args);
                args = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "解析工具 {Tool} 的参数 JSON 失败", request.ToolName);
                return HostResponse.Fail($"args JSON 解析失败: {ex.Message}");
            }
        }

        var scope = rootServices.CreateAsyncScope();
        await using var _ = scope.ConfigureAwait(false);

        try
        {
            var result = await invoker(scope.ServiceProvider, args).ConfigureAwait(false);
            return HostResponse.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "工具 {Tool} 执行失败", request.ToolName);
            return HostResponse.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 串行写入响应，确保单行 JSON 完整输出。
    /// </summary>
    private static async Task WriteResponseAsync(
        TextWriter output,
        SemaphoreSlim writeLock,
        HostResponse response)
    {
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(response, ProcessProtocolJsonContext.Default.HostResponse);
            await output.WriteLineAsync(json).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static Task WriteStderrAsync(string message)
        => Console.Error.WriteLineAsync(message);

    private sealed class PluginRuntimeState(
        PluginInfoData info,
        ConfigurePluginServices? configure,
        Action<IServiceCollection> registerTools) : IAsyncDisposable
    {
        private ServiceProvider? _services;
        private HostedServiceLifecycle? _hostedServiceLifecycle;

        public IServiceProvider? Services => _services;

        public ILogger Logger { get; private set; } = NullLogger.Instance;

        public async Task<HostResponse> InitializeAsync(string? argsJson)
        {
            if (_services is not null)
                return HostResponse.Fail("插件已经初始化");

            var response = TryCreateSettings(argsJson, out var settings);
            if (!response.Success)
                return response;

            ServiceProvider? services = null;
            HostedServiceLifecycle? hostedServiceLifecycle = null;

            try
            {
                services = BuildServiceProvider(info, configure, registerTools, settings!);
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ProcessPluginHost");
                hostedServiceLifecycle = new HostedServiceLifecycle(services, logger);
                await hostedServiceLifecycle.StartAsync(CancellationToken.None).ConfigureAwait(false);

                _services = services;
                _hostedServiceLifecycle = hostedServiceLifecycle;
                Logger = logger;
            }
            catch (Exception ex)
            {
                await LogInitializationFailureAsync(services, ex).ConfigureAwait(false);

                if (hostedServiceLifecycle is not null)
                {
                    await hostedServiceLifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (services is not null)
                {
                    await services.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

            Logger.LogInformation(
                "插件初始化完成: DataDir={DataDir}, Workspace={Workspace}, PluginDir={PluginDir}, WsPort={WsPort}",
                settings!.DataDirectory, settings.WorkspaceDirectory, settings.PluginDirectory, settings.WsPort);

            return HostResponse.Ok(null);
        }

        private static async Task LogInitializationFailureAsync(ServiceProvider? services, Exception exception)
        {
            try
            {
                var logger = services?
                    .GetService<ILoggerFactory>()?
                    .CreateLogger("ProcessPluginHost.Init");

                if (logger is not null)
                {
                    logger.LogError(exception, "插件初始化失败");
                    return;
                }
            }
            catch
            {
                // 初始化诊断不能遮蔽真正的初始化异常。
            }

            await WriteStderrAsync($"插件初始化失败: {exception}").ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_hostedServiceLifecycle is not null)
            {
                await _hostedServiceLifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _hostedServiceLifecycle = null;
            }

            if (_services is not null)
            {
                await _services.DisposeAsync().ConfigureAwait(false);
                _services = null;
            }
        }
    }

    private sealed class ProcessPluginHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public CancellationToken ApplicationStarted => _startedCts.Token;

        public CancellationToken ApplicationStopping => _stoppingCts.Token;

        public CancellationToken ApplicationStopped => _stoppedCts.Token;

        public void StopApplication()
            => TryCancel(_stoppingCts);

        public Task NotifyStartedAsync()
            => TryCancelAsync(_startedCts);

        public Task NotifyStoppingAsync()
            => TryCancelAsync(_stoppingCts);

        public Task NotifyStoppedAsync()
            => TryCancelAsync(_stoppedCts);

        public void Dispose()
        {
            _startedCts.Dispose();
            _stoppingCts.Dispose();
            _stoppedCts.Dispose();
        }

        private static Task TryCancelAsync(CancellationTokenSource source)
        {
            try
            {
                return source.IsCancellationRequested
                    ? Task.CompletedTask
                    : source.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                return Task.CompletedTask;
            }
        }

        private static void TryCancel(CancellationTokenSource source)
        {
            try
            {
                if (!source.IsCancellationRequested)
                {
                    source.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // 容器释放后重复通知时保持兼容，不再向外抛出。
            }
        }
    }

    private sealed class HostedServiceLifecycle(IServiceProvider services, ILogger logger)
    {
        private readonly ProcessPluginHostApplicationLifetime? _applicationLifetime =
            services.GetService<IHostApplicationLifetime>() as ProcessPluginHostApplicationLifetime;
        private readonly List<IHostedService> _startedServices = [];
        private readonly List<IHostedLifecycleService> _startedLifecycleServices = [];
        private bool _started;
        private bool _stoppingNotified;
        private bool _stoppedNotified;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_started)
                return;

            var hostedServices = services.GetServices<IHostedService>().ToList();
            var lifecycleServices = hostedServices.OfType<IHostedLifecycleService>().ToList();

            try
            {
                foreach (var lifecycleService in lifecycleServices)
                {
                    await lifecycleService.StartingAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach (var hostedService in hostedServices)
                {
                    await hostedService.StartAsync(cancellationToken).ConfigureAwait(false);
                    _startedServices.Add(hostedService);
                }

                _startedLifecycleServices.AddRange(_startedServices.OfType<IHostedLifecycleService>());
                foreach (var lifecycleService in _startedLifecycleServices)
                {
                    await lifecycleService.StartedAsync(cancellationToken).ConfigureAwait(false);
                }

                _started = true;
                await NotifyStartedAsync().ConfigureAwait(false);
                logger.LogInformation("已启动 {Count} 个后台服务", _startedServices.Count);
            }
            catch
            {
                await StopAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_started && _startedServices.Count == 0 && _startedLifecycleServices.Count == 0)
            {
                return;
            }

            await NotifyStoppingAsync().ConfigureAwait(false);
            await StopLifecycleServicesStoppingAsync(cancellationToken).ConfigureAwait(false);
            await StopStartedServicesAsync(cancellationToken).ConfigureAwait(false);
            await StopLifecycleServicesStoppedAsync(cancellationToken).ConfigureAwait(false);
            await NotifyStoppedAsync().ConfigureAwait(false);
            _started = false;
        }

        private async Task NotifyStartedAsync()
        {
            if (_applicationLifetime is not null)
            {
                await _applicationLifetime.NotifyStartedAsync().ConfigureAwait(false);
            }
        }

        private async Task NotifyStoppingAsync()
        {
            if (_stoppingNotified)
                return;

            _stoppingNotified = true;
            try
            {
                if (_applicationLifetime is not null)
                {
                    await _applicationLifetime.NotifyStoppingAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知 ApplicationStopping 失败");
            }
        }

        private async Task NotifyStoppedAsync()
        {
            if (_stoppedNotified)
                return;

            _stoppedNotified = true;
            try
            {
                if (_applicationLifetime is not null)
                {
                    await _applicationLifetime.NotifyStoppedAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "通知 ApplicationStopped 失败");
            }
        }

        private async Task StopLifecycleServicesStoppingAsync(CancellationToken cancellationToken)
        {
            for (var i = _startedLifecycleServices.Count - 1; i >= 0; i--)
            {
                var lifecycleService = _startedLifecycleServices[i];
                try
                {
                    await lifecycleService.StoppingAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "执行后台服务 {ServiceType} 的 StoppingAsync 失败", lifecycleService.GetType().FullName);
                }
            }
        }

        private async Task StopLifecycleServicesStoppedAsync(CancellationToken cancellationToken)
        {
            for (var i = _startedLifecycleServices.Count - 1; i >= 0; i--)
            {
                var lifecycleService = _startedLifecycleServices[i];
                try
                {
                    await lifecycleService.StoppedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "执行后台服务 {ServiceType} 的 StoppedAsync 失败", lifecycleService.GetType().FullName);
                }
            }

            _startedLifecycleServices.Clear();
        }

        private async Task StopStartedServicesAsync(CancellationToken cancellationToken)
        {
            for (var i = _startedServices.Count - 1; i >= 0; i--)
            {
                var hostedService = _startedServices[i];
                try
                {
                    await hostedService.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "停止后台服务 {ServiceType} 失败", hostedService.GetType().FullName);
                }
            }

            logger.LogInformation("已停止 {Count} 个后台服务", _startedServices.Count);
            _startedServices.Clear();
        }
    }
}
