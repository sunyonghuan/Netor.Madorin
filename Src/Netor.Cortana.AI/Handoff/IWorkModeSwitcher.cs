namespace Netor.Cortana.AI.Handoff;

/// <summary>
/// UI 侧实现的工作模式切换接口。
/// </summary>
public interface IWorkModeSwitcher
{
    void SwitchToWorkMode(string taskId);
}
