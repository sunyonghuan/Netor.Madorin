using NAudio.Wave;

using SherpaOnnx;

using System.Text.RegularExpressions;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 默认启动 MeloTTS
Console.WriteLine("正在启动 MeloTTS 语音合成试听程序...\n");

// ──────────────────── 模型路径 ────────────────────

string modelDir = Path.Combine(AppContext.BaseDirectory, "sherpa_models", "TTS");
string modelPath = Path.Combine(modelDir, "model.int8.onnx");
string tokensPath = Path.Combine(modelDir, "tokens.txt");
string lexiconPath = Path.Combine(modelDir, "lexicon.txt");

foreach (var (path, name) in new[] { (modelPath, "model"), (tokensPath, "tokens"), (lexiconPath, "lexicon") })
{
    if (!File.Exists(path))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"错误：TTS {name} 文件不存在：{path}");
        Console.ResetColor();
        return 1;
    }
}

// ──────────────────── 默认参数 ────────────────────

const string DefaultText = "这是一个中英文混合的 text to speech 测试例子。";
float speed = 1.0f;

// ──────────────────── 初始化 MeloTTS 引擎（VITS 架构） ────────────────────

Console.WriteLine("正在初始化 MeloTTS zh_en 引擎...");

var config = new OfflineTtsConfig();
config.Model.Vits.Model = modelPath;
config.Model.Vits.Tokens = tokensPath;
config.Model.Vits.Lexicon = lexiconPath;
config.Model.Vits.DictDir = Path.Combine(modelDir, "dict");
config.Model.NumThreads = Environment.ProcessorCount;
config.Model.Provider = "cpu";
config.Model.Debug = 0;
config.RuleFsts = string.Join(",",
    Path.Combine(modelDir, "phone.fst"),
    Path.Combine(modelDir, "date.fst"),
    Path.Combine(modelDir, "number.fst"),
    Path.Combine(modelDir, "new_heteronym.fst"));

using var tts = new OfflineTts(config);

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"\rMeloTTS zh_en 引擎已就绪 — 采样率: {tts.SampleRate}Hz");
Console.ResetColor();
Console.WriteLine();
PrintHelp();

// ──────────────────── 交互式循环 ────────────────────

while (true)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"[语速={speed:F1}] 输入文本或按 Enter 试听 > ");
    Console.ResetColor();

    string? input = Console.ReadLine();
    if (input is null) break; // Ctrl+Z / EOF

    string trimmed = input.Trim();

    // 空输入：使用默认文本试听
    if (string.IsNullOrEmpty(trimmed))
    {
        Console.WriteLine($"试听文本：\"{DefaultText}\"");
        trimmed = DefaultText;
    }

    // 退出命令
    if (trimmed is "exit" or "quit" or "q")
        break;

    // 帮助命令
    if (trimmed is "help" or "h" or "?")
    {
        PrintHelp();
        continue;
    }

    // 设置语速
    if (trimmed.StartsWith("spd ", StringComparison.OrdinalIgnoreCase))
    {
        if (float.TryParse(trimmed[4..].Trim(), out float newSpeed) && newSpeed > 0f && newSpeed <= 5f)
        {
            speed = newSpeed;
            Console.WriteLine($"语速已设置为 {speed:F1}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("无效的语速值，有效范围: 0.1 ~ 5.0");
            Console.ResetColor();
        }
        continue;
    }

    // 合成并播放
    string? sanitized = TtsTextHelper.SanitizeForTts(trimmed);
    if (string.IsNullOrEmpty(sanitized))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("输入文本清洗后为空（不含可朗读字符），跳过。");
        Console.ResetColor();
        continue;
    }

    try
    {
        Console.Write("合成中...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var audio = tts.Generate(sanitized, speed, 0);
        sw.Stop();

        if (audio?.Samples is not null && audio.Samples.Length > 0)
        {
            float[] samples = audio.Samples;
            GC.KeepAlive(audio);

            float durationSec = (float)samples.Length / tts.SampleRate;
            Console.Write($"\r合成耗时: {sw.ElapsedMilliseconds}ms，音频时长: {durationSec:F1}s，播放中...");
            PlaySamples(samples, tts.SampleRate);
            Console.WriteLine("\r播放完成。" + new string(' ', 40));
        }
        else
        {
            Console.WriteLine("\r合成结果为空。");
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\r合成/播放出错: {ex.Message}");
        Console.ResetColor();
    }
}

Console.WriteLine("再见！");
return 0;

// ──────────────────── 辅助方法 ────────────────────

static void PrintHelp()
{
    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine("  MeloTTS zh_en 试听控制台（中英双语，单说话人）");
    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine("  输入文本直接合成播放，支持中英文混合。");
    Console.WriteLine("  空输入使用默认文本试听。");
    Console.WriteLine();
    Console.WriteLine("  命令：");
    Console.WriteLine("    spd <值>     设置语速倍率（如：spd 1.2）");
    Console.WriteLine("    help         显示帮助");
    Console.WriteLine("    exit         退出");
    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine();
}

static void PlaySamples(float[] samples, int sampleRate)
{
    using var waveOut = new WaveOutEvent();
    var provider = new FloatArrayWaveProvider(samples, sampleRate);
    using var done = new ManualResetEventSlim(false);

    waveOut.PlaybackStopped += (_, _) => done.Set();
    waveOut.Init(provider);
    waveOut.Play();

    done.Wait();
}

// ──────────────────── 文本清洗 ────────────────────

internal static partial class TtsTextHelper
{
    private static readonly Regex MarkdownPattern = new(
        @"```[\s\S]*?```|`[^`]+`|!?\[[^\]]*\]\([^)]*\)|#{1,6}\s|\*{1,3}|_{1,3}|~~|>\s|\||-{3,}|={3,}",
        RegexOptions.Compiled);

    private static readonly Regex SpeakablePattern = new(
        @"[\u4e00-\u9fff\u3040-\u309f\u30a0-\u30ff\uac00-\ud7afa-zA-Z0-9]",
        RegexOptions.Compiled);

    public static string? SanitizeForTts(string text)
    {
        string cleaned = MarkdownPattern.Replace(text, " ");
        cleaned = cleaned.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();

        if (!SpeakablePattern.IsMatch(cleaned))
            return null;

        return cleaned;
    }
}

// ──────────────────── 辅助类 ────────────────────

/// <summary>
/// 将 float[] PCM 采样数据包装为 NAudio IWaveProvider，供 WaveOutEvent 播放。
/// </summary>
internal sealed class FloatArrayWaveProvider : IWaveProvider
{
    private readonly float[] _samples;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public FloatArrayWaveProvider(float[] samples, int sampleRate)
    {
        _samples = samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesAvailable = _samples.Length - _position;
        int bytesAvailable = samplesAvailable * sizeof(float);
        int bytesToCopy = Math.Min(count, bytesAvailable);
        int samplesToCopy = bytesToCopy / sizeof(float);

        if (samplesToCopy <= 0) return 0;

        Buffer.BlockCopy(_samples, _position * sizeof(float), buffer, offset, samplesToCopy * sizeof(float));
        _position += samplesToCopy;

        return samplesToCopy * sizeof(float);
    }
}