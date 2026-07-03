using System.Text.Json;

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.UI.Controls.Meeting;

/// <summary>
/// 左侧会议记录面板，展示当前会话下的会议并支持进入历史回放。
/// </summary>
public partial class MeetingHistoryPanel : UserControl
{
    private readonly List<MeetingSessionEntity> _loadedMeetings = [];
    private MeetingSessionService? _sessions;
    private ICurrentSessionResolver? _sessionResolver;
    private string _searchKeyword = string.Empty;
    private bool _subscribed;

    public MeetingHistoryPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event Action<string, string>? MeetingSelected;

    public void Reload()
    {
        MeetingItems.Items.Clear();
        _loadedMeetings.Clear();

        var workspaceId = _sessionResolver?.GetCurrentWorkspaceId();
        if (string.IsNullOrWhiteSpace(workspaceId) || _sessions is null)
        {
            EmptyText.IsVisible = true;
            return;
        }

        try
        {
            var meetings = _sessions.ListByWorkspace(workspaceId);
            if (!string.IsNullOrWhiteSpace(_searchKeyword))
            {
                meetings = meetings
                    .Where(m => m.Topic.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var meeting in meetings)
            {
                _loadedMeetings.Add(meeting);
                MeetingItems.Items.Add(CreateMeetingItem(meeting));
            }

            EmptyText.IsVisible = meetings.Count == 0;
        }
        catch (Exception ex)
        {
            EmptyText.IsVisible = true;
            App.Services.GetService<ILogger<MeetingHistoryPanel>>()
                ?.LogError(ex, "加载会议记录失败");
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _sessions ??= App.Services.GetService<MeetingSessionService>();
        _sessionResolver ??= App.Services.GetService<ICurrentSessionResolver>();

        if (!_subscribed)
        {
            _subscribed = true;
            var subscriber = App.Services.GetService<ISubscriber>();
            if (subscriber is not null)
            {
                subscriber.Subscribe<MeetingCreatedArgs>(Events.OnMeetingCreated, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<MeetingCompletedArgs>(Events.OnMeetingCompleted, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<MeetingCancelledArgs>(Events.OnMeetingCancelled, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
            }
        }

        Reload();
    }

    private Border CreateMeetingItem(MeetingSessionEntity meeting)
    {
        var participantCount = CountParticipants(meeting) + 1;
        var updatedTime = DateTimeOffset.FromUnixTimeMilliseconds(meeting.UpdatedAt).LocalDateTime;
        var status = meeting.Status switch
        {
            0 => "进行中",
            1 => "待回复",
            2 => "已结束",
            3 => "已散会",
            _ => "未知",
        };
        var statusColor = meeting.Status switch
        {
            0 => "#72A58A",
            1 => "#A06CFF",
            _ => "#6a6a6a",
        };
        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(meeting.Topic) ? "未命名会议" : meeting.Topic,
                    Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = $"{status}  {participantCount}位  {updatedTime:MM-dd HH:mm}",
                    Foreground = new SolidColorBrush(Color.Parse(statusColor)),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };
        var border = new Border
        {
            Classes = { "meeting-history-item" },
            Tag = meeting.Id,
            Child = textPanel,
        };
        border.PointerPressed += OnMeetingPressed;
        return border;
    }

    private void OnMeetingPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string meetingId })
        {
            return;
        }

        var meeting = _loadedMeetings.Find(m => string.Equals(m.Id, meetingId, StringComparison.Ordinal));
        if (meeting is not null)
        {
            MeetingSelected?.Invoke(meeting.Id, meeting.Topic);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        Reload();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var keyword = (textBox.Text ?? string.Empty).Trim();
        if (string.Equals(keyword, _searchKeyword, StringComparison.Ordinal))
        {
            return;
        }

        _searchKeyword = keyword;
        Reload();
    }

    private static int CountParticipants(MeetingSessionEntity meeting)
    {
        try
        {
            return JsonSerializer.Deserialize(
                meeting.ParticipantsJson,
                MeetingJsonContext.Default.ListMeetingParticipantDto)?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
