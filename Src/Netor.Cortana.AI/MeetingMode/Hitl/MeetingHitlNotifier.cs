using Netor.Cortana.AI.Hitl;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.MeetingMode.Hitl;

/// <summary>
/// 会议模式 HITL 事件通知器。
/// </summary>
public sealed class MeetingHitlNotifier : IHitlNotifier
{
    private readonly IPublisher _publisher;

    public MeetingHitlNotifier(IPublisher publisher)
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
            Events.OnMeetingAskUserRequested,
            new MeetingAskUserRequestedArgs(contextId, requestId, question));
    }
}
