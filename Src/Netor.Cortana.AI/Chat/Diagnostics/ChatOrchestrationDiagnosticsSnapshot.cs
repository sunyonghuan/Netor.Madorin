using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 专家模式当前 turn 的编排诊断快照。
/// 仅用于内部观测与开发态 UI 展示，不参与外部事件契约。
/// </summary>
public sealed record ChatOrchestrationDiagnosticsSnapshot(
    string SessionId,
    string TurnId,
    string TraceId,
    ConversationTurnStatus Status,
    AgentOrchestrationMode Mode,
    IReadOnlyList<string> AgentIds,
    IReadOnlyList<string> Warnings,
    DateTimeOffset UpdatedAt,
    bool IsCompleted);
