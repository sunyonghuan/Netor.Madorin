using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

namespace Netor.Cortana.Voice;

/// <summary>
/// 编排 KWS / STT / TTS 三方对麦克风的独占切换。
/// KWS 与 STT 都通过 winmm 独占麦克风，必须分时复用：
/// 启动 → KWS 监听；唤醒 → 取消上一轮 → 播欢迎语 → 切 STT；STT 结束 → AI/TTS → 切回 KWS。
/// 支持播报中"barge-in"打断：任何状态下收到唤醒词都会取消当前进行中的任务并重新开始。
/// 不实现 IHostedService：必须在所有语音插件 Configure 完成后由宿主显式 StartAsync。
/// </summary>
public sealed class VoicePipelineCoordinator(
    ILogger<VoicePipelineCoordinator> logger,
    IKwsPluginAdapter kws,
    ISttPluginAdapter stt,
    ITtsPluginAdapter tts,
    IAiChatEngine chatEngine,
    ISubscriber subscriber,
    SystemSettingsService settings) : IDisposable
{
    private enum State
    {
        Idle,           // 未启动
        Listening,      // KWS 跑着，等唤醒
        Cancelling,     // 唤醒触发的清理过程，期间 stale 事件被丢弃
        Greeting,       // 唤醒后播欢迎语，KWS/STT 已停，TTS 正在播
        Recognizing,    // STT 跑着，KWS 停
        AwaitingTts,    // STT 已出 final，等 AI/TTS 接力，KWS 仍停
        Speaking        // AI 回复 TTS 播放中，KWS 仍停
    }

    /// <summary>清理阶段等待 stale TTS Completed 事件落地的时长。</summary>
    private static readonly TimeSpan CleanupDrainDelay = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private State _state = State.Idle;
    /// <summary>
    /// 协调器期待收到几次 TTS.Completed。每次发起新的 GreetingPlay/Streaming 时 +1。
    /// 上一轮残留的 Completed 事件计数已被清零，会被 HandleTtsCompletedAsync 丢弃。
    /// </summary>
    private int _expectedTtsCompletions;
    private CancellationTokenSource? _serviceCts;
    private bool _disposed;

    private bool IsKwsEnabled() => settings.GetValue("Voice.Kws.Enabled", false);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnWakeWordDetected, (_, _) =>
        {
            _ = HandleWakeWordAsync();
            return Task.FromResult(false);
        });

        subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, _) =>
        {
            _ = HandleSttFinalAsync();
            return Task.FromResult(false);
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnSttStopped, (_, _) =>
        {
            _ = HandleSttStoppedAsync();
            return Task.FromResult(false);
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsStarted, (_, _) =>
        {
            _ = HandleTtsStartedAsync();
            return Task.FromResult(false);
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, (_, _) =>
        {
            _ = HandleTtsCompletedAsync();
            return Task.FromResult(false);
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnChatCompleted, (_, _) =>
        {
            _ = HandleChatCompletedAsync();
            return Task.FromResult(false);
        });

        _ = StartListeningAsync();
        logger.LogInformation("语音流水线协调器已启动");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SafeAsync("STT.Stop", () => stt.StopAsync(cancellationToken: cancellationToken));
            await SafeAsync("KWS.Stop", () => kws.StopAsync(cancellationToken));
            _state = State.Idle;
            _expectedTtsCompletions = 0;
        }
        finally
        {
            _gate.Release();
        }
        _serviceCts?.Cancel();
        logger.LogInformation("语音流水线协调器已停止");
    }

    private Task StartListeningAsync() => TransitionAsync("初始化", async ct =>
    {
        if (_state != State.Idle) return;
        if (!IsKwsEnabled())
        {
            logger.LogInformation("KWS 插件开关已关闭，语音流水线保持空闲状态");
            return;
        }

        await SafeAsync("KWS.Start", () => kws.StartAsync(ct));
        _state = State.Listening;
        logger.LogInformation("语音流水线进入监听唤醒状态");
    });

    public Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        return TransitionAsync("Settings.Apply", async ct =>
        {
            if (!IsKwsEnabled())
            {
                await SafeAsync("STT.Stop", () => stt.StopAsync(cancellationToken: ct));
                await SafeAsync("KWS.Stop", () => kws.StopAsync(ct));
                _state = State.Idle;
                _expectedTtsCompletions = 0;
                logger.LogInformation("语音唤醒已关闭，KWS/STT 已停止");
                return;
            }

            await SafeAsync("KWS.Configure", () => kws.ConfigureAsync(ct));
            await SafeAsync("STT.Configure", () => stt.ConfigureAsync(ct));
            if (_state == State.Idle)
            {
                await SafeAsync("KWS.Start", () => kws.StartAsync(ct));
                _state = State.Listening;
                logger.LogInformation("语音唤醒已开启，KWS 已启动");
            }
        });
    }

    /// <summary>
    /// 处理唤醒事件。任何状态都接受，先 barge-in cleanup 再开新轮。
    /// 分两阶段：阶段 1 取消上一轮（lock 内）→ 等 150ms 让残留事件落地 → 阶段 2 启新轮（lock 内）。
    /// </summary>
    private Task HandleWakeWordAsync()
    {
        if (_serviceCts is null || _serviceCts.IsCancellationRequested) return Task.CompletedTask;
        if (!IsKwsEnabled()) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                if (!await BargeInCleanupAsync().ConfigureAwait(false)) return;
                await Task.Delay(CleanupDrainDelay, _serviceCts.Token).ConfigureAwait(false);
                await StartNewRoundAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "处理唤醒事件失败");
            }
        });
    }

    /// <summary>
    /// 阶段 1：取消上一轮所有进行中任务，进入 Cancelling 状态。
    /// 返回 false 表示协调器已停止，不再继续。
    /// </summary>
    private async Task<bool> BargeInCleanupAsync()
    {
        if (_serviceCts is null || _serviceCts.IsCancellationRequested) return false;
        try { await _gate.WaitAsync(_serviceCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }

        try
        {
            var prev = _state;
            logger.LogInformation("收到唤醒，取消上一轮（之前状态={Prev}）", prev);

            // 1. 取消 AI 推理（若正在跑），避免它继续向 TTS 队列写入。
            try { chatEngine.CancelCurrentTask(); }
            catch (Exception ex) { logger.LogDebug(ex, "取消 AI 推理时异常"); }

            // 2. 停 TTS：中断当前正在播放/合成的内容。
            await SafeAsync("TTS.Stop", () => tts.StopAsync(cancellationToken: _serviceCts.Token));

            // 3. 停 STT（若在跑），释放麦克风给后续 STT 重启用。
            if (prev == State.Recognizing)
            {
                await SafeAsync("STT.Stop", () => stt.StopAsync(cancellationToken: _serviceCts.Token));
            }

            // 4. 停 KWS：以下状态 KWS 都在跑（Listening 显然，AwaitingTts/Speaking 在 barge-in 模式下也在跑）。
            if (prev is State.Listening or State.AwaitingTts or State.Speaking)
            {
                await SafeAsync("KWS.Stop", () => kws.StopAsync(_serviceCts.Token));
            }

            // 5. 作废所有上一轮期待的 TTS Completed 事件。
            _expectedTtsCompletions = 0;
            _state = State.Cancelling;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>阶段 2：启动新一轮，播欢迎语 → STT。</summary>
    private async Task StartNewRoundAsync()
    {
        if (_serviceCts is null || _serviceCts.IsCancellationRequested) return;
        try { await _gate.WaitAsync(_serviceCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        try
        {
            if (_state != State.Cancelling)
            {
                logger.LogDebug("StartNewRoundAsync 进入时状态已不是 Cancelling（实际={State}），放弃", _state);
                return;
            }

            // 优先播放欢迎语；播完才进 STT。若没有 TTS 插件或欢迎语缓存，直接进 STT。
            var greetingResult = await SafeInvokeAsync("TTS.GreetingPlay",
                () => tts.GreetingPlayAsync(cancellationToken: _serviceCts.Token));

            if (greetingResult.Ok && greetingResult.Code != "skipped")
            {
                _state = State.Greeting;
                _expectedTtsCompletions = 1;
                logger.LogInformation("唤醒成功，正在播放欢迎语");
                return;
            }

            await SafeAsync("STT.Start", () => stt.StartAsync(cancellationToken: _serviceCts.Token));
            _state = State.Recognizing;
            logger.LogInformation("唤醒成功（无欢迎语），已切到 STT 识别状态");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task HandleSttFinalAsync() => TransitionAsync("STT.Final", async ct =>
    {
        if (_state != State.Recognizing) return;
        await SafeAsync("STT.Stop", () => stt.StopAsync(cancellationToken: ct));
        // 期待一次 TTS Completed（AI 回复出 token 流时 TtsPluginOutputChannel 会发起播放）。
        _expectedTtsCompletions = 1;
        // STT 释放麦克风后，立即把 KWS 拉起来听下一次唤醒，让用户能在 AI 播报中打断。
        await SafeAsync("KWS.Start", () => kws.StartAsync(ct));
        _state = State.AwaitingTts;
        logger.LogDebug("STT 已出 final，等待 AI/TTS 接力（KWS 已恢复以支持 barge-in）");
    });

    private Task HandleSttStoppedAsync() => TransitionAsync("STT.Stopped", async ct =>
    {
        if (_state != State.Recognizing) return;
        await SafeAsync("STT.Stop", () => stt.StopAsync(cancellationToken: ct));
        await SafeAsync("KWS.Start", () => kws.StartAsync(ct));
        _state = State.Listening;
        logger.LogInformation("STT 超时无内容，已重新进入监听唤醒状态");
    });

    private Task HandleTtsStartedAsync() => TransitionAsync("TTS.Started", _ =>
    {
        if (_state == State.AwaitingTts)
        {
            _state = State.Speaking;
            logger.LogDebug("TTS 播放开始");
        }
        // Greeting 状态：保持 Greeting；Cancelling 状态：忽略（stale）。
        return Task.CompletedTask;
    });

    private Task HandleTtsCompletedAsync() => TransitionAsync("TTS.Completed", async ct =>
    {
        // stale 事件直接丢弃。Cancelling 期间收到的 Completed 一律被丢弃；
        // 任何状态下若没有期待的 Completion 也丢弃（防止上一轮残留）。
        if (_state == State.Cancelling || _expectedTtsCompletions <= 0)
        {
            logger.LogDebug("丢弃 stale TTS.Completed（state={State}, expected={Expected}）",
                _state, _expectedTtsCompletions);
            return;
        }

        _expectedTtsCompletions--;

        if (_state == State.Greeting)
        {
            // 欢迎语播放结束，进入正式识别。Greeting 期间 KWS 是停的，需要先停 STT 不需要。
            await SafeAsync("STT.Start", () => stt.StartAsync(cancellationToken: ct));
            _state = State.Recognizing;
            logger.LogInformation("欢迎语播报结束，已切到 STT 识别状态");
            return;
        }

        if (_state is State.Speaking or State.AwaitingTts)
        {
            // AwaitingTts/Speaking 期间 KWS 已经在跑（用于 barge-in），不需再 Start，
            // 直接切回 Listening 状态即可。
            _state = State.Listening;
            logger.LogInformation("TTS 播报结束，已重新进入监听唤醒状态");
        }
    });

    private Task HandleChatCompletedAsync() => TransitionAsync("Chat.Completed", _ =>
    {
        // AI 流结束但没发起 TTS（例如主窗口可见时直走文字通道）。
        // AwaitingTts 状态下 KWS 已经在跑，直接切回 Listening 即可。
        if (_state == State.AwaitingTts)
        {
            _expectedTtsCompletions = 0;
            _state = State.Listening;
            logger.LogInformation("AI 完成但未发起 TTS，已重新进入监听唤醒状态");
        }
        return Task.CompletedTask;
    });

    private async Task TransitionAsync(string trigger, Func<CancellationToken, Task> action)
    {
        if (_serviceCts is null || _serviceCts.IsCancellationRequested) return;
        try
        {
            await _gate.WaitAsync(_serviceCts.Token);
        }
        catch (OperationCanceledException) { return; }

        try
        {
            await action(_serviceCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音流水线状态转换失败：trigger={Trigger}, state={State}", trigger, _state);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SafeAsync(string label, Func<Task<VoicePluginToolResult>> call)
    {
        try
        {
            var result = await call();
            if (!result.Ok && result.Code != "skipped" && result.Code != "model_not_ready")
            {
                logger.LogWarning("语音插件调用 {Label} 失败：{Code} {Message}", label, result.Code, result.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音插件调用 {Label} 异常", label);
        }
    }

    private async Task SafeAsync(string label, Func<Task> call)
    {
        try { await call(); }
        catch (Exception ex) { logger.LogError(ex, "语音插件调用 {Label} 异常", label); }
    }

    private async Task<TtsPluginToolResult> SafeInvokeAsync(string label, Func<Task<TtsPluginToolResult>> call)
    {
        try
        {
            var result = await call();
            if (!result.Ok && result.Code != "skipped" && result.Code != "greeting_not_ready")
            {
                logger.LogWarning("语音插件调用 {Label} 失败：{Code} {Message}", label, result.Code, result.Message);
            }
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音插件调用 {Label} 异常", label);
            return TtsPluginToolResult.Failed("invoke_exception", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _gate.Dispose();
    }
}
