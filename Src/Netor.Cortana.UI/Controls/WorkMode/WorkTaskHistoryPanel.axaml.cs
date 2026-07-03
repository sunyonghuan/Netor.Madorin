using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工作记录面板，承载当前工作区的工作模式任务列表。
/// </summary>
public partial class WorkTaskHistoryPanel : UserControl
{
    private readonly List<WorkTaskEntity> _loadedTasks = [];
    private WorkTaskService? _taskService;
    private WorkTaskFileService? _fileService;
    private ICurrentSessionResolver? _sessionResolver;
    private string _searchKeyword = string.Empty;
    private bool _subscribed;

    public WorkTaskHistoryPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 点击任务时触发，参数为任务 ID 和标题。
    /// </summary>
    public event Action<string, string>? TaskSelected;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _taskService ??= App.Services.GetService<WorkTaskService>();
        _fileService ??= App.Services.GetService<WorkTaskFileService>();
        _sessionResolver ??= App.Services.GetService<ICurrentSessionResolver>();

        if (!_subscribed)
        {
            _subscribed = true;
            var subscriber = App.Services.GetService<ISubscriber>();
            if (subscriber is not null)
            {
                subscriber.Subscribe<WorkTaskCreatedArgs>(Events.OnWorkTaskCreated, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<WorkTaskCompletedArgs>(Events.OnWorkTaskCompleted, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<WorkTaskTitleUpdatedArgs>(Events.OnWorkTaskTitleUpdated, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<WorkTaskFailedArgs>(Events.OnWorkTaskFailed, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<WorkTaskCancelledArgs>(Events.OnWorkTaskCancelled, (_, _) =>
                {
                    Dispatcher.UIThread.Post(Reload);
                    return Task.FromResult(false);
                });
                subscriber.Subscribe<WorkTaskPausedArgs>(Events.OnWorkTaskPaused, (_, _) =>
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

    /// <summary>
    /// 重新加载当前工作区的任务列表。
    /// </summary>
    public void Reload()
    {
        TaskItems.Items.Clear();
        _loadedTasks.Clear();

        var workspaceId = _sessionResolver?.GetCurrentWorkspaceId();
        if (string.IsNullOrWhiteSpace(workspaceId) || _taskService is null)
        {
            EmptyText.IsVisible = true;
            return;
        }

        try
        {
            var tasks = _taskService.ListByWorkspace(workspaceId, take: 80);
            if (!string.IsNullOrWhiteSpace(_searchKeyword))
            {
                tasks = tasks
                    .Where(t => t.Title.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase)
                        || t.InitialInput.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var task in tasks)
            {
                _loadedTasks.Add(task);
                TaskItems.Items.Add(CreateTaskItem(task));
            }

            EmptyText.IsVisible = tasks.Count == 0;
        }
        catch (Exception ex)
        {
            EmptyText.IsVisible = true;
            var logger = App.Services.GetService<ILogger<WorkTaskHistoryPanel>>();
            logger?.LogError(ex, "加载工作任务列表失败");
        }
    }

    private Border CreateTaskItem(WorkTaskEntity task)
    {
        var title = string.IsNullOrWhiteSpace(task.Title) ? "未命名任务" : task.Title;
        var status = FormatTaskStatus(task, _fileService?.LoadPlan(task.Id)?.Status);
        var updatedTime = DateTimeOffset.FromUnixTimeMilliseconds(task.LastActiveAt).LocalDateTime;

        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var metaBlock = new TextBlock
        {
            Text = $"{status}  {updatedTime:MM-dd HH:mm}",
            Foreground = new SolidColorBrush(Color.Parse(task.IsActive ? "#8FAF9F" : "#6a6a6a")),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleBlock,
                metaBlock,
            }
        };

        var border = new Border
        {
            Classes = { "work-task-item" },
            Tag = task.Id,
            Child = textPanel,
        };
        border.PointerPressed += OnTaskPressed;
        return border;
    }

    private static string FormatTaskStatus(WorkTaskEntity task, string? planStatus)
    {
        if (!task.IsActive)
        {
            return "已关闭";
        }

        return task.OrchestratorState switch
        {
            WorkTaskOrchestratorStates.Running => "执行中",
            WorkTaskOrchestratorStates.Paused => "已暂停",
            _ => planStatus switch
            {
                WorkTaskPlanStatuses.Planning => "待执行计划",
                WorkTaskPlanStatuses.Running => "执行中",
                WorkTaskPlanStatuses.Paused => "已暂停",
                WorkTaskPlanStatuses.Done => "本轮已完成",
                WorkTaskPlanStatuses.Failed => "本轮失败",
                WorkTaskPlanStatuses.Cancelled => "本轮已取消",
                _ => "可继续",
            },
        };
    }

    private void OnTaskPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string taskId })
        {
            return;
        }

        var task = _loadedTasks.Find(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        if (task is null)
        {
            return;
        }

        TaskSelected?.Invoke(task.Id, task.Title);
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
}
