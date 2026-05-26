using Netor.Cortana.Entitys;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

namespace Netor.Cortana.Voice;

/// <summary>
/// 语音子系统协调器。聚合"取消所有进行中的语音/AI 任务"的统一入口。
/// 用户在主窗体上做出"发送"或"停止"决策时调用。
/// </summary>
public interface IVoiceCoordinator
{
    /// <summary>
    /// 取消所有进行中的语音/AI 任务：当前 STT 轮次、AI 推理、TTS 流水线，
    /// 并通知 UI 层（气泡/指示器）收尾。
    /// 该方法是同步阻塞的，会等待识别线程退出后才返回。
    /// </summary>
    void CancelEverything();
}

internal sealed class VoiceCoordinator(
    SpeechRecognitionService stt,
    IPublisher publisher) : IVoiceCoordinator
{
    public void CancelEverything()
    {
        // STT 内部的 CancelCurrentRound 已经会同步取消 AI + TTS，
        // 这里只需调它一次即可，避免重复关流水线。
        stt.CancelCurrentRound();

        // 显式发出 Stopped 信号，确保 UI 气泡能收尾，
        // 即使 RecognitionLoop 的 finally 因竞争错过这一帧也兜底一次。
        publisher.Publish(Events.OnSttStopped, new VoiceSignalArgs());
    }
}