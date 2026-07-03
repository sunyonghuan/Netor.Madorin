namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 定义 Chat 模式下的智能体编排模式。
/// Workflow 模式的并行/群聊编排不走这里。
/// </summary>
public enum AgentOrchestrationMode
{
    None = 0,
    ToolDelegation = 1,
    HandoffChat = 2,
}
