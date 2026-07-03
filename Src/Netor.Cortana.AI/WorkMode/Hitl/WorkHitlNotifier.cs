using Netor.Cortana.AI.Hitl;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.Hitl;

/// <summary>
/// 工作模式 HITL 事件通知器。
/// </summary>
public sealed class WorkHitlNotifier : IHitlNotifier
{
    private readonly IPublisher _publisher;

    public WorkHitlNotifier(IPublisher publisher)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public Task PublishAskUserAsync(
        string contextId,
        string requestId,
        string question,
        CancellationToken cancellationToken = default)
    {
        return _publisher.PublishAsync(
            Events.OnWorkAskUserRequested,
            new WorkAskUserRequestedArgs(contextId, requestId, question));
    }
}
