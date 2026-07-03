using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议模式启动清理服务。
/// 应用启动时把上次未正常结束的会议标记为已散会。
/// </summary>
public sealed class MeetingStartupService : IHostedService
{
    private readonly MeetingSessionService _sessionService;
    private readonly IPublisher _publisher;
    private readonly ILogger<MeetingStartupService> _logger;

    public MeetingStartupService(
        MeetingSessionService sessionService,
        IPublisher publisher,
        ILogger<MeetingStartupService> logger)
    {
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var meetings = _sessionService.GetAllInProgress();
        if (meetings.Count == 0)
        {
            return;
        }

        _logger.LogInformation("会议模式检测到 {Count} 个未正常结束的会议，自动散会", meetings.Count);
        foreach (var meeting in meetings)
        {
            _sessionService.MarkAsAdjourned(meeting.Id);
            await _publisher.PublishAsync(
                Events.OnMeetingCancelled,
                new MeetingCancelledArgs(meeting.Id, "auto_adjourn_on_startup"));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
