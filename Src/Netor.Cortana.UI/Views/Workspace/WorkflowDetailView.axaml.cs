using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.EventHub;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// P4 三面板布局任务详情视图（2026-05-24 重写）。
///
/// 布局：
/// - Row 0: 标题栏（任务标题 + 状态 + 操作按钮）
/// - Row 1: 主内容区（左: 计划步骤概览 | 右: 时间线事件流）
/// - Row 2: 底部输入工具栏（智能体/厂商/模型/工具选择器）
///
/// DataContext:
/// - 根 = WorkspaceTabVm
/// - InputArea.DataContext = WorkflowInputVm
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
        InputArea.DataContext = _inputVm;

        // 监听 VM 属性变化 → 同步 Label
        _inputVm.PropertyChanged += OnInputVmPropertyChanged;

        // 订阅 task.completed / task.failed 事件 → 复位 _inputVm.IsRunning
        _subscriber.Subscribe<WorkflowTaskCompletedArgs>(Events.OnWorkflowTaskCompleted, (_, args) =>
        {
            if (args.TaskId == _inputVm.CurrentTaskId)
                Dispatcher.UIThread.Post(() => _inputVm.OnTaskFinished());
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkflowTaskFailedArgs>(Events.OnWorkflowTaskFailed, (_, args) =>
        {
            if (args.TaskId == _inputVm.CurrentTaskId)
                Dispatcher.UIThread.Post(() => _inputVm.OnTaskFinished());
            return Task.FromResult(false);
        });

        // 控件加载完成后填充 Popup 列表
        AttachedToVisualTree += (_, _) =>
        {
            FillAgentSelectorList();
            FillProviderSelectorList();
            FillModelSelectorList();
            RefreshAllLabels();
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    // 输入框：发送 / 停止 / 快捷键
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>"▶ 发送" 按钮：调 _inputVm.StartAsync 启动任务。</summary>
    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        // NativeAOT 下绑定可能滞后，显式同步一次
        if (InputBox is not null)
            _inputVm.InitialInput = InputBox.Text ?? string.Empty;

        _logger.LogInformation(
            "[WorkflowDetailView] OnSendClick: WorkspaceId={WorkspaceId}, Manager={Manager}, Provider={Provider}, Model={Model}, Input.Len={InputLen}",
            _vm.List.WorkspaceId,
            _inputVm.SelectedManager?.Name ?? "(null)",
            _inputVm.SelectedProvider?.Name ?? "(null)",
            _inputVm.SelectedModel?.Name ?? "(null)",
            (_inputVm.InitialInput ?? string.Empty).Length);

        try
        {
            _inputVm.WorkspaceId = _vm.List.WorkspaceId ?? string.Empty;
            var taskId = await _inputVm.StartAsync(CancellationToken.None);
            if (string.IsNullOrEmpty(taskId))
            {
                var hint = !string.IsNullOrWhiteSpace(_inputVm.ValidationError)
                    ? _inputVm.ValidationError
                    : "无法启动任务：请检查智能体 / 厂商 / 模型是否已选 + 输入内容是否为空。";
                _logger.LogWarning("[WorkflowDetailView] StartAsync 返回 null（校验失败）：{Hint}", hint);
                return;
            }

            // 清空输入框
            if (InputBox is not null)
                InputBox.Text = string.Empty;

            await _vm.ShowTaskAsync(taskId);
            _logger.LogInformation("[WorkflowDetailView] Workflow task started: taskId={TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WorkflowDetailView] 启动任务失败");
        }
    }

    /// <summary>"⏹ 停止" 按钮。</summary>
    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        try { await _inputVm.StopAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "[WorkflowDetailView] 停止任务失败"); }
    }

    /// <summary>Enter 发送 / Shift+Enter 换行。</summary>
    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_inputVm.IsIdle && _inputVm.CanSubmit)
            OnSendClick(sender, new RoutedEventArgs());
    }

    // ══════════════════════════════════════════════════════════════════════
    // 任务操作按钮
    // ══════════════════════════════════════════════════════════════════════

    private async void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm.CurrentTaskId is null)
        {
            _logger.LogWarning("P4 取消任务失败：CurrentTaskId 为空");
            return;
        }

        var engine = App.Services.GetRequiredService<AI.TaskEngine.TaskExecutionEngine>();
        var success = await engine.CancelTaskAsync(_inputVm.CurrentTaskId, CancellationToken.None);

        if (success)
        {
            _logger.LogInformation("P4 任务取消请求已发送: {TaskId}", _inputVm.CurrentTaskId);
            _inputVm.OnTaskFinished();
        }
        else
        {
            _logger.LogWarning("P4 取消任务失败：任务不存在或已结束: {TaskId}", _inputVm.CurrentTaskId);
        }
    }

    private async void OnPauseTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm.CurrentTaskId is null) return;

        var engine = App.Services.GetRequiredService<AI.TaskEngine.TaskExecutionEngine>();
        var success = await engine.PauseTaskAsync(_inputVm.CurrentTaskId, CancellationToken.None);

        if (success)
            _logger.LogInformation("P4 暂停请求已发送: {TaskId}", _inputVm.CurrentTaskId);
        else
            _logger.LogWarning("P4 暂停失败：任务不存在或已暂停: {TaskId}", _inputVm.CurrentTaskId);
    }

    private async void OnResumeTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm.CurrentTaskId is null) return;

        var engine = App.Services.GetRequiredService<AI.TaskEngine.TaskExecutionEngine>();
        var success = await engine.ResumeTaskAsync(_inputVm.CurrentTaskId, CancellationToken.None);

        if (success)
            _logger.LogInformation("P4 恢复请求已发送: {TaskId}", _inputVm.CurrentTaskId);
        else
            _logger.LogWarning("P4 恢复失败：任务不存在或未暂停: {TaskId}", _inputVm.CurrentTaskId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 计划面板
    // ══════════════════════════════════════════════════════════════════════

    private void OnTogglePlanOverview(object? sender, RoutedEventArgs e)
    {
        if (_vm.Detail is not null)
            _vm.Detail.IsPlanOverviewExpanded = !_vm.Detail.IsPlanOverviewExpanded;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 时间线卡片操作
    // ══════════════════════════════════════════════════════════════════════

    private async void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string actionId) return;
        if (_inputVm.CurrentTaskId is null) return;

        var engine = App.Services.GetRequiredService<AI.TaskEngine.TaskExecutionEngine>();

        switch (actionId)
        {
            case "confirm_plan":
                await engine.ConfirmPlanAsync(_inputVm.CurrentTaskId, CancellationToken.None);
                _logger.LogInformation("P4 用户确认计划: {TaskId}", _inputVm.CurrentTaskId);
                break;

            case "reject_plan":
                await engine.CancelTaskAsync(_inputVm.CurrentTaskId, CancellationToken.None);
                _inputVm.OnTaskFinished();
                _logger.LogInformation("P4 用户拒绝计划（取消任务）: {TaskId}", _inputVm.CurrentTaskId);
                break;

            case "modify_plan":
                // TODO P4-Phase2: 弹出修改意见输入框，调用 RequestPlanModificationAsync
                _logger.LogInformation("P4 用户请求修改计划（待实现交互）: {TaskId}", _inputVm.CurrentTaskId);
                break;

            default:
                _logger.LogDebug("P4 未知卡片动作: {ActionId}", actionId);
                break;
        }
    }

    /// <summary>时间线追加事件后自动滚动到底部。</summary>
    public void ScrollTimelineToEnd()
    {
        TimelineScrollViewer?.ScrollToEnd();
    }

    // ══════════════════════════════════════════════════════════════════════
    // 底部工具栏：Popup 选择器
    // ══════════════════════════════════════════════════════════════════════

    private void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        _ = AttachFilesAsync();
    }

    private async Task AttachFilesAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "选择附件",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*.*"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("文档文件") { Patterns = ["*.pdf", "*.doc", "*.docx", "*.txt", "*.md", "*.csv", "*.xlsx", "*.pptx"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("脚本与源码") { Patterns = ["*.cs", "*.py", "*.js", "*.ts", "*.json", "*.yml", "*.yaml", "*.xml"] },
                    ],
                });

            foreach (var file in files)
            {
                var path = file.Path.LocalPath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var name = file.Name;
                var mimeType = FileContentTypeResolver.GetMimeType(path);
                _inputVm.Attachments.Add(new AttachmentInfo(path, name, mimeType));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择附件失败");
        }
    }

    /// <summary>P3-2：外部（WorkspaceExplorer 右键菜单）将文件/文件夹路径列表推送到工作流附件。</summary>
    public void AddExternalAttachments(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (Directory.Exists(path))
            {
                var scanner = new Entitys.Services.FolderAttachmentScanner();
                var result = scanner.Scan(path);
                var folderName = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                _inputVm.Attachments.Add(new AttachmentInfo(path, folderName, "inode/directory",
                    IsFolder: true, FileCount: result.FileCount, TotalBytes: result.TotalBytes));
            }
            else if (System.IO.File.Exists(path))
            {
                _inputVm.Attachments.Add(new AttachmentInfo(path, System.IO.Path.GetFileName(path), FileContentTypeResolver.GetMimeType(path)));
            }
        }
    }

    private void OnToolPopupOpenClick(object? sender, RoutedEventArgs e)
    {
        ToolPopup.IsOpen = !ToolPopup.IsOpen;
    }

    // ──── 智能体选择器 ────

    private void OnAgentSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillAgentSelectorList();
        AgentSelectorPopup.IsOpen = !AgentSelectorPopup.IsOpen;
    }

    private void FillAgentSelectorList()
    {
        AgentSelectorList.Items.Clear();
        var activeId = _inputVm.SelectedManager?.Id;
        foreach (var agent in _inputVm.AvailableAgents)
        {
            var id = agent.Id;
            var isActive = id == activeId;
            var btn = new Button
            {
                Classes = { isActive ? "selector-item-active" : "selector-item" },
                Tag = id,
                Content = new TextBlock
                {
                    Text = agent.Name,
                    FontSize = 12,
                    Foreground = isActive
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnAgentItemClick;
            AgentSelectorList.Items.Add(btn);
        }
    }

    private void OnAgentItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string agentId)
        {
            var agent = _inputVm.AvailableAgents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null) return;

            _inputVm.SelectedManager = agent;
            AgentSelectorPopup.IsOpen = false;

            FillAgentSelectorList();
            FillProviderSelectorList();
            FillModelSelectorList();
        }
    }

    // ──── 厂商选择器 ────

    private void OnProviderSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillProviderSelectorList();
        ProviderPopup.IsOpen = !ProviderPopup.IsOpen;
    }

    private void FillProviderSelectorList()
    {
        ProviderList.Items.Clear();
        var activeId = _inputVm.SelectedProvider?.Id;
        foreach (var provider in _inputVm.AvailableProviders)
        {
            var id = provider.Id;
            var isActive = id == activeId;
            var btn = new Button
            {
                Classes = { isActive ? "selector-item-active" : "selector-item" },
                Tag = id,
                Content = new TextBlock
                {
                    Text = provider.Name,
                    FontSize = 12,
                    Foreground = isActive
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnProviderItemClick;
            ProviderList.Items.Add(btn);
        }
    }

    private void OnProviderItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string providerId)
        {
            var provider = _inputVm.AvailableProviders.FirstOrDefault(p => p.Id == providerId);
            if (provider is null) return;

            _inputVm.SelectedProvider = provider;
            ProviderPopup.IsOpen = false;

            FillProviderSelectorList();
            FillModelSelectorList();
        }
    }

    // ──── 模型选择器 ────

    private void OnModelSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillModelSelectorList();
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    private void FillModelSelectorList()
    {
        ModelList.Items.Clear();
        var activeId = _inputVm.SelectedModel?.Id;
        foreach (var model in _inputVm.AvailableModels)
        {
            var id = model.Id;
            var isActive = id == activeId;
            var display = !string.IsNullOrWhiteSpace(model.DisplayName) ? model.DisplayName : model.Name;
            var btn = new Button
            {
                Classes = { isActive ? "selector-item-active" : "selector-item" },
                Tag = id,
                Content = new TextBlock
                {
                    Text = display,
                    FontSize = 12,
                    Foreground = isActive
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnModelItemClick;
            ModelList.Items.Add(btn);
        }
    }

    private void OnModelItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string modelId)
        {
            var model = _inputVm.AvailableModels.FirstOrDefault(m => m.Id == modelId);
            if (model is null) return;

            _inputVm.SelectedModel = model;
            ModelPopup.IsOpen = false;
            FillModelSelectorList();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 属性变化同步
    // ══════════════════════════════════════════════════════════════════════

    private void OnInputVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(WorkflowInputVm.SelectedManagerName):
                case nameof(WorkflowInputVm.SelectedProviderName):
                case nameof(WorkflowInputVm.SelectedModelName):
                    RefreshAllLabels();
                    break;
            }
        });
    }

    private void RefreshAllLabels()
    {
        ToolbarAgentLabel.Text = _inputVm.SelectedManagerName;
        ToolbarProviderLabel.Text = _inputVm.SelectedProviderName;
        ToolbarModelLabel.Text = _inputVm.SelectedModelName;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 工具方法
    // ══════════════════════════════════════════════════════════════════════

    private void ShowError(string message, Exception? ex = null)
    {
        if (ex is null)
            _logger.LogError("[WorkflowDetailView] {Message}", message);
        else
            _logger.LogError(ex, "[WorkflowDetailView] {Message}", message);
    }
}
