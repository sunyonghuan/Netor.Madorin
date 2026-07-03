using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

using SherpaOnnx;

using System.Collections.Concurrent;

namespace Netor.Cortana.Voice;

/// <summary>
/// 实时流式语音识别服务。使用 Sherpa-ONNX Paraformer Streaming 进行流式中英双语语音识别。
/// Paraformer 为阿里达摩院非自回归架构，使用 encoder/decoder 两件套（无 joiner）。
/// 唤醒后按需启动，识别完成或超时后自动停止。
/// </summary>
public sealed class SpeechRecognitionService(
    ILogger<SpeechRecognitionService> logger,
    IAppPaths appPaths,
    SystemSettingsService systemSettings,
    IPublisher publisher,
    ISubscriber subscriber,
    TextToSpeechService ttsService,
    IAiChatEngine chatEngine,
    IWindowController windowController) : IHostedService, IDisposable
{
    private const int SampleRate = 16000;
    private OnlineRecognizer? _recognizer;
    private bool _isModelLoaded;
    private CancellationTokenSource? _serviceCts;

    /// <summary>轮次级取消令牌，每次唤醒时重建，用于取消上一轮 STT/AI/TTS。</summary>
    private CancellationTokenSource? _roundCts;

    /// <summary>当前正在运行的识别线程，CancelCurrentRound 时需 Join 等待退出。</summary>
    private Thread? _currentThread;

    /// <summary>同步锁，保护 _currentThread / _roundCts 的并发访问。</summary>
    private readonly Lock _roundLock = new();

    // ──────────────────── IHostedService ────────────────────

    /// <summary>
    /// 启动 STT 服务：订阅唤醒词事件和对话完成事件。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 唤醒词检测 → 取消当前轮次 + 播放问候语 + 启动 STT
        subscriber.On(Events.OnWakeWordDetected, async (_, _) =>
        {
            // 取消上一轮所有进行中的任务（STT / AI 推理 / TTS 播放）
            CancelCurrentRound();

            // 创建新轮次令牌，链接服务生命周期
            CancellationToken roundToken;
            lock (_roundLock)
            {
                _roundCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCts!.Token);
                roundToken = _roundCts.Token;
            }

            try
            {
                await ttsService.PlayGreetingAsync(roundToken);
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "问候语播放失败，跳过直接进入语音识别");
            }

            StartRecognition(roundToken);
            return false;
        });

        // AI 对话 + TTS 完成 → 重启 STT 继续监听
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnChatCompleted, (_, _) =>
        {
            // 仅在当前轮次未被取消时继续监听
            CancellationToken? token;
            lock (_roundLock)
            {
                token = _roundCts is null || _roundCts.IsCancellationRequested ? null : _roundCts.Token;
            }
            if (token is null) return Task.FromResult(false);

            // 主窗体可见时不再自动续杯：用户在主窗体下需要再说就重新喊唤醒词，
            // 避免 "AI 答完 → 自动监听 → 又一条 → 又触发 AI" 的循环。
            if (windowController.IsMainWindowVisible()) return Task.FromResult(false);

            logger.LogInformation("AI 回复播放完成，继续监听用户语音...");
            publisher.Emit(Events.OnSttPartial, new VoiceTextArgs("主人, 我在听..."));
            StartRecognition(token.Value);
            return Task.FromResult(false);
        });

        logger.LogInformation("STT 服务已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 STT 服务。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serviceCts?.Cancel();
        logger.LogInformation("STT 服务已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 模型文件目录，与 TTS 模型保持一致，位于输出目录 sherpa_models/STT/。
    /// </summary>
    private string ModelDirectory => Path.Combine(appPaths.UserDataDirectory, "sherpa_models", "STT");

    /// <summary>
    /// 延迟加载 STT 模型，首次调用时从输出目录加载 Paraformer Streaming 模型文件并初始化引擎。
    /// </summary>
    private void EnsureModelLoaded()
    {
        if (_isModelLoaded)
            return;

        string encoderPath = Path.Combine(ModelDirectory, "encoder.int8.onnx");
        string decoderPath = Path.Combine(ModelDirectory, "decoder.int8.onnx");
        string tokensPath = Path.Combine(ModelDirectory, "tokens.txt");

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;

        // Paraformer Streaming 模型使用 encoder/decoder 两件套（非自回归架构，无 joiner）
        config.ModelConfig.Paraformer.Encoder = encoderPath;
        config.ModelConfig.Paraformer.Decoder = decoderPath;
        config.ModelConfig.Tokens = tokensPath;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;

        config.DecodingMethod = "greedy_search";
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = systemSettings.GetValue("SherpaOnnx.Rule1MinTrailingSilence", 2.4f);
        config.Rule2MinTrailingSilence = systemSettings.GetValue("SherpaOnnx.Rule2MinTrailingSilence", 1.2f);
        config.Rule3MinUtteranceLength = systemSettings.GetValue("SherpaOnnx.Rule3MinUtteranceLength", 20.0f);

        _recognizer = new OnlineRecognizer(config);
        _isModelLoaded = true;

        logger.LogInformation("Sherpa-ONNX Paraformer Streaming 引擎初始化完成");
    }

    /// <summary>
    /// 启动一次语音识别会话。在后台线程上运行，超时或静音时自动结束。
    /// </summary>
    /// <param name="cancellationToken">外部取消令牌。</param>
    public void StartRecognition(CancellationToken cancellationToken = default)
    {
        var thread = new Thread(() => RecognitionLoop(cancellationToken))
        {
            IsBackground = true,
            Name = "SpeechRecognition"
        };
        lock (_roundLock)
        {
            _currentThread = thread;
        }
        thread.Start();
    }

    /// <summary>
    /// 识别循环：采集麦克风音频 → 流式解码 → 触发识别结果事件。
    /// 使用可重置的空闲超时机制：检测到语音内容时自动重置计时器，
    /// 避免长语音被固定倒计时截断。
    /// </summary>
    private void RecognitionLoop(CancellationToken cancellationToken)
    {
        // 在 try 外部声明，确保超时/异常后仍可访问
        string lastText = string.Empty;
        string accumulatedText = string.Empty;
        bool hasFinalResult = false;

        try
        {
            EnsureModelLoaded();

            var stream = _recognizer!.CreateStream();

            // 可重置的空闲超时：每次检测到语音活动时重置
            float recognitionTimeout = systemSettings.GetValue("SherpaOnnx.RecognitionTimeoutSeconds", 10.0f);
            DateTime lastActivityTime = DateTime.UtcNow;
            TimeSpan idleTimeout = TimeSpan.FromSeconds(recognitionTimeout);

            using var recorder = new AotWaveRecorder(SampleRate, 16, 1, 100, 3);

            // 线程安全的音频缓冲区：回调线程入队，识别线程出队，避免并发访问 stream 导致乱码
            ConcurrentQueue<float[]> audioQueue = new();

            recorder.DataAvailable += (buffer, bytesRecorded) =>
            {
                int sampleCount = bytesRecorded / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768.0f;
                }

                audioQueue.Enqueue(samples);
            };

            recorder.StartRecording();
            logger.LogInformation("语音识别已开始，等待用户说话...");

            // 用 WaitHandle 替代 Thread.Sleep，使取消令牌能瞬时打断循环。
            var cancelHandle = cancellationToken.WaitHandle;

            while (!cancellationToken.IsCancellationRequested)
            {
                // 检查空闲超时
                if (DateTime.UtcNow - lastActivityTime > idleTimeout)
                {
                    logger.LogInformation("语音识别空闲超时（{Seconds}秒无新内容）", recognitionTimeout);
                    break;
                }

                // 从缓冲区取出音频数据，统一在识别线程中喂给 stream
                while (audioQueue.TryDequeue(out float[]? samples))
                {
                    stream.AcceptWaveform(SampleRate, samples);
                }

                while (_recognizer.IsReady(stream))
                {
                    _recognizer.Decode(stream);
                }

                string currentSegment = _recognizer.GetResult(stream).Text;
                bool isEndpoint = _recognizer.IsEndpoint(stream);

                // 构建完整显示文本：已确认的段落 + 当前正在识别的段落
                string displayText = accumulatedText + (currentSegment ?? string.Empty);

                // 推送中间结果，并重置空闲计时器
                if (!string.IsNullOrWhiteSpace(displayText) && displayText != lastText)
                {
                    lastText = displayText;
                    lastActivityTime = DateTime.UtcNow;
                    publisher.Publish(Events.OnSttPartial, new VoiceTextArgs(displayText));
                }

                // 检测到端点（自然停顿或 Rule3 最大时长触发）：
                // 累积当前段落文本，重置流，继续监听直到空闲超时
                if (isEndpoint)
                {
                    if (!string.IsNullOrWhiteSpace(currentSegment))
                    {
                        accumulatedText += currentSegment;
                        logger.LogDebug("端点分段确认：{Text}", currentSegment);
                    }

                    _recognizer.Reset(stream);

                    // 已有识别内容时，缩短空闲超时为 Rule2 的值，让用户停顿后快速结束
                    if (!string.IsNullOrWhiteSpace(accumulatedText))
                    {
                        float rule2Silence = systemSettings.GetValue("SherpaOnnx.Rule2MinTrailingSilence", 1.2f);
                        var shortTimeout = TimeSpan.FromSeconds(rule2Silence);
                        if (shortTimeout < idleTimeout)
                        {
                            idleTimeout = shortTimeout;
                        }

                        lastActivityTime = DateTime.UtcNow;
                    }
                }

                // 等待 100ms 或被取消唤醒，取消能瞬时退出循环
                cancelHandle.WaitOne(100);
            }

            try { recorder.StopRecording(); } catch { /* 释放即可，吞掉退出期异常 */ }

            // 会话自然结束（超时），提交所有已识别内容作为最终结果。
            // 注意：被外部主动取消时，cancellationToken.IsCancellationRequested == true，
            // 这种情况下不应当再发出 final，避免取消瞬间在 UI 上多冒一条用户消息。
            if (!cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(lastText))
            {
                logger.LogInformation("语音识别最终结果：{Text}", lastText);
                publisher.Publish(Events.OnSttFinal, new VoiceTextArgs(lastText));
                hasFinalResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("语音识别被外部取消");
            // 取消路径不再补发 final，避免出现意外的用户消息
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音识别服务运行异常");
        }
        finally
        {
            // 始终发出 Stopped 信号，让 UI 端（气泡/指示器）能收尾。
            // 上游订阅者根据是否已收到 final 自行决定显示文案。
            publisher.Publish(Events.OnSttStopped, new VoiceSignalArgs());
            _ = hasFinalResult; // 保留变量以便日后扩展（避免编译警告）
        }
    }

    /// <summary>
    /// 取消当前轮次的所有进行中任务：STT 识别、AI 推理、TTS 播放。
    /// 该方法是同步阻塞的，会等待识别线程退出后才返回，确保旧线程不会再发任何事件。
    /// </summary>
    public void CancelCurrentRound()
    {
        Thread? thread;
        CancellationTokenSource? cts;

        lock (_roundLock)
        {
            cts = _roundCts;
            thread = _currentThread;
            _roundCts = null;
            _currentThread = null;
        }

        // 1. 取消轮次令牌 → 通知 STT RecognitionLoop 退出
        cts?.Cancel();

        // 2. 等待识别线程真正退出（最多 2 秒），避免旧线程继续 publish 事件
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); }
            catch { /* Join 失败不阻断后续清理 */ }
        }

        cts?.Dispose();

        // 3. 取消 AI 推理 + 通知输出通道清理
        try { chatEngine.CancelCurrentTask(); } catch (Exception ex) { logger.LogDebug(ex, "取消 AI 推理时异常"); }

        // 4. 停止 TTS 播放流水线
        try { ttsService.Stop(); } catch (Exception ex) { logger.LogDebug(ex, "停止 TTS 时异常"); }

        logger.LogInformation("已取消上一轮语音交互任务");
    }

    public void Dispose()
    {
        CancelCurrentRound();
        try
        {
            _serviceCts?.Cancel();
            _serviceCts?.Dispose();
            _recognizer?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音识别服务清理异常");
        }
    }
}