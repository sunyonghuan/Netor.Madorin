namespace Netor.Cortana.AI;

/// <summary>
/// 应用程序配置根节点，对应嵌入的 appsettings.json。
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// 阿里云服务配置。
    /// </summary>
    public AliyunSettings Aliyun { get; init; } = new();

    /// <summary>
    /// Sherpa-ONNX 语音引擎配置。
    /// </summary>
    public SherpaOnnxSettings SherpaOnnx { get; init; } = new();

    /// <summary>
    /// 语音合成（TTS）配置。
    /// </summary>
    public TtsSettings Tts { get; init; } = new();
}

/// <summary>
/// 阿里云相关密钥与区域配置。
/// </summary>
public sealed record AliyunSettings
{
    public string AccessKeyId { get; init; } = string.Empty;
    public string AccessKeySecret { get; init; } = string.Empty;
    public string AppKey { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
}

/// <summary>
/// Sherpa-ONNX 语音引擎配置（唤醒词检测 + 流式语音识别）。
/// </summary>
public sealed record SherpaOnnxSettings
{
    /// <summary>
    /// 唤醒词灵敏度阈值，值越小越灵敏（默认 0.1）。
    /// </summary>
    public float KeywordsThreshold { get; init; } = 0.1f;

    /// <summary>
    /// 唤醒词增强分数，值越大越容易触发（默认 5.0）。
    /// </summary>
    public float KeywordsScore { get; init; } = 5.0f;

    /// <summary>
    /// 唤醒词确认所需的尾部空白帧数，值越小响应越快（默认 1）。
    /// </summary>
    public int NumTrailingBlanks { get; init; } = 1;

    /// <summary>
    /// 未检测到任何语音时的静音超时（秒），超过后视为端点（默认 5.0）。
    /// </summary>
    public float Rule1MinTrailingSilence { get; init; } = 5.0f;

    /// <summary>
    /// 检测到语音后的静音超时（秒），说话停顿超过此时间视为一句话结束（默认 3.0）。
    /// </summary>
    public float Rule2MinTrailingSilence { get; init; } = 1.0f;

    /// <summary>
    /// 单次语音最大时长（秒），超过后强制结束（默认 30.0）。
    /// </summary>
    public float Rule3MinUtteranceLength { get; init; } = 30.0f;

    /// <summary>
    /// 语音识别超时时间（秒），超过此时间未说话则自动停止识别（默认 15.0）。
    /// </summary>
    public float RecognitionTimeoutSeconds { get; init; } = 1.0f;
}

/// <summary>
/// 语音合成（TTS）配置，使用 Sherpa-ONNX MeloTTS zh_en 中英双语模型。
/// </summary>
public sealed record TtsSettings
{
    /// <summary>
    /// 语速倍率，1.0 为正常速度（默认 1.0）。
    /// </summary>
    public float Speed { get; init; } = 1.0f;
}
