using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.EventHub;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// 群聊（GroupChat）任务详情视图（重构版，2026-05-26）。
///
/// 重构变更：
/// - Row 1 输入区已替换为 <see cref="Controls.InputAreaView"/>（GroupChatInputArea）。
/// - 所有附件渲染、拖放、走马灯、Popup 填充逻辑已迁移到 InputAreaView code-behind。
/// - code-behind 只保留：详情区操作（取消任务、附加到对话）、步骤滚动、EventHub 订阅。
///
/// DataContext：
/// - 主体 DataContext = WorkspaceTabVm（构造函数注入）
/// - GroupChatInputArea.DataContext 由 InputAreaView.SetInputVm 自动设置
/// </summary>
public partial class GroupChatDetailView : UserControl
{
    private readonly WorkspaceTabVm _vm;
    private readonly GroupChatInputVm _inputVm;
    private readonly ISubscriber _subscriber;
    private readonly ILogger<GroupChatDetailView> _logger;

    public GroupChatDetailView()
    {
        InitializeComponent();

        _vm = App.Services.GetRequiredService<WorkspaceTabVm>();
        _inputVm = App.Services.GetRequiredService<GroupChatInputVm>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        _logger = App.Services.GetRequiredService<ILogger<GroupChatDetailView>>();

        DataContext = _vm;

        // 注入 GroupChatInputVm 到共享输入区
        GroupChatInputArea.SetInputVm(_inputVm);

        // 订阅 task.completed / task.failed → 复位 IsRunning
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
                ScrollStepsToEnd();
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

    // ──── 详情区操作 ────

    private async void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_inputVm.CurrentTaskId))
        {
            _logger.LogWarning("[GroupChatDetailView] 取消任务失败：CurrentTaskId 为空");
            return;
        }

        try
        {
            var engine = App.Services.GetRequiredService<AI.TaskEngine.TaskExecutionEngine>();
            var success = await engine.CancelTaskAsync(_inputVm.CurrentTaskId, CancellationToken.None);
            if (success)
            {
                _logger.LogInformation("[GroupChatDetailView] 任务取消请求已发送: {TaskId}", _inputVm.CurrentTaskId);
                _inputVm.OnTaskFinished();
            }
            else
            {
                _logger.LogWarning("[GroupChatDetailView] 取消任务失败：任务不存在或已结束: {TaskId}", _inputVm.CurrentTaskId);
            }
        }
        catch (Exception ex) { ShowError($"取消任务失败：{ex.Message}", ex); }
    }

    private void OnAttachToConversationClick(object? sender, RoutedEventArgs e)
    {
        // NOTE P4: BackflowService 已移除，GroupChat 模式的"附加到对话"功能待后续按需实现
        _logger.LogWarning("[GroupChatDetailView] 附加到对话功能待实现");
    }

    // ──── 步骤滚动 ────

    private void ScrollStepsToEnd()
    {
        StepsScrollViewer?.ScrollToEnd();
    }

    // ──── 外部附件注入 API（供 LeftPanel 调用） ────

    /// <summary>
    /// 接受外部文件路径，添加到 GroupChatInputVm.Attachments。
    /// 由 MainWindow（LeftPanel.WorkflowAttachmentRequested）回调。
    /// </summary>
    public void AddExternalAttachments(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
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

    private void ShowError(string message, Exception? ex = null)
    {
        if (ex is null)
            _logger.LogError("[GroupChatDetailView] {Message}", message);
        else
            _logger.LogError(ex, "[GroupChatDetailView] {Message}", message);
    }
}
