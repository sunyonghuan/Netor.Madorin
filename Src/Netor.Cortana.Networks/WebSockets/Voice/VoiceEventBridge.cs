using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// voice.* 事件桥接器：把 PluginBusBroadcastDispatcher 转发的 voice 事件
/// 桥接到宿主 IPublisher 的 Events.OnXxx，UI 层（字幕 / 桌宠 / 状态）零感知。
/// 取代原 PluginBusVoiceEventDispatcher 的拦截器角色（不再做帧识别）。
/// 详见 docs/已完成功能规划/插件事件广播/03-宿主转发器实现.md §三。
/// </summary>
internal sealed class VoiceEventBridge
{
    private readonly IPublisher _publisher;
    private readonly ILogger _logger;

    public VoiceEventBridge(IPublisher publisher, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);
        _publisher = publisher;
        _logger = logger;
    }

    /// <summary>
    /// 由 PluginBusBroadcastDispatcher.HandleAsync 在同步阶段调用，frame 在调用期间有效；
    /// 本方法是同步方法、不持有 frame 引用，方法返回后调用方释放 doc 也安全。
    /// </summary>
    public void Bridge(string op, JsonElement frame)
    {
        op = NormalizeVoiceOp(op);

        var payload = frame.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : frame;
        var text = ReadString(payload, "text")
            ?? ReadString(payload, "data")
            ?? ReadString(frame, "text")
            ?? ReadString(frame, "data")
            ?? string.Empty;

        switch (op)
        {
            case "voice.kws.detected.v1":
                _publisher.Publish(Events.OnWakeWordDetected, new VoiceSignalArgs());
                break;

            case "voice.stt.partial.v1":
                _publisher.Publish(Events.OnSttPartial, new VoiceTextArgs(text));
                break;

            case "voice.stt.final.v1":
                _publisher.Publish(Events.OnSttFinal, new VoiceTextArgs(text));
                break;

            case "voice.stt.stopped.v1":
                _publisher.Publish(Events.OnSttStopped, new VoiceSignalArgs());
                break;

            case "voice.tts.started.v1":
                _publisher.Publish(Events.OnTtsStarted, new VoiceSignalArgs());
                break;

            case "voice.tts.subtitle.v1":
                _publisher.Publish(Events.OnTtsSubtitle, new VoiceTextArgs(text));
                break;

            case "voice.tts.completed.v1":
                _publisher.Publish(Events.OnTtsCompleted, new VoiceSignalArgs());
                break;

            // F7：voice.tts.greeting_ready.v1 业务上仅通知 TTS 模型已就绪，宿主侧无 UI 联动需求；
            // 显式列 case + LogDebug，避免落 default 分支被误判为未识别 op
            case "voice.tts.greeting_ready.v1":
                _logger.LogDebug("Voice greeting ready (no UI bridge by design). Op={Op}", op);
                break;

            // F7：voice.tts.error.v1 显式列 case，对齐旧 dispatcher .error 后缀分支
            case "voice.tts.error.v1":
                _logger.LogWarning("Voice TTS plugin reported error. Op={Op} Text={Text}", op, text);
                break;

            default:
                if (op.EndsWith(".error.v1", StringComparison.Ordinal)
                    || op.EndsWith(".error", StringComparison.Ordinal))
                {
                    var message = string.IsNullOrWhiteSpace(text) ? "语音插件上报错误" : text;
                    _logger.LogWarning("Voice plugin reported error. Op={Op} Message={Message}", op, message);
                    return;
                }
                _logger.LogDebug("Unmapped voice op (forward only). Op={Op}", op);
                break;
        }
    }

    /// <summary>
    /// PluginBus 1.4.0 OQ5：归一化 voice op 到 .vN 后缀。
    /// 旧版仓库三件套发包不带 .v1 后缀（如 voice.tts.subtitle），
    /// 宿主升级后 case 表全是 voice.xxx.v1 形式，未归一化会落 default 分支导致 UI 字幕静默失声。
    /// 规则：op 末段以 v 开头紧跟数字（如 .v1）则原样返回；否则追加 .v1。
    /// </summary>
    internal static string NormalizeVoiceOp(string op)
    {
        if (string.IsNullOrEmpty(op))
        {
            return op;
        }

        var lastDot = op.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < op.Length)
        {
            var tail = op.AsSpan(lastDot + 1);
            if (tail.Length >= 2 && tail[0] == 'v' && char.IsDigit(tail[1]))
            {
                return op;
            }
        }

        return op + ".v1";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }
}
