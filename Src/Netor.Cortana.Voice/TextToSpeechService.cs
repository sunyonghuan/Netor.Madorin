using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

using SherpaOnnx;

using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Netor.Cortana.Voice;

/// <summary>
/// 语音合成服务。使用 Sherpa-ONNX MeloTTS zh_en 模型（VITS 架构）将文本转换为语音并播放。
/// 采用双 Channel 流水线架构：文本队列 → 合成线程 → 音频队列 → 播放线程，
/// 合成与播放并行执行，有效消除句间等待间隙。
/// </summary>
public sealed class TextToSpeechService(
    ILogger<TextToSpeechService> logger,
    IAppPaths appPaths,
    SystemSettingsService systemSettings,
    IPublisher publisher,
    ISubscriber subscriber) : IHostedService, IDisposable
{
    private OfflineTts? _tts;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;
    private CancellationTokenSource? _serviceCts;

    /// <summary>预生成的唤醒问候语音频缓存，应用启动时后台合成，唤醒时零延迟播放。</summary>
    private (float[] Samples, int SampleRate)? _greetingAudioCache;

    // ──────────────────── IHostedService ────────────────────

    /// <summary>
    /// 启动 TTS 服务：订阅 OnTtsEnqueue/OnTtsFinish 事件，后台预生成问候语缓存。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // AI 断句完成 → 入队合成
        subscriber.Subscribe<TtsEnqueueArgs>(Events.OnTtsEnqueue, async (_, args) =>
        {
            try
            {
                await EnqueueTextAsync(args.Sentence, _serviceCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "TTS 入队失败：{Text}", args.Sentence);
            }
            return false;
        });

        // AI 推理完成 → 完成合成并等待播放结束
        subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsFinish, async (_, _) =>
        {
            try
            {
                await FinishAndWaitAsync(_serviceCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "TTS 完成等待失败");
            }
            return false;
        });

        // 后台预生成唤醒问候语音频
        _ = Task.Run(async () =>
        {
            try
            {
                var audio = await SynthesizeAsync(WelcomeGreeting, _serviceCts.Token);
                if (audio.Samples.Length > 0)
                {
                    _greetingAudioCache = audio;
                    logger.LogInformation("唤醒问候语音频已预生成并缓存（{SampleCount} 采样点）", audio.Samples.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "预生成唤醒问候语失败，唤醒时将回退到实时合成");
            }
        }, _serviceCts.Token);

        logger.LogInformation("TTS 服务已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 TTS 服务。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serviceCts?.Cancel();
        Stop();
        logger.LogInformation("TTS 服务已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 播放预缓存的问候语，缓存未就绪时回退到实时合成。
    /// </summary>
    public async Task PlayGreetingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cached = _greetingAudioCache;
            if (cached is { } audio && audio.Samples.Length > 0)
            {
                logger.LogInformation("开始播放问候语");
                await PlayCachedAudioAsync(audio.Samples, audio.SampleRate, cancellationToken);
            }
            else
            {
                logger.LogDebug("问候语缓存未就绪，使用实时合成");
                await SpeakAsync(WelcomeGreeting, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "问候语播放失败，跳过直接进入语音识别");
        }
    }

    /// <summary>
    /// 重新生成欢迎语音频缓存。当用户修改欢迎语设置时调用，以立刻更新缓存。
    /// 如果服务还未初始化则安全跳过，不会抛出异常。
    /// </summary>
    public async Task RegenerateGreetingAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 如果 TTS 还未初始化，则跳过（避免多线程冲突和空值异常）
            if (_tts is null)
            {
                logger.LogDebug("TTS 服务尚未初始化，跳过欢迎语缓存更新");
                return;
            }

            await EnsureModelReadyAsync(cancellationToken);
            var audio = await SynthesizeAsync(WelcomeGreeting, cancellationToken);
            if (audio.Samples.Length > 0)
            {
                _greetingAudioCache = audio;
                logger.LogInformation("唤醒问候语音频已重新生成并更新缓存（{SampleCount} 采样点，文本：{Greeting}）",
                    audio.Samples.Length, WelcomeGreeting);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "重新生成唤醒问候语失败");
        }
    }

    // ──────── 双 Channel 流水线字段 ────────

    /// <summary>文本队列：调用方 → 合成线程。</summary>
    private Channel<string>? _textChannel;

    /// <summary>音频队列：合成线程 → 播放线程。</summary>
    private Channel<(float[] stream, string text)>? _audioChannel;

    /// <summary>合成后台任务。</summary>
    private Task? _synthesizeLoop;

    /// <summary>播放后台任务。</summary>
    private Task? _playbackLoop;

    /// <summary>控制流水线整体取消（Stop 或 Dispose 时触发）。</summary>
    private CancellationTokenSource? _pipelineCts;

    /// <summary>当所有播放完成时发出信号，供 FinishAndWaitAsync 等待。</summary>
    private TaskCompletionSource? _playbackDone;

    /// <summary>
    /// 模型文件目录，位于 UserDataDirectory/sherpa_models/TTS/。
    /// </summary>
    private string ModelDirectory => Path.Combine(appPaths.UserDataDirectory, "sherpa_models", "TTS");

    /// <summary>
    /// 当前语速倍率，每次合成时从数据库实时读取，支持无重启调整。
    /// </summary>
    private float CurrentSpeed => systemSettings.GetValue("Tts.Speed", 1.0f);

    /// <summary>
    /// 当前欢迎语，每次唤醒时从数据库实时读取，支持无重启调整。
    /// </summary>
    private string WelcomeGreeting => systemSettings.GetValue("Tts.WelcomeGreeting", "主人，我在!");

    // ──────────────────── 流水线生命周期 ────────────────────

    /// <summary>
    /// 创建新的双 Channel 流水线并启动合成/播放后台消费者。
    /// 每次开启新一轮对话（EnqueueText 或 SpeakAsync）前调用。
    /// </summary>
    private void StartPipeline(CancellationToken externalToken)
    {
        _pipelineCts?.Dispose();
        _pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = _pipelineCts.Token;

        _textChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _audioChannel = Channel.CreateBounded<(float[] stream, string text)>(new BoundedChannelOptions(32)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _playbackDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _synthesizeLoop = Task.Run(() => SynthesizeLoopAsync(token), token);
        _playbackLoop = Task.Run(() => PlaybackLoopAsync(token), token);
        publisher.Publish(Events.OnTtsStarted, new VoiceSignalArgs());
    }

    /// <summary>
    /// 停止当前流水线并等待后台任务结束。
    /// </summary>
    private async Task StopPipelineAsync()
    {
        _pipelineCts?.Cancel();

        // 强制完成两个 Channel，确保消费者循环能退出
        _textChannel?.Writer.TryComplete();
        _audioChannel?.Writer.TryComplete();

        if (_synthesizeLoop is not null)
        {
            try { await _synthesizeLoop; } catch (OperationCanceledException) { }
        }
        if (_playbackLoop is not null)
        {
            try { await _playbackLoop; } catch (OperationCanceledException) { }
        }

        // 确保等待方不会永久阻塞
        _playbackDone?.TrySetResult();
        publisher.Publish(Events.OnChatCompleted, new VoiceSignalArgs());
    }

    // ──────────────────── 公共 API ────────────────────

    /// <summary>
    /// 将文本加入合成队列（非阻塞）。文本会自动断句后逐句写入 text channel，
    /// 由后台合成线程消费。首次调用时自动启动流水线。
    /// </summary>
    /// <param name="text">要合成的文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task EnqueueTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        await EnsureModelReadyAsync(cancellationToken);

        // 如果流水线未启动或已完成，则重新启动
        if (_textChannel is null || _pipelineCts is null || _pipelineCts.IsCancellationRequested
            || _textChannel.Reader.Completion.IsCompleted)
        {
            StartPipeline(cancellationToken);
        }

        var sentences = SplitSentences(text);
        foreach (var sentence in sentences)
        {
            var sanitized = SanitizeForTts(sentence);
            if (string.IsNullOrEmpty(sanitized)) continue;

            await _textChannel!.Writer.WriteAsync(sanitized, cancellationToken);
        }
    }

    /// <summary>
    /// 标记文本输入结束并等待所有音频播放完毕。
    /// 调用此方法后不能再 EnqueueText，直到下一轮流水线重建。
    /// </summary>
    public async Task FinishAndWaitAsync(CancellationToken cancellationToken = default)
    {
        // 标记文本 channel 写入完成，合成线程会在消费完后自动关闭 audio channel
        _textChannel?.Writer.TryComplete();

        if (_playbackDone is null) return;

        // 等待播放完成或取消
        await using var reg = cancellationToken.Register(() => _playbackDone.TrySetCanceled(cancellationToken));
        await _playbackDone.Task;
        publisher.Publish(Events.OnChatCompleted, new VoiceSignalArgs());
    }

    /// <summary>
    /// 将文本合成为语音并播放（阻塞直到播放完毕）。
    /// 适用于需要等待播放结束后再继续的场景（如问候语播放后启动语音识别）。
    /// </summary>
    /// <param name="text">要合成的文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // 停止上一轮流水线（如有）
        await StopPipelineAsync();

        await EnqueueTextAsync(text, cancellationToken);
        await FinishAndWaitAsync(cancellationToken);
    }

    /// <summary>
    /// 将文本合成为 PCM 采样数据（不播放），用于预生成缓存场景。
    /// 返回 float[] 采样数据和采样率，调用方可自行缓存和播放。
    /// </summary>
    public async Task<(float[] Samples, int SampleRate)> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ([], 0);

        await EnsureModelReadyAsync(cancellationToken);

        var sanitized = SanitizeForTts(text);
        if (string.IsNullOrEmpty(sanitized))
            return ([], 0);

        var audio = _tts!.Generate(sanitized, CurrentSpeed, 0);

        if (audio is not null && audio.Samples is not null && audio.Samples.Length > 0)
        {
            float[] samples = audio.Samples;
            GC.KeepAlive(audio);
            return (samples, _tts.SampleRate);
        }

        return ([], 0);
    }

    /// <summary>
    /// 直接播放预缓存的 PCM 采样数据（不经过合成流水线）。
    /// </summary>
    public static async Task PlayCachedAudioAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0) return;
        await PlaySamplesAsync(samples, sampleRate, cancellationToken);
    }

    /// <summary>
    /// 立即停止当前正在进行的语音合成与播放。
    /// </summary>
    public void Stop()
    {
        _pipelineCts?.Cancel();
        _textChannel?.Writer.TryComplete();
        _audioChannel?.Writer.TryComplete();
        _playbackDone?.TrySetCanceled();
    }

    // ──────────────────── 后台消费者 ────────────────────

    /// <summary>
    /// 合成消费者：从 text channel 读取句子，调用 TTS 引擎生成音频，写入 audio channel。
    /// </summary>
    private async Task SynthesizeLoopAsync(CancellationToken cancellationToken)
    {
        var tts = _tts!;
        var textReader = _textChannel!.Reader;
        var audioWriter = _audioChannel!.Writer;

        try
        {
            await foreach (var sentence in textReader.ReadAllAsync(cancellationToken))
            {
                logger.LogInformation("合成片段：{Sentence}", sentence);

                try
                {
                    var audio = tts.Generate(sentence, CurrentSpeed, 0);

                    if (audio is not null && audio.Samples is not null)
                    {
                        float[] samples = audio.Samples;
                        GC.KeepAlive(audio);

                        if (samples.Length > 0)
                        {
                            await audioWriter.WriteAsync((samples, sentence), cancellationToken);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "合成片段失败，跳过：{Sentence}", sentence);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音合成循环出错");
        }
        finally
        {
            audioWriter.TryComplete();
        }
    }

    /// <summary>
    /// 播放消费者：使用 SegmentedWaveProvider 按句子分段播放，
    /// 字幕由声卡 Read 回调驱动，确保与实际音频输出精确同步。
    /// </summary>
    private async Task PlaybackLoopAsync(CancellationToken cancellationToken)
    {
        var audioReader = _audioChannel!.Reader;
        var sampleRate = _tts!.SampleRate;

        using var provider = new SegmentedWaveProvider(sampleRate, 1,
            text => publisher.Publish(Events.OnTtsSubtitle, new VoiceTextArgs(text)));

        using var player = new AotWavePlayer(provider, 200);
        var playbackStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += _ => playbackStopped.TrySetResult();

        player.Play();

        try
        {
            // 从 audioChannel 读取合成结果，入队到 SegmentedWaveProvider
            await foreach (var (samples, text) in audioReader.ReadAllAsync(cancellationToken))
            {
                provider.AddSegment(samples, text);
            }

            // 所有音频已入队，标记完成，播放器播完后会自动停止
            provider.Complete();

            // 等待播放线程自然结束
            await using var reg = cancellationToken.Register(() => playbackStopped.TrySetCanceled(cancellationToken));
            await playbackStopped.Task;

            publisher.Publish(Events.OnTtsCompleted, new VoiceSignalArgs());
            _playbackDone?.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            player.Stop();
            logger.LogDebug("语音播放已取消");
            _playbackDone?.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            player.Stop();
            logger.LogError(ex, "语音播放循环出错");
            _playbackDone?.TrySetResult();
        }
    }

    // ──────────────────── 文本清洗 ────────────────────

    private static readonly Regex MarkdownPattern = new(
        @"```[\s\S]*?```|`[^`]+`|!?\[[^\]]*\]\([^)]*\)|#{1,6}\s|\*{1,3}|_{1,3}|~~|>\s|\||-{3,}|={3,}",
        RegexOptions.Compiled);

    private static readonly Regex SpeakablePattern = new(
        @"[\u4e00-\u9fff\u3040-\u309f\u30a0-\u30ff\uac00-\ud7afa-zA-Z0-9]",
        RegexOptions.Compiled);

    private static string? SanitizeForTts(string text)
    {
        var cleaned = MarkdownPattern.Replace(text, " ");
        cleaned = cleaned.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

        if (!SpeakablePattern.IsMatch(cleaned))
        {
            return null;
        }

        return cleaned;
    }

    // ──────────────────── 断句 ────────────────────

    private static List<string> SplitSentences(string text)
    {
        var results = new List<string>();
        var sentenceBreaks = new[] { '。', '！', '？', '；', '\n' };
        var segments = SplitKeepDelimiter(text, sentenceBreaks);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.Length > 30)
            {
                var clauseBreaks = new[] { '，', '、', '：', '—' };
                var clauses = SplitKeepDelimiter(trimmed, clauseBreaks);
                foreach (var clause in clauses)
                {
                    var c = clause.Trim();
                    if (!string.IsNullOrEmpty(c)) results.Add(c);
                }
            }
            else
            {
                results.Add(trimmed);
            }
        }

        return results;
    }

    private static List<string> SplitKeepDelimiter(string text, char[] delimiters)
    {
        var parts = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (delimiters.Contains(text[i]))
            {
                var part = text[start..(i + 1)];
                if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            var remaining = text[start..];
            if (!string.IsNullOrWhiteSpace(remaining)) parts.Add(remaining);
        }

        return parts;
    }

    // ──────────────────── 音频播放 ────────────────────

    private static async Task PlaySamplesAsync(float[] samples, int sampleRate, CancellationToken cancellationToken)
    {
        var provider = new FloatArrayAudioProvider(samples, sampleRate);
        using var player = new AotWavePlayer(provider);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        player.PlaybackStopped += _ => tcs.TrySetResult();
        player.Play();

        await using var reg = cancellationToken.Register(() =>
        {
            player.Stop();
            tcs.TrySetResult();
        });

        await tcs.Task;
    }

    // ──────────────────── 模型初始化 ────────────────────

    private async Task EnsureModelReadyAsync(CancellationToken cancellationToken)
    {
        if (_tts is not null) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_tts is not null) return;

            var modelPath = Path.Combine(ModelDirectory, "model.int8.onnx");
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"TTS 模型文件不存在：{modelPath}");

            var tokensPath = Path.Combine(ModelDirectory, "tokens.txt");
            if (!File.Exists(tokensPath))
                throw new FileNotFoundException($"TTS tokens 文件不存在：{tokensPath}");

            var lexiconPath = Path.Combine(ModelDirectory, "lexicon.txt");
            if (!File.Exists(lexiconPath))
                throw new FileNotFoundException($"TTS lexicon 文件不存在：{lexiconPath}");

            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = modelPath;
            config.Model.Vits.Tokens = tokensPath;
            config.Model.Vits.Lexicon = lexiconPath;
            config.Model.Vits.DictDir = Path.Combine(ModelDirectory, "dict");
            config.Model.NumThreads = 2;
            config.Model.Provider = "cpu";
            config.Model.Debug = 0;
            config.RuleFsts = string.Join(",",
                Path.Combine(ModelDirectory, "phone.fst"),
                Path.Combine(ModelDirectory, "date.fst"),
                Path.Combine(ModelDirectory, "number.fst"),
                Path.Combine(ModelDirectory, "new_heteronym.fst"));

            _tts = new OfflineTts(config);
            _ = _tts.Generate("你好", 1.0f, 0);

            logger.LogInformation(
                "MeloTTS zh_en TTS 引擎已初始化，采样率: {SampleRate}Hz",
                _tts.SampleRate);
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ──────────────────── IDisposable ────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        Stop();

        _pipelineCts?.Dispose();
        _tts?.Dispose();
        _initLock.Dispose();
    }
}

/// <summary>
/// 将 float[] PCM 采样数据包装为 IAudioDataProvider，供 AotWavePlayer 播放。
/// </summary>
internal sealed class FloatArrayAudioProvider(float[] samples, int sampleRate) : IAudioDataProvider
{
    private int _position;

    public WAVEFORMATEX GetWaveFormatEx() => new()
    {
        wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
        nChannels = 1,
        nSamplesPerSec = (uint)sampleRate,
        nAvgBytesPerSec = (uint)(sampleRate * sizeof(float)),
        nBlockAlign = sizeof(float),
        wBitsPerSample = 32,
        cbSize = 0
    };

    public int Read(byte[] buffer, int offset, int count)
    {
        var samplesAvailable = samples.Length - _position;
        var bytesAvailable = samplesAvailable * sizeof(float);
        var bytesToCopy = Math.Min(count, bytesAvailable);
        var samplesToCopy = bytesToCopy / sizeof(float);

        if (samplesToCopy <= 0) return 0;

        Buffer.BlockCopy(samples, _position * sizeof(float), buffer, offset, samplesToCopy * sizeof(float));
        _position += samplesToCopy;

        return samplesToCopy * sizeof(float);
    }
}