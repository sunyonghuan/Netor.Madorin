namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议终止信号，由主持人管理器实现。
/// </summary>
public interface IMeetingTerminationSignal
{
    /// <summary>标记会议应在下一轮终止。</summary>
    void SetTerminationFlag();
}
