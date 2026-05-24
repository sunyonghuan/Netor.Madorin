using System.ComponentModel;
using System.Diagnostics;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.EventHub;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// P2-1：工作流任务详情视图（条款 1-12 重写版，2026-05-17）。
///
/// 设计原则：完全复刻 Chat 输入框（MainWindow.axaml line 263-500）的视觉与交互。
///
/// P4 重构说明（2026-05-24）：
/// 输入区（InputBox / SendButton / StopButton / SpinnerIcon / InputBorder / InputBorderHost /
/// InputMarqueeBorder / InputMarqueeTop/Right/Bottom/Left / SubModeLabel / MaxSubAgentsBtn /
/// MaxSubAgentsLabel / AttachmentList / FilePopup / FileList）已移至独立组件，
/// 相关方法体已清空，待重新接入。
/// </summary>
public partial class WorkflowDetailView : UserControl
{
    /// <summary>当前控件持有的 VM 引用（与 TaskListPanel 同源 Singleton）。</summary>
    private readonly WorkspaceTabVm _vm;

    /// <summary>P2-1：聊天式输入框 VM。从 DI 解析（Singleton）。</summary>
    private readonly WorkflowInputVm _inputVm;

    /// <summary>P2-1：事件订阅器。订阅 task.completed / task.failed 让 IsRunning 复位。</summary>
    private readonly ISubscriber _subscriber;

    /// <summary>诊断日志。</summary>
    private readonly ILogger<WorkflowDetailView> _logger;

    /// <summary>P2-1：旋转动画（运行中显示）。复刻自 MainWindow.Messaging.cs。</summary>
    private Animation? _spinnerAnimation;

    /// <summary>P2-1：走马灯动画 Timer（16ms / 帧）。</summary>
    private DispatcherTimer? _inputMarqueeTimer;

    /// <summary>P2-1：走马灯动画 Stopwatch（计算进度）。</summary>
    private readonly Stopwatch _inputMarqueeStopwatch = new();

    // ──── Bug 8 修复：附件 + 拖放视觉反馈静态字段（完全复刻 Chat MainWindow.Attachments.cs） ────

    /// <summary>InputBorder 默认边框颜色（#3c3c3c 灰）。</summary>
    private static readonly IBrush BorderNormal = SolidColorBrush.Parse("#3c3c3c");

    /// <summary>InputBorder 拖放/聚焦激活色（#007ACC 蓝）。</summary>
    private static readonly IBrush BorderActive = SolidColorBrush.Parse("#007ACC");

    /// <summary>InputBorder 默认背景（#252526）。</summary>
    private static readonly IBrush BgNormal = SolidColorBrush.Parse("#252526");

    /// <summary>InputBorder 拖入时的半透明蓝背景。</summary>
    private static readonly IBrush BgDragOver = SolidColorBrush.Parse("#1a007ACC");

    /// <summary>当前是否处于拖入状态（用于 DragLeave 时判断是否需要恢复）。</summary>
    private bool _isDragOver;

    /// <summary>
    /// Bug 3 修复 2026-05-17：# 文件引用映射表（fileName → fullPath，OrdinalIgnoreCase）。
    /// </summary>
    private readonly Dictionary<string, string> _fileReferences = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowDetailView()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<WorkspaceTabVm>();
        _inputVm = App.Services.GetRequiredService<WorkflowInputVm>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        _logger = App.Services.GetRequiredService<ILogger<WorkflowDetailView>>();

        DataContext = _vm;
        InputArea.DataContext = _inputVm;

        // TODO P4: 输入区已移至独立组件，待重新接入
        // 原 InputBox.AddHandler / InputBox.TextChanged 注册已移除

        // 监听 IsRunning 变化驱动走马灯 + 旋转动画启停（条款 5 / 12）
        _inputVm.PropertyChanged += OnInputVmPropertyChanged;

