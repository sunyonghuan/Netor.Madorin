namespace Netor.Cortana.Entitys;

/// <summary>
/// UI 专用的实时过程输出通道。
/// </summary>
public interface IRealtimeProcessOutput
{
    /// <summary>
    /// 接收实时过程事件。
    /// </summary>
    Task OnProcessEventAsync(RealtimeProcessEvent evt, CancellationToken ct = default);
}
