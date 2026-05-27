using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.EventHub;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// 工作流任务详情视图（重构版 2026-05-26）。
///
/// 设计原则：
/// - 纯对话流，所有内容都是消息
/// - code-behind 只保留：工具授权按钮事件、Feed 滚动、EventHub 订阅
/// - 不再有独立的工具授权浮动面板（已内联到对话流中）
///
/// DataContext：WorkspaceTabVm
/// </summary>
public partial class WorkflowDetailView : UserControl
{
    private readonly WorkspaceTabVm _vm;
    private readonly WorkflowInputVm _inputVm;
    private readonly ISubscriber _subscriber;
    private readonly ILogger<WorkflowDetailView> _logger;

    public WorkflowDetailView()
    {
        InitializeComponent();

        _vm = App.Services.GetRequiredService<WorkspaceTabVm>();
        _inputVm = App.Services.GetRequiredService<WorkflowInputVm>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        _logger = App.Services.GetRequiredService<ILogger<WorkflowDetailView>>();

        DataContext = _vm;

        // 注入 WorkflowInputVm 到共享输入区
        WorkflowInputArea.SetInputVm(_inputVm);

        // Feed 有新消息时自动滚到底部 + 订阅卡片内容更新
        _vm.Detail.FeedItems.CollectionChanged += (_, args) =>
        {
            Dispatcher.UIThread.Post(ScrollFeedToEnd, DispatcherPriority.Background);

            // 新增的 step_card 消息：订阅 ContentUpdated 事件以自动滚动卡片内 ScrollViewer
            if (args.NewItems is not null)
            {
                foreach (var item in args.NewItems)
                {
                    if (item is ConversationMessageVm { IsStepCard: true } cardVm)
                    {
                        cardVm.ContentUpdated += () =>
                            Dispatcher.UIThread.Post(() => ScrollStepCard(cardVm), DispatcherPriority.Background);
                    }
                }
            }
        };

        // 订阅引擎生命周期事件 → 复位 InputVm 状态
        SubscribeTaskEvents();
    }

    // ──── EventHub 订阅 ────

    private void SubscribeTaskEvents()
    {
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineCompleted, (_, args) =>
        {
            if (args is null) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(_inputVm.CurrentTaskId) &&
                    string.Equals(_inputVm.CurrentTaskId, args.TaskId, StringComparison.Ordinal))
                {
                    _inputVm.OnTaskFinished();
                }
                ScrollFeedToEnd();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineFailed, (_, args) =>
        {
            if (args is null) return Task.FromResult(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(_inputVm.CurrentTaskId) &&
                    string.Equals(_inputVm.CurrentTaskId, args.TaskId, StringComparison.Ordinal))
                {
                    _inputVm.OnTaskFinished();
                }
            });
            return Task.FromResult(false);
        });
    }

    // ──── 工具授权按钮事件（内联在对话流中） ────

    private async void OnToolAuthConfirmClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var requestId = (sender as Button)?.Tag as string;
            var taskId = _vm.Detail?.TaskId;
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(requestId)) return;

            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            await engine.GrantToolAuthorizationAsync(taskId, requestId, CancellationToken.None);
            _logger.LogInformation("[WorkflowDetailView] 工具授权确认: {TaskId} RequestId={RequestId}", taskId, requestId);
        }
        catch (Exception ex) { _logger.LogError(ex, "[WorkflowDetailView] 工具授权操作失败"); }
    }

    private async void OnToolAuthGrantAllClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var msg = (sender as Button)?.Tag as ConversationMessageVm;
            var taskId = _vm.Detail?.TaskId;
            if (string.IsNullOrEmpty(taskId) || msg is null || string.IsNullOrEmpty(msg.AuthRequestId)) return;

            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            await engine.GrantAllToolAuthorizationAsync(taskId, msg.AuthRequestId, msg.ToolName ?? "", CancellationToken.None);
            _logger.LogInformation("[WorkflowDetailView] 工具全部授权: {TaskId} Tool={ToolName}", taskId, msg.ToolName);
        }
        catch (Exception ex) { _logger.LogError(ex, "[WorkflowDetailView] 工具授权操作失败"); }
    }

    private async void OnToolAuthDenyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var requestId = (sender as Button)?.Tag as string;
            var taskId = _vm.Detail?.TaskId;
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(requestId)) return;

            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            await engine.DenyToolAuthorizationAsync(taskId, requestId, CancellationToken.None);
            _logger.LogInformation("[WorkflowDetailView] 工具调用被拒绝: {TaskId} RequestId={RequestId}", taskId, requestId);
        }
        catch (Exception ex) { _logger.LogError(ex, "[WorkflowDetailView] 工具授权操作失败"); }
    }

    // ──── 步骤执行卡片折叠/展开 ────

    private void OnStepCardHeaderClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is ConversationMessageVm vm)
        {
            vm.IsExpanded = !vm.IsExpanded;
        }
    }

    // ──── 外部附件注入 API（供 LeftPanel 调用） ────

    /// <summary>
    /// 接受外部文件路径，添加到 WorkflowInputVm.Attachments。
    /// 由 MainWindow（LeftPanel.WorkflowAttachmentRequested）回调。
    /// </summary>
    public void AddExternalAttachments(IReadOnlyList<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (System.IO.Directory.Exists(path))
            {
                var scanner = new Netor.Cortana.Entitys.Services.FolderAttachmentScanner();
                var result = scanner.Scan(path);
                var folderName = System.IO.Path.GetFileName(
                    path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                _inputVm.Attachments.Add(new AttachmentInfo(path, folderName, "inode/directory",
                    IsFolder: true, FileCount: result.FileCount, TotalBytes: result.TotalBytes));
            }
            else if (System.IO.File.Exists(path))
            {
                _inputVm.Attachments.Add(new AttachmentInfo(path,
                    System.IO.Path.GetFileName(path),
                    FileContentTypeResolver.GetMimeType(path)));
            }
        }
    }

    // ──── 工具方法 ────

    private void ScrollFeedToEnd()
    {
        FeedScrollViewer?.ScrollToEnd();
    }

    /// <summary>
    /// 找到指定卡片 VM 对应的 ScrollViewer 并滚动到底部。
    /// 通过遍历 ItemsControl 可视树，匹配 DataContext 为目标 VM 的容器。
    /// </summary>
    private void ScrollStepCard(ConversationMessageVm cardVm)
    {
        try
        {
            // 遍历 FeedList 的子项，找到 DataContext 匹配的容器
            foreach (var container in FeedList.GetRealizedContainers())
            {
                if (container.DataContext != cardVm) continue;

                // 在容器可视树中查找 ScrollViewer
                var scroller = FindDescendant<ScrollViewer>(container);
                scroller?.ScrollToEnd();
                break;
            }
        }
        catch
        {
            // 可视树未就绪时忽略
        }
    }

    private static T? FindDescendant<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T target) return target;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
