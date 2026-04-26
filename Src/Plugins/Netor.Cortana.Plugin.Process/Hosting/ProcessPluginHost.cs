using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // 构建 DI 容器
        var services = BuildServiceProvider(configure, registerTools);
        await using var _ = services.ConfigureAwait(false);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ProcessPluginHost");
        var accessor = services.GetRequiredService<PluginSettingsAccessor>();

        // 序列化锁：stdout 每次只允许写一行，避免交错
        var writeLock = new SemaphoreSlim(1, 1);

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
                line, info, invokers, services, accessor, output, writeLock, logger)
                .ConfigureAwait(false);

            if (shouldExit)
                break;
        }
    }

    // ... (处理器在后续分段中添加)

    /// <summary>
    /// 构建 DI 容器：注册 <see cref="PluginSettingsAccessor"/>、
    /// 文件日志、工具类和用户自定义依赖。
    /// </summary>
    private static ServiceProvider BuildServiceProvider(
        ConfigurePluginServices? configure,
        Action<IServiceCollection> registerTools)
    {
        var services = new ServiceCollection();

        // 配置访问桥接（init 前未初始化，工具类解析时才读取 Value）
        var accessor = new PluginSettingsAccessor();
        services.AddSingleton(accessor);
        services.AddSingleton<PluginSettings>(_ => accessor.Value);

        // 文件日志（init 之前写的日志会被丢弃）
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProcessPluginFileLogger(LogLevel.Information);
        });

        // 注册所有工具类（Generator 生成的委托内部使用 AddScoped<T>()，AOT 安全）
        registerTools(services);

        // 用户扩展：AddHttpClient 等
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 处理单行请求。返回 <c>true</c> 表示应退出循环。
    /// </summary>
    private static async Task<bool> HandleLineAsync(
        string line,
        PluginInfoData info,
        IReadOnlyDictionary<string, ToolInvoker> invokers,
        IServiceProvider rootServices,
        PluginSettingsAccessor accessor,
        TextWriter output,
        SemaphoreSlim writeLock,
        ILogger logger)
    {
        HostRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, ProcessProtocolJsonContext.Default.HostRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析请求 JSON 失败: {Line}", line);
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
                    response = HandleInit(request.Args, accessor, logger);
                    break;

                case "invoke":
                    response = await HandleInvokeAsync(request, invokers, rootServices, logger)
                        .ConfigureAwait(false);
                    break;

                case "destroy":
                    response = HostResponse.Ok(null);
                    exitAfter = true;
                    logger.LogInformation("收到 destroy 请求，插件准备退出");
                    break;

                default:
                    response = HostResponse.Fail($"unknown method: {request.Method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 {Method} 时发生未捕获异常", request.Method);
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
    private static HostResponse HandleInit(string? argsJson, PluginSettingsAccessor accessor, ILogger logger)
    {
        if (string.IsNullOrEmpty(argsJson))
            return HostResponse.Fail("init args 缺失");

        var config = JsonSerializer.Deserialize(argsJson, ProcessProtocolJsonContext.Default.InitConfig);
        if (config is null)
            return HostResponse.Fail("init args 反序列化失败");

        var settings = new PluginSettings(
            dataDirectory: config.DataDirectory,
            workspaceDirectory: config.WorkspaceDirectory,
            pluginDirectory: config.PluginDirectory,
            wsPort: config.WsPort,
            chatWsEndpoint: GetExtension(config.Extensions, "chatWsEndpoint"),
            conversationFeedEndpoint: GetExtension(config.Extensions, "conversationFeedEndpoint"),
            conversationFeedProtocol: GetExtension(config.Extensions, "conversationFeedProtocol"),
            conversationFeedVersion: GetExtension(config.Extensions, "conversationFeedVersion"),
            conversationFeedPort: GetExtensionInt32(config.Extensions, "conversationFeedPort"),
            extensions: config.Extensions);

        accessor.Set(settings);
        logger.LogInformation(
            "插件初始化完成: DataDir={DataDir}, Workspace={Workspace}, PluginDir={PluginDir}, WsPort={WsPort}",
            config.DataDirectory, config.WorkspaceDirectory, config.PluginDirectory, config.WsPort);

        return HostResponse.Ok(null);
    }

    private static string GetExtension(IReadOnlyDictionary<string, string>? extensions, string name)
    {
        return extensions is not null && extensions.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private static int GetExtensionInt32(IReadOnlyDictionary<string, string>? extensions, string name)
    {
        return extensions is not null && extensions.TryGetValue(name, out var value) && int.TryParse(value, out var number) ? number : 0;
    }

    /// <summary>
    /// 处理 <c>invoke</c>：按工具名查路由，解析参数 JSON，调用委托。
    /// 每次调用创建一个 Scope，确保工具类实例的生命周期独立。
    /// </summary>
    private static async ValueTask<HostResponse> HandleInvokeAsync(
        HostRequest request,
        IReadOnlyDictionary<string, ToolInvoker> invokers,
        IServiceProvider rootServices,
        ILogger logger)
    {
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
}
