using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

using SherpaOnnx;

namespace Netor.Cortana.Voice;

/// <summary>
/// 语音唤醒后台服务。使用 Sherpa-ONNX KeywordSpotter 引擎监听麦克风，检测唤醒词后触发事件。
/// 作为 <see cref="IHostedService"/> 在应用启动时自动开始监听。
/// </summary>
public sealed class WakeWordService(
    ILogger<WakeWordService> logger,
    IAppPaths appPaths,
    SystemSettingsService systemSettings,
    IPublisher publisher) : IHostedService, IDisposable
{
    private const int SampleRate = 16000;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cts;
    private Thread? _listenThread;
    private KeywordSpotter? _kws;

    /// <summary>
    /// KWS 模型文件目录，位于 UserDataDirectory/sherpa_models/KWS/。
    /// </summary>
    private string ModelDirectory => Path.Combine(appPaths.UserDataDirectory, "sherpa_models", "KWS");

    /// <summary>
    /// 启动唤醒词监听。在独立后台线程上运行，避免阻塞 UI 线程。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (!systemSettings.GetValue("Voice.WakeWordEnabled", true))
            {
                logger.LogInformation("语音唤醒开关已关闭，跳过唤醒服务启动");
                return Task.CompletedTask;
            }

            if (_listenThread?.IsAlive == true)
                return Task.CompletedTask;

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "WakeWordListener"
            };
            _listenThread.Start();
            logger.LogInformation("语音唤醒服务已启动");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止唤醒词监听并释放资源。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_listenThread is null)
                return Task.CompletedTask;

            _cts?.Cancel();
            _listenThread.Join(TimeSpan.FromSeconds(3));
            _listenThread = null;
            _cts?.Dispose();
            _cts = null;
            _kws?.Dispose();
            _kws = null;
            logger.LogInformation("语音唤醒服务已停止");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 麦克风监听循环。从输出目录加载 KWS 模型 → 初始化 KeywordSpotter → 采集音频 → 检测唤醒词。
    /// </summary>
    private void ListenLoop()
    {
        try
        {
            // 从 Content 输出目录加载模型文件（与 STT/TTS 一致）
            string encoderPath = Path.Combine(ModelDirectory, "encoder.int8.onnx");
            string decoderPath = Path.Combine(ModelDirectory, "decoder.int8.onnx");
            string joinerPath = Path.Combine(ModelDirectory, "joiner.int8.onnx");
            string tokensPath = Path.Combine(ModelDirectory, "tokens.txt");
            string keywordsPath = Path.Combine(ModelDirectory, "keywords.txt");

            var config = new KeywordSpotterConfig();
            config.FeatConfig.SampleRate = SampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = encoderPath;
            config.ModelConfig.Transducer.Decoder = decoderPath;
            config.ModelConfig.Transducer.Joiner = joinerPath;
            config.ModelConfig.Tokens = tokensPath;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.NumThreads = 1;
            config.ModelConfig.Debug = 0;
            config.KeywordsFile = keywordsPath;
            config.KeywordsThreshold = systemSettings.GetValue("SherpaOnnx.KeywordsThreshold", 0.1f);
            config.KeywordsScore = systemSettings.GetValue("SherpaOnnx.KeywordsScore", 2.0f);
            config.NumTrailingBlanks = systemSettings.GetValue("SherpaOnnx.NumTrailingBlanks", 1);

            _kws = new KeywordSpotter(config);
            var stream = _kws.CreateStream();

            logger.LogInformation("Sherpa-ONNX KeywordSpotter 引擎初始化完成 (采样率={SampleRate})", SampleRate);

            int deviceCount = AotWaveRecorder.DeviceCount;
            logger.LogInformation("音频输入设备数量: {Count}", deviceCount);

            using var recorder = new AotWaveRecorder(SampleRate, 16, 1, 100, 3);

            recorder.RecordingStopped += ex =>
            {
                if (ex != null)
                    logger.LogError(ex, "录音线程异常终止");
            };

            recorder.DataAvailable += (buffer, bytesRecorded) =>
            {
                // 将 byte[]（16-bit PCM）转换为 float[]
                int sampleCount = bytesRecorded / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768.0f;
                }

                stream.AcceptWaveform(SampleRate, samples);
            };

            recorder.StartRecording();
            logger.LogInformation("麦克风监听已开始 (设备数={DeviceCount})", deviceCount);

            // 主循环：解码 + 检测唤醒词
            while (!_cts!.Token.IsCancellationRequested)
            {
                while (_kws.IsReady(stream))
                {
                    _kws.Decode(stream);

                    var result = _kws.GetResult(stream);
                    if (!string.IsNullOrEmpty(result.Keyword))
                    {
                        _kws.Reset(stream);
                        logger.LogInformation("检测到唤醒词：{Keyword}", result.Keyword);
                        publisher.Emit(Events.OnWakeWordDetected, new VoiceSignalArgs());
                    }
                }

                Thread.Sleep(100);
            }

            recorder.StopRecording();
        }
        catch (OperationCanceledException)
        {
            // 正常取消，忽略
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "语音唤醒服务运行异常");
        }
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}