using Microsoft.Agents.AI;

namespace Netor.Cortana.AI;

/// <summary>
/// 当前专家模式 turn 的运行态上下文。
/// 阶段 3.5 先承接取消、完成去重与 turn 级资源，后续阶段再继续承接执行职责。
/// </summary>
public sealed record class ChatTurnContext(
    string TurnId,
    AIAgent? Agent,
    AgentSession? Session,
    CancellationTokenSource Cts)
{
    private int _cancelNotificationRequested;
    private int _completionPublished;

    public ConversationEventMetadata? Metadata { get; set; }

    public bool IsCompletionPublished => Volatile.Read(ref _completionPublished) != 0;

    public bool TryMarkCancellationNotified()
    {
        return Interlocked.Exchange(ref _cancelNotificationRequested, 1) == 0;
    }

    public bool TryMarkCompletionPublished()
    {
        return Interlocked.CompareExchange(ref _completionPublished, 1, 0) == 0;
    }

    public void Cancel()
    {
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
