using DesktopPet.Ai;
using DesktopPet.Behaviors;

internal sealed class PetBehaviorRuntime : IDisposable
{
    private readonly PetBehaviorStateMachine _stateMachine;
    private SimpleMouthMotionLoop? _mouthMotionLoop;
    private readonly Action<Exception, string>? _logError;
    // 非 readonly，以便 RestartWebSocket 可以替换
    private CancellationTokenSource _cts = new();
    private Task? _webSocketTask;
    private bool _disposed;

    /// <summary>
    /// 每当状态快照里的字幕文本变化时触发，参数为新字幕（空/null 表示清除）。
    /// 由外部（Program.cs）连接到 renderHost.SetSubtitle()。
    /// </summary>
    public event Action<string?>? SubtitleChanged;

    public PetBehaviorRuntime(
        PetBehaviorStateMachine stateMachine,
        SimpleMouthMotionLoop? mouthMotionLoop,
        Action<Exception, string>? logError = null)
    {
        _stateMachine = stateMachine;
        _mouthMotionLoop = mouthMotionLoop;
        _logError = logError;
    }

    public PetBehaviorSnapshot Current => _stateMachine.Current;

    /// <summary>Replaces the mouth animation target after a model switch.</summary>
    public void SetMouthMotionLoop(SimpleMouthMotionLoop? loop)
    {
        _mouthMotionLoop = loop;
        if (loop is not null)
        {
            loop.SetState(_stateMachine.Current.State);
        }
    }

    public PetBehaviorSnapshot Apply(PetEvent petEvent)
    {
        var snapshot = _stateMachine.Apply(petEvent);
        _mouthMotionLoop?.SetState(snapshot.State);
        SubtitleChanged?.Invoke(string.IsNullOrEmpty(snapshot.Subtitle) ? null : snapshot.Subtitle);
        return snapshot;
    }

    /// <summary>
    /// 首次启动 WebSocket 连接（幂等：已启动则忽略）。
    /// </summary>
    public void StartWebSocket(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocketTask is not null)
        {
            return;
        }

        _webSocketTask = Task.Run(() => RunWebSocketWithReconnectAsync(uri, _cts.Token));
    }

    /// <summary>
    /// 停止现有 WebSocket 连接，并以新 URI 重新启动。
    /// 用于设置面板保存新连接参数后立即生效。
    /// </summary>
    public void RestartWebSocket(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 1. 取消旧连接
        _cts.Cancel();
        try
        {
            _webSocketTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
            when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
        catch (OperationCanceledException) { }

        _cts.Dispose();

        // 2. 换新 CTS 并启动
        _cts = new CancellationTokenSource();
        _webSocketTask = Task.Run(() => RunWebSocketWithReconnectAsync(uri, _cts.Token));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            _webSocketTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
            when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
        catch (OperationCanceledException) { }

        _cts.Dispose();
    }

    /// <summary>
    /// 带指数退避的自动重连循环。
    /// 延迟序列：2s → 4s → 8s → 16s → 30s（上限）。
    /// </summary>
    private async Task RunWebSocketWithReconnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested)
        {
            var succeeded = await RunWebSocketOnceAsync(uri, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested) break;

            // 连接成功后正常断开，重置退避
            if (succeeded)
            {
                delay = TimeSpan.FromSeconds(2);
            }

            Apply(new PetEvent(PetEventKind.Idle));
            Console.Error.WriteLine($"[DesktopPet] WebSocket 断开，{delay.TotalSeconds:0}s 后重连…");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = delay * 2 < maxDelay ? delay * 2 : maxDelay;
        }
    }

    /// <summary>
    /// 执行一次 WebSocket 连接 + 事件接收。
    /// 返回 true 表示连接曾成功建立；false 表示连接阶段就失败了。
    /// </summary>
    private async Task<bool> RunWebSocketOnceAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new CortanaRealtimeClient(uri);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var petEvent in client.ReadPetEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                Apply(petEvent);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logError?.Invoke(ex, "Cortana WebSocket 连接异常");
            Console.Error.WriteLine($"[DesktopPet] WebSocket 异常: {ex.Message}");
            return false;
        }
    }
}
