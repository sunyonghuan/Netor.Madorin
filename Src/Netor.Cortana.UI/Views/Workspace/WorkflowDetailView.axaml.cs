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

using Netor.Cortana.AI.TaskEngine;
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
/// 输入框区职责：
/// - 6 个 Popup 动态渲染（子模式 / 子 Agent 数量 / 智能体 / 厂商 / 模型 / 工具屏蔽）
/// - 走马灯霓虹边动画（条款 12，工作流橙色 #ff9c40）
/// - 发送按钮旋转动画（条款 5，复刻 Chat 模式）
/// - 订阅 task.completed / task.failed 事件复位 IsRunning
/// - 监听 _inputVm.IsRunning 变化驱动动画启停
///
/// DataContext：
/// - 主体 DataContext = WorkspaceTabVm（构造函数注入）
/// - InputArea.DataContext = WorkflowInputVm（构造函数注入）
///
/// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md。
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
    /// 完全复刻 Chat 模式 MainWindow.Input.cs line 23。
    /// 使用：用户输入 # 触发 FilePopup → 选中文件 → InputBox 文本替换为 #fileName，同时 _fileReferences[fileName] = fullPath。
    /// 发送时由 .cs 把 #fileName 转换为 attachment（已直接 push 到 _inputVm.Attachments，无需运行时再解析）。
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

        // Bug 2 修复 2026-05-17：用 Tunnel 路由注册 KeyDown（完全复刻 Chat 模式 MainWindow.axaml.cs line 116）。
        // axaml KeyDown="" 是 Bubble 路由，TextBox 内部先吞掉 Enter 插入换行；Tunnel 让事件先到顶层 handler。
        InputBox.AddHandler(KeyDownEvent, OnInputBoxKeyDown, RoutingStrategies.Tunnel);

        // Bug 3 修复 2026-05-17：订阅 TextChanged 实现 # 文件补全（完全复刻 Chat 模式 MainWindow.axaml.cs line 103）。
        InputBox.TextChanged += OnInputTextChanged;

        // 监听 IsRunning 变化驱动走马灯 + 旋转动画启停（条款 5 / 12）
        _inputVm.PropertyChanged += OnInputVmPropertyChanged;

        // P4: 订阅 task.engine.completed / task.engine.failed 事件 → 复位 _inputVm.IsRunning
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineCompleted, (_, args) =>
        {
            if (args.TaskId == _inputVm.CurrentTaskId)
                Dispatcher.UIThread.Post(() => _inputVm.OnTaskFinished());
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineFailed, (_, args) =>
        {
            if (args.TaskId == _inputVm.CurrentTaskId)
                Dispatcher.UIThread.Post(() => _inputVm.OnTaskFinished());
            return Task.FromResult(false);
        });

        // 监听 Attachments 集合变化 → 触发 RenderAttachments（Bug 8 修复：完全复刻 Chat 模式）
        _inputVm.Attachments.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(RenderAttachments);
        };

        // 控件加载完成后填充 Popup 列表（避免构造期 ItemsControl 还没准备好）+ 注册拖放事件 + 初始渲染附件
        AttachedToVisualTree += (_, _) =>
        {
            FillSubModeSelectorActiveState();
            FillMaxSubAgentsSelector();
            FillAgentSelectorList();
            FillProviderSelectorList();
            FillModelSelectorList();
            RenderAttachments();
            RefreshAllLabels(); // 根因修复 2026-05-17：初始同步所有 Label.Text（显示默认值）

            // Bug 8：注册输入框拖放事件（完全复刻 Chat MainWindow.axaml.cs line 106-109）
            InputBorder.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            InputBorder.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            InputBorder.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            InputBorder.AddHandler(DragDrop.DropEvent, OnDrop);
        };
    }

    // ──── 详情区（C4 / 5B 保留逻辑） ────

    private async void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        // P4: 通过 TaskExecutionEngine 取消任务
        if (_inputVm.CurrentTaskId is null)
        {
            _logger.LogWarning("取消任务失败：CurrentTaskId 为空");
            return;
        }
        try
        {
            var engine = App.Services.GetRequiredService<AI.TaskEngine.TaskExecutionEngine>();
            var success = await engine.CancelTaskAsync(_inputVm.CurrentTaskId, CancellationToken.None);
            if (success)
            {
                _logger.LogInformation("任务取消请求已发送: {TaskId}", _inputVm.CurrentTaskId);
                _inputVm.OnTaskFinished();
            }
            else
            {
                _logger.LogWarning("取消任务失败：任务不存在或已结束: {TaskId}", _inputVm.CurrentTaskId);
            }
        }
        catch (Exception ex) { ShowError($"取消任务失败：{ex.Message}", ex); }
    }

    // P4: HITL 审批按钮 — 接通 TaskExecutionEngine API
    private async void OnApprovalApproveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            var taskId = _vm.Detail.TaskId;
            if (string.IsNullOrEmpty(taskId)) return;

            // 禁用交互防止重复点击
            if (_vm.Detail.Approval is { } approval)
                approval.IsInteractive = false;

            var ok = await engine.ConfirmPlanAsync(taskId, CancellationToken.None);
            if (!ok)
                _logger.LogWarning("[WorkflowDetailView] ConfirmPlanAsync 返回 false: {TaskId}", taskId);
        }
        catch (Exception ex) { ShowError($"确认计划失败：{ex.Message}", ex); }
    }

    private async void OnApprovalReviseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            var taskId = _vm.Detail.TaskId;
            var approval = _vm.Detail.Approval;
            if (string.IsNullOrEmpty(taskId) || approval is null) return;

            var revision = approval.RevisionInput?.Trim();
            if (string.IsNullOrEmpty(revision))
            {
                ShowError("请输入修改建议后再提交");
                return;
            }

            approval.IsInteractive = false;
            var ok = await engine.RequestPlanModificationAsync(taskId, revision, CancellationToken.None);
            if (ok)
            {
                approval.ProgressSummary = $"已提交修改意见，等待重新生成计划…";
                approval.RevisionInput = string.Empty;
            }
            else
            {
                approval.IsInteractive = true;
                _logger.LogWarning("[WorkflowDetailView] RequestPlanModificationAsync 返回 false: {TaskId}", taskId);
            }
        }
        catch (Exception ex) { ShowError($"提交修改失败：{ex.Message}", ex); }
    }

    private async void OnApprovalRejectClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var engine = App.Services.GetRequiredService<TaskExecutionEngine>();
            var taskId = _vm.Detail.TaskId;
            if (string.IsNullOrEmpty(taskId)) return;

            if (_vm.Detail.Approval is { } approval)
                approval.IsInteractive = false;

            await engine.CancelTaskAsync(taskId, CancellationToken.None);
        }
        catch (Exception ex) { ShowError($"取消任务失败：{ex.Message}", ex); }
    }

    private void OnDynamicAgentApproveClick(object? sender, RoutedEventArgs e)
        => _logger.LogInformation("[WorkflowDetailView] 动态智能体批准（P4 迁移中，功能待接入）");

    private void OnDynamicAgentApproveAllClick(object? sender, RoutedEventArgs e)
        => _logger.LogInformation("[WorkflowDetailView] 动态智能体全部批准（P4 迁移中，功能待接入）");

    private void OnDynamicAgentRejectClick(object? sender, RoutedEventArgs e)
        => _logger.LogInformation("[WorkflowDetailView] 动态智能体拒绝（P4 迁移中，功能待接入）");

    // ──── P4 时间线预览 ────

    /// <summary>P4-4：当前活跃的 P4 实时详情 ViewModel（任务级生命周期）。</summary>
    private P4TaskDetailVm? _p4DetailVm;

    private void OnP4PreviewClick(object? sender, RoutedEventArgs e)
    {
        // 在 Row 0 的 Panel 中添加 P4 预览视图
        if (DetailRoot.Children[0] is Panel panel)
        {
            // 如果当前有选中的任务，使用实时 ViewModel；否则使用 mock 预览
            var taskId = _vm.Detail.TaskId;
            if (!string.IsNullOrEmpty(taskId))
            {
                _p4DetailVm ??= new P4TaskDetailVm();
                _p4DetailVm.LoadTask(taskId, _vm.Detail.Title ?? "P4 任务");
                panel.Children.Add(new P4TimelinePreviewView(_p4DetailVm));
            }
            else
            {
                panel.Children.Add(new P4TimelinePreviewView());
            }
        }
    }

    // ──── P3-1：时间线折叠 toggle + 自动滚动 ────
    // P4 迁移：WorkflowTimelineStepVm 已删除，使用 dynamic 做安全 toggle

    private void OnToggleThinking(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag })
        {
            try
            {
                dynamic vm = tag;
                vm.IsThinkingExpanded = !vm.IsThinkingExpanded;
            }
            catch { /* P4: VM 类型不匹配时静默忽略 */ }
        }
    }

    private void OnToggleToolCalls(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag })
        {
            try
            {
                dynamic vm = tag;
                vm.IsToolCallsExpanded = !vm.IsToolCallsExpanded;
            }
            catch { /* P4: VM 类型不匹配时静默忽略 */ }
        }
    }

    /// <summary>P3-1：步骤追加后自动滚动到底部（由事件回调触发）。</summary>
    private void ScrollStepsToEnd()
    {
        if (StepsScrollViewer is not null)
            StepsScrollViewer.ScrollToEnd();
    }

    private void OnAttachToConversationClick(object? sender, RoutedEventArgs e)
    {
        // P4: WorkflowToChatBackflowService 已移除，功能待后续重新实现
        _logger.LogInformation("[WorkflowDetailView] 附加到对话功能暂不可用（P4 迁移中）");
    }

    // ──── P2-1：聊天式输入框 - 核心动作 ────

    /// <summary>"▶ 发送" 按钮：调 _inputVm.StartAsync 启动任务。</summary>
    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        SyncInputTextToViewModel();

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
                // Bug 1 修复 2026-05-17：StartAsync 返回 null 表示 CanSubmit=false 校验失败，
                // 之前只把 ValidationError 写到小字红色 TextBlock 容易被忽略，导致用户感觉"点击发送无反应"。
                // 现在统一用 ShowError 弹窗给出醒目反馈。
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
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        SyncInputTextToViewModel();
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
                // Bug B 修复 2026-05-17：按 path 去重，避免同一文件重复添加
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
            // CollectionChanged 触发器自动调用 RenderAttachments
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
        // CollectionChanged 自动触发 RenderAttachments（无需显式调用）
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

            // SelectedManager setter 联动了 Provider/Model，刷新下面两个 Popup 显示状态
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

            // SelectedProvider setter 联动了 Model 列表，刷新两个 Popup 显示
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
    /// Bug 2 修复 2026-05-17：显式控制 SendButton/StopButton 互斥。
    /// 根因修复 2026-05-17：去掉 axaml DataContext binding 后，在 .cs 中手动同步 Label.Text
    /// （完全复刻 Chat 模式 RefreshProviderDisplay 风格，更稳定）。
    /// </summary>
    private void OnInputVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName;
        Dispatcher.UIThread.Post(() =>
        {
            switch (name)
            {
                case nameof(WorkflowInputVm.IsRunning):
                    // Bug 2 修复：显式控制按钮可见性（互斥）
                    SendButton.IsVisible = !_inputVm.IsRunning;
                    StopButton.IsVisible = _inputVm.IsRunning;
                    if (_inputVm.IsRunning)
                        StartSpinnerAndMarquee();
                    else
                        StopSpinnerAndMarquee();
                    break;

                // Label 同步：任何相关属性变化都触发 RefreshAllLabels
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
    /// 完全复刻 Chat 模式不依赖 binding 的可靠模式（RefreshProviderDisplay 风格）。
    /// </summary>
    private void RefreshAllLabels()
    {
        // 上方一行：子模式 + 子 Agent 数量
        SubModeLabel.Text = _inputVm.SubModeDisplayName;
        MaxSubAgentsLabel.Text = _inputVm.MaxSubAgentsDisplay;
        MaxSubAgentsBtn.IsVisible = _inputVm.IsMagentic;

        // 下方一行：智能体 / 厂商 / 模型
        ToolbarAgentLabel.Text = _inputVm.SelectedManagerName;
        ToolbarProviderLabel.Text = _inputVm.SelectedProviderName;
        ToolbarModelLabel.Text = _inputVm.SelectedModelName;
    }

    private void StartSpinnerAndMarquee()
    {
        _spinnerAnimation ??= new Animation
        {
            Duration = TimeSpan.FromSeconds(1),
            IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360.0) } },
            }
        };
        _spinnerAnimation.RunAsync(SpinnerIcon);

        InputMarqueeBorder.IsVisible = true;
        _inputMarqueeStopwatch.Restart();
        _inputMarqueeTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => UpdateInputMarquee());
        _inputMarqueeTimer.Start();
        UpdateInputMarquee();
    }

    private void StopSpinnerAndMarquee()
    {
        _inputMarqueeTimer?.Stop();
        _inputMarqueeStopwatch.Reset();
        InputMarqueeBorder.IsVisible = false;
    }

    private void UpdateInputMarquee()
    {
        var width = Math.Max(InputBorderHost.Bounds.Width, 1);
        var height = Math.Max(InputBorderHost.Bounds.Height, 1);
        var linearProgress = _inputMarqueeStopwatch.Elapsed.TotalSeconds % 1.9 / 1.9;
        var progress = EaseInOutCubic(linearProgress);

        Canvas.SetLeft(InputMarqueeTop, -120 + (width + 120) * progress);
        Canvas.SetTop(InputMarqueeTop, 0);

        Canvas.SetLeft(InputMarqueeRight, width - 2);
        Canvas.SetTop(InputMarqueeRight, -120 + (height + 120) * progress);

        Canvas.SetLeft(InputMarqueeBottom, width - 120 - (width + 120) * progress);
        Canvas.SetTop(InputMarqueeBottom, height - 2);

        Canvas.SetLeft(InputMarqueeLeft, 0);
        Canvas.SetTop(InputMarqueeLeft, height - 120 - (height + 120) * progress);
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
    /// 由 _inputVm.Attachments.CollectionChanged 自动触发。
    /// </summary>
    private void RenderAttachments()
    {
        AttachmentList.Items.Clear();

        for (var i = 0; i < _inputVm.Attachments.Count; i++)
        {
            var attachment = _inputVm.Attachments[i];
            var index = i;

            var removeBtn = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                FontSize = 11,
                Padding = new Thickness(2, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Tag = index,
            };
            removeBtn.Click += OnRemoveAttachmentClick;

            var tag = new Border
            {
                Classes = { "attachment-tag" },
                Child = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = attachment.IsFolder
                                ? $"📁 {attachment.Name} ({attachment.FileCount} 文件, {FormatBytes(attachment.TotalBytes)})"
                                : $"📎 {attachment.Name}",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            MaxWidth = 240,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        removeBtn,
                    },
                },
            };

            AttachmentList.Items.Add(tag);
        }
    }

    // ──── Bug 8 修复：拖放文件支持（完全复刻 Chat MainWindow.Attachments.cs line 168-224） ────

    /// <summary>拖入时给 InputBorder 加蓝色边框 + 半透明蓝背景反馈。</summary>
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        _isDragOver = true;
        InputBorder.BorderBrush = BorderActive;
        InputBorder.BorderThickness = new Thickness(2);
        InputBorder.Background = BgDragOver;
        e.Handled = true;
    }

    /// <summary>拖出时恢复 InputBorder 默认外观。</summary>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();
        e.Handled = true;
    }

    /// <summary>拖放悬停 → 显示复制光标。</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>拖放完成 → 把文件添加到 _inputVm.Attachments。</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (string.IsNullOrEmpty(path)) continue;
            // Bug B 修复 2026-05-17：按 path 去重，避免同一文件重复添加
            if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            // P3-2：支持文件夹拖放
            if (System.IO.Directory.Exists(path))
            {
                var scanner = new Entitys.Services.FolderAttachmentScanner();
                var result = scanner.Scan(path);
                var folderName = System.IO.Path.GetFileName(path.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                _inputVm.Attachments.Add(new AttachmentInfo(path, folderName, "inode/directory",
                    IsFolder: true, FileCount: result.FileCount, TotalBytes: result.TotalBytes));
            }
            else if (System.IO.File.Exists(path))
            {
                var name = System.IO.Path.GetFileName(path);
                var mimeType = FileContentTypeResolver.GetMimeType(path);
                _inputVm.Attachments.Add(new AttachmentInfo(path, name, mimeType));
            }
            // CollectionChanged 自动触发 RenderAttachments
        }
        e.Handled = true;
    }

    /// <summary>P3-2：人类可读的字节数格式化。</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / 1024.0 / 1024.0:F1}MB";
    }

    /// <summary>恢复 InputBorder 默认外观（边框 1px 灰色 + 默认背景）。</summary>
    private void RestoreInputBorder()
    {
        InputBorder.BorderThickness = new Thickness(1);
        InputBorder.Background = BgNormal;
        InputBorder.BorderBrush = InputBox.IsFocused ? BorderActive : BorderNormal;
    }

    // ──── Bug 3 修复 2026-05-17：# 文件补全（完全复刻 Chat 模式 MainWindow.Input.cs line 128-254）────

    /// <summary>
    /// 输入框文本变化时检测 # 触发文件补全。完全复刻 Chat MainWindow.Input.cs OnInputTextChanged。
    /// 与 Chat 不同的是：Workflow 不支持 @ 智能体补全（决策 P2-1：仅靠输入框上方的智能体选择器）。
    /// </summary>
    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        SyncInputTextToViewModel();

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 尝试 # 文件补全
        var hashIndex = text.LastIndexOf('#', Math.Max(0, caret - 1));
        if (hashIndex < 0)
        {
            FilePopup.IsOpen = false;
            return;
        }

        // # 后面到光标之间的内容作为搜索关键字
        var afterHash = text.Substring(hashIndex + 1, caret - hashIndex - 1);

        // 如果包含空格 / 换行，关闭补全
        if (afterHash.Contains(' ') || afterHash.Contains('\n'))
        {
            FilePopup.IsOpen = false;
            return;
        }

        // 获取工作目录下的文件列表（过滤匹配）
        var appPaths = App.Services.GetRequiredService<IAppPaths>();
        var workDir = appPaths.WorkspaceDirectory;

        if (!System.IO.Directory.Exists(workDir))
        {
            FilePopup.IsOpen = false;
            return;
        }

        try
        {
            var files = System.IO.Directory
                .EnumerateFiles(workDir, "*", System.IO.SearchOption.AllDirectories)
                .Select(f => (FullPath: f, RelativePath: System.IO.Path.GetRelativePath(workDir, f)))
                .Where(f => !f.RelativePath.Contains($"{System.IO.Path.DirectorySeparatorChar}.", StringComparison.Ordinal)
                         && !f.RelativePath.StartsWith('.'))
                .Where(f => string.IsNullOrEmpty(afterHash)
                         || f.RelativePath.Contains(afterHash, StringComparison.OrdinalIgnoreCase))
                .Take(30)
                .ToList();

            if (files.Count == 0)
            {
                FilePopup.IsOpen = false;
                return;
            }

            FillFileList(files, hashIndex);
            FilePopup.IsOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WorkflowDetailView] 枚举工作目录文件失败：{WorkDir}", workDir);
            FilePopup.IsOpen = false;
        }
    }

    /// <summary>填充文件补全列表（完全复刻 Chat MainWindow.Input.cs FillFileList）。</summary>
    private void FillFileList(List<(string FullPath, string RelativePath)> files, int hashIndex)
    {
        FileList.Items.Clear();

        foreach (var (fullPath, relativePath) in files)
        {
            var fileName = System.IO.Path.GetFileName(fullPath);
            var dirPart = System.IO.Path.GetDirectoryName(relativePath) ?? string.Empty;

            var sp = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 1 };
            sp.Children.Add(new Avalonia.Controls.TextBlock
            {
                Text = fileName,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            });
            if (!string.IsNullOrEmpty(dirPart))
            {
                sp.Children.Add(new Avalonia.Controls.TextBlock
                {
                    Text = dirPart,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")),
                });
            }

            var btn = new Avalonia.Controls.Button
            {
                Classes = { "selector-item" },
                Content = sp,
                Tag = fullPath,
            };
            btn.Click += (_, _) => OnFileItemSelected(fileName, fullPath, hashIndex);
            FileList.Items.Add(btn);
        }
    }

    /// <summary>
    /// 选中文件后：(a) 文本替换 #关键字 → #fileName，(b) 同时把文件添加到 Attachments（按 path 去重）。
    /// 与 Chat 不同：Workflow 模式有显式 Attachments 概念，所以选中即触发附件添加（用户能看到 chip 反馈）。
    /// </summary>
    private void OnFileItemSelected(string fileName, string fullPath, int hashIndex)
    {
        FilePopup.IsOpen = false;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 1) 文本替换 # 到光标之间的内容为 #文件名
        var replacement = $"#{fileName} ";
        var newText = string.Concat(text.AsSpan(0, hashIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = hashIndex + replacement.Length;

        // 2) 记录 fileName → fullPath 映射（备发送时解析需要）
        _fileReferences[fileName] = fullPath;

        // 3) 同时把文件添加到 Attachments（按 path OrdinalIgnoreCase 去重，与 OnAttachClick / OnDrop 一致）
        if (System.IO.File.Exists(fullPath) &&
            !_inputVm.Attachments.Any(a => string.Equals(a.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            var mimeType = FileContentTypeResolver.GetMimeType(fullPath);
            _inputVm.Attachments.Add(new AttachmentInfo(fullPath, fileName, mimeType));
        }
    }

    /// <summary>
    /// NativeAOT 发布模式下 Avalonia 绑定可能晚于点击事件写回，发送前显式同步输入框文本。
    /// </summary>
    private void SyncInputTextToViewModel()
    {
        var text = InputBox.Text ?? string.Empty;
        if (!string.Equals(_inputVm.InitialInput, text, StringComparison.Ordinal))
            _inputVm.InitialInput = text;
    }
}