        // 订阅 task.completed / task.failed 事件 → 复位 _inputVm.IsRunning
        _subscriber.Subscribe<WorkflowTaskCompletedArgs>(Events.OnWorkflowTaskCompleted, (_, args) =>
        {
            if (args.TaskId == _inputVm.CurrentTaskId)
                Dispatcher.UIThread.Post(() => _inputVm.OnTaskFinished());

            // TODO P4: DynamicAgentRegistry 已删除，SaveAgentDialog 弹窗逻辑待 P4 重新实现
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<WorkflowTaskFailedArgs>(Events.OnWorkflowTaskFailed, (_, args) =>
        {
            if (args.TaskId == _inputVm.CurrentTaskId)
                Dispatcher.UIThread.Post(() => _inputVm.OnTaskFinished());
            return Task.FromResult(false);
        });

        // TODO P4: 输入区已移至独立组件，待重新接入
        // 原 Attachments.CollectionChanged → RenderAttachments 已移除

        // 控件加载完成后填充 Popup 列表
        AttachedToVisualTree += (_, _) =>
        {
            FillSubModeSelectorActiveState();
            FillMaxSubAgentsSelector();
            FillAgentSelectorList();
            FillProviderSelectorList();
            FillModelSelectorList();
            RefreshAllLabels();

            // TODO P4: 输入区已移至独立组件，待重新接入
            // 原 InputBorder 拖放事件注册已移除
        };
    }

    // ──── 详情区（P4 重构：老 Approval/DynamicAgentCreationApproval 已移除） ────

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

    // TODO P4: 老 HITL 审批按钮（Approve/Revise/Reject）和动态子智能体审批按钮已移除。
    // P4 的用户确认/暂停/恢复将在第 3 步 UI 重做时实现。

    // ──── P4 时间线预览 ────

    /// <summary>P4-4：当前活跃的 P4 实时详情 ViewModel（任务级生命周期）。</summary>
    private P4TaskDetailVm? _p4DetailVm;

    private void OnP4PreviewClick(object? sender, RoutedEventArgs e)
    {
        // 在 Row 0 的 Panel 中添加 P4 预览视图
        if (DetailRoot.Children[0] is Panel panel)
        {
            _p4DetailVm ??= new P4TaskDetailVm();
            var taskTitle = _vm.Detail?.TaskTitle ?? "P4 任务";
            _p4DetailVm.LoadTask(string.Empty, taskTitle);
            panel.Children.Add(new P4TimelinePreviewView(_p4DetailVm));
        }
    }

    /// <summary>P3-1：步骤追加后自动滚动到底部（由事件回调触发）。</summary>
    private void ScrollStepsToEnd()
    {
        if (StepsScrollViewer is not null)
            StepsScrollViewer.ScrollToEnd();
    }

    // ──── P2-1：聊天式输入框 - 核心动作 ────

    /// <summary>"▶ 发送" 按钮：调 _inputVm.StartAsync 启动任务。</summary>
    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        _logger.LogInformation(
            "[WorkflowDetailView] OnSendClick: WorkspaceId={WorkspaceId}, SubMode={SubMode}, MaxSubAgents={MaxSubAgents}, Manager={Manager}, Provider={Provider}, Model={Model}, Input.Len={InputLen}",
            _vm.List.WorkspaceId, _inputVm.SubMode, _inputVm.MaxSubAgents,
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
                ShowError(hint);
                return;
            }

            await _vm.ShowTaskAsync(taskId);
            _logger.LogInformation("[WorkflowDetailView] Workflow task started and selected: taskId={TaskId}", taskId);
        }
        catch (Exception ex) { ShowError($"启动任务失败：{ex.Message}", ex); }
    }

    /// <summary>"⏸ 停止" 按钮：调 _inputVm.StopAsync。</summary>
    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        try { await _inputVm.StopAsync(CancellationToken.None); }
        catch (Exception ex) { ShowError($"停止任务失败：{ex.Message}", ex); }
    }

    /// <summary>Enter 发送 / Shift+Enter 换行（与 Chat 输入框行为一致）。</summary>
    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_inputVm.IsIdle && _inputVm.CanSubmit)
            OnSendClick(sender, new RoutedEventArgs());
    }

    /// <summary>"附件" 按钮（条款 4）：弹文件选择器。</summary>
    private async void OnAttachClick(object? sender, RoutedEventArgs e)
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
        catch (Exception ex) { ShowError($"选择附件失败：{ex.Message}", ex); }
    }

    /// <summary>附件 ✕ 按钮 → 按 index 移除（Bug 8 修复：完全复刻 Chat 模式）。</summary>
    private void OnRemoveAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index
            && index >= 0 && index < _inputVm.Attachments.Count)
        {
            _inputVm.Attachments.RemoveAt(index);
        }
    }

    /// <summary>P3-2：外部（WorkspaceExplorer 右键菜单）将文件/文件夹路径列表推送到工作流附件。</summary>
    public void AddExternalAttachments(IReadOnlyList<string> paths)
    {
        var added = false;
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
                added = true;
            }
            else if (System.IO.File.Exists(path))
            {
                _inputVm.Attachments.Add(new AttachmentInfo(path, System.IO.Path.GetFileName(path), FileContentTypeResolver.GetMimeType(path)));
                added = true;
            }
        }
        _ = added; // suppress unused warning
    }

    /// <summary>"工具" 按钮（条款 4）：弹 Popup。</summary>
    private void OnToolPopupOpenClick(object? sender, RoutedEventArgs e)
    {
        ToolPopup.IsOpen = !ToolPopup.IsOpen;
    }

    // ──── P2-1：6 个 Popup 选择器（条款 7/8/9/10） ────

    /// <summary>子模式按钮点击（条款 9）。</summary>
    private void OnSubModeSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillSubModeSelectorActiveState();
        SubModePopup.IsOpen = !SubModePopup.IsOpen;
    }

    /// <summary>子模式 Popup 项点击 → 切换 _inputVm.SubMode。</summary>
    private void OnSubModeItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string subMode)
        {
            _inputVm.SubMode = subMode;
            SubModePopup.IsOpen = false;
            FillSubModeSelectorActiveState();
        }
    }

    /// <summary>刷新子模式 Popup 项的高亮状态（active class）。</summary>
    private void FillSubModeSelectorActiveState()
    {
        var current = _inputVm.SubMode;
        ApplyActiveClass(SubModeMagenticItem, current == "magentic");
        ApplyActiveClass(SubModeParallelItem, current == "parallelanalysis");
    }

    /// <summary>子 Agent 数量按钮点击（条款 10）。</summary>
    private void OnMaxSubAgentsSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillMaxSubAgentsSelector();
        MaxSubAgentsPopup.IsOpen = !MaxSubAgentsPopup.IsOpen;
    }

    /// <summary>填充子 Agent 数量 Popup（1-20）。</summary>
    private void FillMaxSubAgentsSelector()
    {
        MaxSubAgentsList.Items.Clear();
        var current = _inputVm.MaxSubAgents;
        for (var i = 1; i <= 20; i++)
        {
            var n = i;
            var btn = new Button
            {
                Classes = { n == current ? "selector-item-active" : "selector-item" },
                Tag = n,
                Content = new TextBlock
                {
                    Text = $"最多 {n} 个",
                    FontSize = 12,
                    Foreground = n == current
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnMaxSubAgentsItemClick;
            MaxSubAgentsList.Items.Add(btn);
        }
    }

    private void OnMaxSubAgentsItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int n)
        {
            _inputVm.MaxSubAgents = n;
            MaxSubAgentsPopup.IsOpen = false;
            FillMaxSubAgentsSelector();
        }
    }

    /// <summary>智能体按钮点击（条款 7/8）。</summary>
    private void OnAgentSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillAgentSelectorList();
        AgentSelectorPopup.IsOpen = !AgentSelectorPopup.IsOpen;
    }

    /// <summary>填充智能体 Popup 列表。</summary>
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

    /// <summary>厂商按钮点击（条款 7）。</summary>
    private void OnProviderSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillProviderSelectorList();
        ProviderPopup.IsOpen = !ProviderPopup.IsOpen;
    }

    /// <summary>填充厂商 Popup 列表。</summary>
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

    /// <summary>模型按钮点击（条款 7）。</summary>
    private void OnModelSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillModelSelectorList();
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    /// <summary>填充模型 Popup 列表。</summary>
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

    // ──── P2-1：动画驱动（条款 5 / 12） + Label 手动同步 ────

    /// <summary>
    /// 监听 _inputVm.IsRunning + 所有 Label 相关属性变化 → 启动/停止动画 + 同步 Label.Text。
    /// </summary>
    private void OnInputVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        Dispatcher.UIThread.Post(() =>
        {
            switch (name)
            {
                case nameof(WorkflowInputVm.IsRunning):
                    // TODO P4: 输入区已移至独立组件，待重新接入
                    // 原 SendButton/StopButton 可见性切换 + 动画启停已移除
                    break;

                case nameof(WorkflowInputVm.SubModeDisplayName):
                case nameof(WorkflowInputVm.MaxSubAgentsDisplay):
                case nameof(WorkflowInputVm.SelectedManagerName):
                case nameof(WorkflowInputVm.SelectedProviderName):
                case nameof(WorkflowInputVm.SelectedModelName):
                case nameof(WorkflowInputVm.SubMode):
                case nameof(WorkflowInputVm.IsMagentic):
                    RefreshAllLabels();
                    break;
            }
        });
    }

    /// <summary>
    /// 手动同步所有 Label.Text + 子 Agent 数量按钮可见性（根因修复 2026-05-17）。
    /// </summary>
    private void RefreshAllLabels()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        // 原 SubModeLabel / MaxSubAgentsLabel / MaxSubAgentsBtn 同步已移除

        // 下方一行：智能体 / 厂商 / 模型（这些 Label 仍在 AXAML 中）
        ToolbarAgentLabel.Text = _inputVm.SelectedManagerName;
        ToolbarProviderLabel.Text = _inputVm.SelectedProviderName;
        ToolbarModelLabel.Text = _inputVm.SelectedModelName;
    }

    private void StartSpinnerAndMarquee()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        // 原 SpinnerIcon 旋转动画 + InputMarqueeBorder 走马灯已移除
    }

    private void StopSpinnerAndMarquee()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        _inputMarqueeTimer?.Stop();
        _inputMarqueeStopwatch.Reset();
    }

    private void UpdateInputMarquee()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        // 原 InputBorderHost / InputMarqueeTop/Right/Bottom/Left 走马灯计算已移除
    }

    private static double EaseInOutCubic(double value)
        => value < 0.5
            ? 4 * value * value * value
            : 1 - Math.Pow(-2 * value + 2, 3) / 2;

    // ──── 工具方法 ────

    /// <summary>切换 Button 的 selector-item-active / selector-item Classes 状态。</summary>
    private static void ApplyActiveClass(Button btn, bool isActive)
    {
        btn.Classes.Clear();
        btn.Classes.Add(isActive ? "selector-item-active" : "selector-item");
    }

    private void ShowError(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            _logger.LogError("[WorkflowDetailView] {Message}", message);
            Console.Error.WriteLine($"[WorkflowDetailView] {message}");
        }
        else
        {
            _logger.LogError(ex, "[WorkflowDetailView] {Message}", message);
            Console.Error.WriteLine($"[WorkflowDetailView] {message}\n{ex}");
        }
    }

    // ──── Bug 8 修复：附件渲染（完全复刻 Chat MainWindow.Attachments.cs line 76-125） ────

    /// <summary>
    /// 渲染附件预览列表。
    /// TODO P4: 输入区已移至独立组件，AttachmentList 不再存在，待重新接入。
    /// </summary>
    private void RenderAttachments()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    // ──── Bug 8 修复：拖放文件支持（完全复刻 Chat MainWindow.Attachments.cs line 168-224） ────

    /// <summary>拖入时给 InputBorder 加蓝色边框 + 半透明蓝背景反馈。</summary>
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>拖出时恢复 InputBorder 默认外观。</summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>拖放悬停 → 显示复制光标。</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>拖放完成 → 把文件添加到 _inputVm.Attachments。</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>P3-2：人类可读的字节数格式化。</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / 1024.0 / 1024.0:F1}MB";
    }

    /// <summary>恢复 InputBorder 默认外观。</summary>
    private void RestoreInputBorder()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    // ──── Bug 3 修复 2026-05-17：# 文件补全 ────

    /// <summary>
    /// 输入框文本变化时检测 # 触发文件补全。
    /// TODO P4: 输入区已移至独立组件，待重新接入。
    /// </summary>
    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>填充文件补全列表。</summary>
    private void FillFileList(List<(string FullPath, string RelativePath)> files, int hashIndex)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>
    /// 选中文件后：(a) 文本替换 #关键字 → #fileName，(b) 同时把文件添加到 Attachments。
    /// </summary>
    private void OnFileItemSelected(string fileName, string fullPath, int hashIndex)
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
    }

    /// <summary>
    /// NativeAOT 发布模式下 Avalonia 绑定可能晚于点击事件写回，发送前显式同步输入框文本。
    /// </summary>
    private void SyncInputTextToViewModel()
    {
        // TODO P4: 输入区已移至独立组件，待重新接入
        // 原 InputBox.Text 同步已移除
    }
}
