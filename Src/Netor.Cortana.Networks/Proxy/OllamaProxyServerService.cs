using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys.Proxy;

using System.Net;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 本地协议兼容代理服务。
/// 仅负责 HTTP 监听、Ollama 协议路由和响应写回；AI 调用通过独立 Proxy Agent 后端完成。
/// </summary>
/// <remarks>
/// 该服务实现 <see cref="IHostedService"/> 接口，作为后台服务运行。
/// 通过 <see cref="HttpListener"/> 监听本地 HTTP 请求，模拟 Ollama API 协议，
/// 将请求路由到不同的 AI 后端（如 ProxyAgent、本地模型等）。
/// 支持并发控制、状态管理和优雅关闭。
/// </remarks>
public sealed class OllamaProxyServerService(
    ILogger<OllamaProxyServerService> logger,
    OllamaProxyOptionsReader optionsReader,
    ProxyRouteDispatcher routeDispatcher) : IHostedService, IDisposable
{
    /// <summary>
    /// 状态同步锁，保护所有状态字段的读写操作。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// HTTP 监听器实例，负责接收客户端请求。
    /// </summary>
    private HttpListener? _listener;

    /// <summary>
    /// 取消令牌源，用于优雅停止监听和请求处理。
    /// </summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 监听线程，运行 HTTP Accept 循环。
    /// </summary>
    private Thread? _thread;

    /// <summary>
    /// 请求并发限制器，控制同时处理的请求数量。
    /// </summary>
    private SemaphoreSlim _requestLimiter = new(2, 2);

    /// <summary>
    /// 当前代理服务运行状态。
    /// </summary>
    private OllamaProxyStatus _status = OllamaProxyStatus.Stopped;

    /// <summary>
    /// 最后一次错误信息，用于状态快照和 UI 展示。
    /// </summary>
    private string _lastError = string.Empty;

    /// <summary>
    /// 服务启动时间戳，用于计算运行时长。
    /// </summary>
    private DateTimeOffset? _startedAt;

    /// <summary>
    /// 当前生效的配置快照，启动时从 <see cref="optionsReader"/> 读取。
    /// </summary>
    private AiProxyOptionsSnapshot _options = new(false, "localhost", 11434, AiProxyMode.ProxyAgent, null, null, null, true, false, false, 2);

    /// <summary>
    /// 状态变化事件。
    /// </summary>
    /// <remarks>
    /// 当代理服务状态发生变化时触发，UI 层可订阅此事件更新显示。
    /// </remarks>
    public event Action? StateChanged;

    /// <summary>
    /// 获取代理服务是否正在运行。
    /// </summary>
    public bool IsRunning => _status == OllamaProxyStatus.Running;

    /// <summary>
    /// 获取当前监听端口。
    /// </summary>
    public int Port => _options.Port;

    /// <summary>
    /// 获取当前监听主机地址。
    /// </summary>
    public string Host => _options.Host;

    /// <summary>
    /// 获取当前代理服务的完整状态快照。
    /// </summary>
    /// <returns>包含状态、地址、错误信息等在内的快照对象。</returns>
    /// <remarks>
    /// 此方法是线程安全的，通过 <see cref="_gate"/> 锁保护状态读取。
    /// 快照对象用于 UI 展示和状态查询，不会影响服务运行。
    /// </remarks>
    public OllamaProxyStateSnapshot GetStateSnapshot()
    {
        lock (_gate)
        {
            return new OllamaProxyStateSnapshot(
                _status,
                _options.Host,
                _options.Port,
                BuildDisplayUrl(_options),
                _status == OllamaProxyStatus.Running,
                _lastError,
                _startedAt,
                DateTimeOffset.Now);
        }
    }

    /// <summary>
    /// 作为 IHostedService 的启动入口，由宿主框架调用。
    /// </summary>
    /// <param name="cancellationToken">宿主传入的取消令牌。</param>
    /// <returns>启动任务。</returns>
    /// <remarks>
    /// 如果配置中未启用代理（Enabled=false），则直接标记为 Disabled 状态并返回。
    /// 否则调用 <see cref="StartProxyAsync"/> 启动监听。
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = optionsReader.Read();
        if (!options.Enabled)
        {
            SetState(OllamaProxyStatus.Disabled, string.Empty, null);
            return Task.CompletedTask;
        }

        return StartProxyAsync(cancellationToken);
    }

    /// <summary>
    /// 作为 IHostedService 的停止入口，由宿主框架调用。
    /// </summary>
    /// <param name="cancellationToken">宿主传入的取消令牌。</param>
    /// <returns>停止任务。</returns>
    public Task StopAsync(CancellationToken cancellationToken) => StopProxyAsync(cancellationToken);

    /// <summary>
    /// 启动代理监听。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，用于取消启动过程。</param>
    /// <returns>启动任务。</returns>
    /// <remarks>
    /// 该方法执行以下操作：
    /// 1. 读取最新配置并初始化并发限制器
    /// 2. 创建取消令牌源用于后续停止
    /// 3. 启动后台监听线程运行 <see cref="RunListenerThread"/>
    /// 4. 触发状态变化事件
    /// 如果服务已经在运行或启动中，则直接返回。
    /// </remarks>
    public Task StartProxyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_status is OllamaProxyStatus.Running or OllamaProxyStatus.Starting)
            {
                return Task.CompletedTask;
            }

            // 读取最新配置
            _options = optionsReader.Read();
            // 重新创建并发限制器，使用配置中的最大并发数
            _requestLimiter.Dispose();
            _requestLimiter = new SemaphoreSlim(_options.MaxConcurrentRequests, _options.MaxConcurrentRequests);
            // 创建链接到外部取消令牌的令牌源
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetStateCore(OllamaProxyStatus.Starting, string.Empty, null);

            // 创建后台线程运行 HTTP 监听循环
            _thread = new Thread(() => RunListenerThread(_cts.Token))
            {
                IsBackground = true,
                Name = "OllamaProxyServer"
            };
            _thread.Start();
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止代理监听。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（此方法当前未使用）。</param>
    /// <returns>停止任务。</returns>
    /// <remarks>
    /// 该方法执行以下操作：
    /// 1. 取消所有正在进行的请求处理
    /// 2. 关闭 HTTP 监听器
    /// 3. 释放相关资源
    /// 4. 更新状态为 Stopped
    /// 如果服务已经停止，则直接返回。
    /// </remarks>
    public Task StopProxyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_status is OllamaProxyStatus.Stopped)
            {
                return Task.CompletedTask;
            }

            SetStateCore(OllamaProxyStatus.Stopping, string.Empty, _startedAt);
        }

        // 按顺序释放资源，每个操作都捕获异常避免中断
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _listener = null; } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        SetState(OllamaProxyStatus.Stopped, string.Empty, null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 监听线程主函数，负责创建 HttpListener 并运行请求接收循环。
    /// </summary>
    /// <param name="cancellationToken">取消令牌，用于停止监听。</param>
    /// <remarks>
    /// 此方法在独立后台线程中运行，执行以下操作：
    /// 1. 创建并配置 HttpListener，添加监听前缀
    /// 2. 启动监听并更新状态为 Running
    /// 3. 运行异步请求接收循环 <see cref="AcceptLoopAsync"/>
    /// 4. 捕获并处理各种异常（权限不足、端口占用、取消等）
    /// 
    /// 注意：虽然方法内部调用异步方法，但通过 GetAwaiter().GetResult() 同步阻塞线程，
    /// 这是因为 HttpListener 需要在专用线程上运行 Accept 循环。
    /// </remarks>
    private void RunListenerThread(CancellationToken cancellationToken)
    {
        try
        {
            // 创建 HTTP 监听器并配置监听地址
            var listener = new HttpListener();
            var prefix = BuildListenerPrefix(_options);
            listener.Prefixes.Add(prefix);
            listener.Start();

            // 保存监听器实例并更新状态
            lock (_gate)
            {
                _listener = listener;
                SetStateCore(OllamaProxyStatus.Running, string.Empty, DateTimeOffset.Now);
            }
            RaiseStateChanged();

            logger.LogInformation("Ollama Proxy 已启动：{Prefix}", prefix);
            // 运行请求接收循环（同步阻塞等待异步循环完成）
            AcceptLoopAsync(listener, cancellationToken).GetAwaiter().GetResult();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            // 错误码 5 表示权限不足，需要管理员权限或配置 URL ACL
            logger.LogWarning(ex, "Ollama Proxy 监听权限不足");
            SetState(OllamaProxyStatus.AccessDenied, ex.Message, null);
        }
        catch (HttpListenerException ex)
        {
            // 判断是否为端口占用错误
            var status = IsLikelyPortInUse(ex) ? OllamaProxyStatus.PortInUse : OllamaProxyStatus.Failed;
            logger.LogWarning(ex, "Ollama Proxy 启动失败：{Status}", status);
            SetState(status, ex.Message, null);
        }
        catch (ObjectDisposedException)
        {
            // 监听器被释放，正常停止
            SetState(OllamaProxyStatus.Stopped, string.Empty, null);
        }
        catch (OperationCanceledException)
        {
            // 取消令牌触发，正常停止
            SetState(OllamaProxyStatus.Stopped, string.Empty, null);
        }
        catch (Exception ex)
        {
            // 未预期的异常，记录错误日志
            logger.LogError(ex, "Ollama Proxy 未预期失败");
            SetState(OllamaProxyStatus.Failed, ex.Message, null);
        }
    }

    /// <summary>
    /// 异步请求接收循环，持续监听并分发客户端请求。
    /// </summary>
    /// <param name="listener">HTTP 监听器实例。</param>
    /// <param name="cancellationToken">取消令牌，用于退出循环。</param>
    /// <returns>循环任务。</returns>
    /// <remarks>
    /// 此方法是一个无限循环，执行以下操作：
    /// 1. 等待下一个 HTTP 请求到达
    /// 2. 将请求分发到线程池异步处理（不阻塞 Accept 循环）
    /// 3. 捕获并处理各种异常
    /// 
    /// 每个请求通过 Task.Run 在独立线程池线程上处理，确保 Accept 循环不被阻塞。
    /// 当取消令牌触发或监听器关闭时，循环退出。
    /// </remarks>
    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                // 异步等待下一个请求，支持取消
                context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                // 将请求分发到线程池处理，不等待完成（fire-and-forget）
                // 使用 CancellationToken.None 确保请求处理不受 Accept 循环取消影响
                _ = Task.Run(() => HandleContextSafeAsync(context, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ollama Proxy 接收请求失败");
                if (context is not null)
                {
                    try { context.Response.Close(); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 安全处理单个 HTTP 请求，包含异常捕获和资源清理。
    /// </summary>
    /// <param name="context">HTTP 请求上下文。</param>
    /// <param name="serverToken">服务器级取消令牌。</param>
    /// <returns>请求处理任务。</returns>
    /// <remarks>
    /// 此方法是请求处理的顶层入口，执行以下操作：
    /// 1. 调用 <see cref="HandleContextAsync"/> 处理请求
    /// 2. 捕获所有异常并返回 500 错误响应
    /// 3. 确保响应流和连接被正确关闭
    /// 
    /// 即使处理过程中发生异常，也会尝试向客户端返回错误信息，
    /// 并在 finally 块中确保资源释放。
    /// </remarks>
    private async Task HandleContextSafeAsync(HttpListenerContext context, CancellationToken serverToken)
    {
        try
        {
            await HandleContextAsync(context, serverToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ollama Proxy 请求处理失败");
            try
            {
                // 向客户端返回 500 错误响应
                await OllamaHttpResponseWriter.WriteErrorAsync(context.Response, 500, ex.Message, serverToken);
            }
            catch { }
        }
        finally
        {
            // 确保响应流和连接被正确关闭
            try { context.Response.OutputStream.Close(); } catch { }
            try { context.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// 处理 HTTP 请求的核心逻辑，将请求路由到分发器。
    /// </summary>
    /// <param name="context">HTTP 请求上下文。</param>
    /// <param name="serverToken">服务器级取消令牌。</param>
    /// <returns>请求处理任务。</returns>
    /// <remarks>
    /// 此方法将请求委托给 <see cref="ProxyRouteDispatcher"/> 进行路由分发。
    /// 分发器会根据请求路径和配置决定如何处理请求（如转发到 AI 后端、返回模型列表等）。
    /// <see cref="WithLimiterAsync"/> 作为并发控制回调传入，确保请求处理不超过并发限制。
    /// </remarks>
    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken serverToken)
    {
        await routeDispatcher.DispatchAsync(context, _options, WithLimiterAsync, serverToken);
    }

    /// <summary>
    /// 并发控制包装器，限制同时处理的请求数量。
    /// </summary>
    /// <param name="action">要执行的请求处理操作。</param>
    /// <param name="response">HTTP 响应对象，用于返回 429 错误。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行任务。</returns>
    /// <remarks>
    /// 此方法使用 <see cref="SemaphoreSlim"/> 实现并发控制：
    /// 1. 尝试立即获取信号量（不等待）
    /// 2. 如果获取失败，返回 429 Too Many Requests 错误
    /// 3. 如果获取成功，执行实际操作并在完成后释放信号量
    /// 
    /// 这种设计确保代理服务不会因为过多并发请求而过载，
    /// 同时快速拒绝超出限制的请求，避免客户端长时间等待。
    /// </remarks>
    private async Task WithLimiterAsync(Func<Task> action, HttpListenerResponse response, CancellationToken cancellationToken)
    {
        // 尝试立即获取信号量，不等待（timeout=0）
        if (!await _requestLimiter.WaitAsync(0, cancellationToken))
        {
            // 并发数已达上限，返回 429 错误
            await OllamaHttpResponseWriter.WriteErrorAsync(response, 429, "too many proxy requests", cancellationToken);
            return;
        }

        try { await action(); }
        finally { _requestLimiter.Release(); }
    }

    /// <summary>
    /// 更新代理服务状态并触发状态变化事件。
    /// </summary>
    /// <param name="status">新的运行状态。</param>
    /// <param name="error">错误信息（如果有的话）。</param>
    /// <param name="startedAt">服务启动时间（如果有的话）。</param>
    /// <remarks>
    /// 此方法是线程安全的，通过 <see cref="_gate"/> 锁保护状态更新。
    /// 更新完成后触发 <see cref="StateChanged"/> 事件通知订阅者。
    /// </remarks>
    private void SetState(OllamaProxyStatus status, string error, DateTimeOffset? startedAt)
    {
        lock (_gate)
        {
            SetStateCore(status, error, startedAt);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// 核心状态更新方法，直接修改状态字段（不加锁）。
    /// </summary>
    /// <param name="status">新的运行状态。</param>
    /// <param name="error">错误信息。</param>
    /// <param name="startedAt">服务启动时间。</param>
    /// <remarks>
    /// 此方法必须在持有 <see cref="_gate"/> 锁的情况下调用，
    /// 或者在已经确保线程安全的上下文中调用。
    /// </remarks>
    private void SetStateCore(OllamaProxyStatus status, string error, DateTimeOffset? startedAt)
    {
        _status = status;
        _lastError = error;
        _startedAt = startedAt;
    }

    /// <summary>
    /// 触发状态变化事件。
    /// </summary>
    /// <remarks>
    /// 使用 try-catch 包裹事件调用，防止订阅者抛出异常影响服务运行。
    /// </remarks>
    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch { }
    }

    /// <summary>
    /// 构建 HTTP 监听器的 URL 前缀。
    /// </summary>
    /// <param name="options">代理配置快照。</param>
    /// <returns>监听前缀字符串，如 "http://localhost:11434/" 或 "http://+:11434/"。</returns>
    /// <remarks>
    /// 如果配置允许局域网访问（AllowLan=true），使用 "+" 通配符绑定所有网络接口。
    /// 否则使用配置中指定的主机地址。
    /// 特殊处理：将 "127.0.0.1" 转换为 "localhost" 以兼容 HttpListener 的要求。
    /// </remarks>
    private static string BuildListenerPrefix(AiProxyOptionsSnapshot options)
    {
        var host = options.AllowLan ? "+" : options.Host;
        if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) host = "localhost";
        return $"http://{host}:{options.Port}/";
    }

    /// <summary>
    /// 构建用于显示的代理 URL。
    /// </summary>
    /// <param name="options">代理配置快照。</param>
    /// <returns>显示用 URL 字符串，如 "http://localhost:11434"。</returns>
    /// <remarks>
    /// 此方法生成的 URL 用于 UI 展示和状态快照，不用于实际监听。
    /// 即使允许局域网访问，显示时仍使用 "localhost" 以便用户理解。
    /// </remarks>
    private static string BuildDisplayUrl(AiProxyOptionsSnapshot options)
    {
        var host = options.AllowLan ? "localhost" : options.Host;
        return $"http://{host}:{options.Port}";
    }

    /// <summary>
    /// 判断 HttpListenerException 是否由端口占用引起。
    /// </summary>
    /// <param name="ex">HTTP 监听器异常。</param>
    /// <returns>如果是端口占用错误则返回 true。</returns>
    /// <remarks>
    /// 通过检查错误码和错误消息来判断：
    /// - 错误码 32、183、10013 通常表示端口冲突
    /// - 错误消息中包含 "conflict" 或 "占用" 也表示端口被占用
    /// </remarks>
    private static bool IsLikelyPortInUse(HttpListenerException ex)
    {
        return ex.ErrorCode is 32 or 183 or 10013 || ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("占用", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 释放资源，实现 IDisposable 接口。
    /// </summary>
    /// <remarks>
    /// 此方法会同步等待代理服务停止，然后释放并发限制器。
    /// 所有异常都被捕获以确保释放过程不会抛出异常。
    /// </remarks>
    public void Dispose()
    {
        try { StopProxyAsync().GetAwaiter().GetResult(); } catch { }
        _requestLimiter.Dispose();
    }
}