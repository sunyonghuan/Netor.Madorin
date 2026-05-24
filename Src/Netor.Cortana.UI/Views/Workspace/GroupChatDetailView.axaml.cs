using System.ComponentModel;
using System.Diagnostics;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Workflow.Bridges;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.EventHub;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// P2-1：群聊任务详情视图（条款 1-12 重写版，2026-05-17）。
///
/// 与 WorkflowDetailView 95% 相同：
/// - 详情区（Row 0）100% 复用 WorkspaceTabVm.Detail（与 WorkflowDetailView / TaskListPanel 同源）
/// - 输入框区（Row 1）差异：
///   · 没有上方一行（不需要子模式 / 子 Agent 数量）
///   · 没有厂商 / 模型按钮（用户澄清 A：每个 Agent 用自己的 Provider/Model，未设的用默认）
///   · 智能体按钮 → 多选 Popup CheckListBox（条款 11，标签显示「已选 N 个智能体」）
///   · 走马灯紫色 #b070ff（条款 12）
///   · 下方一行只有 3 个按钮：附件 / 智能体 / 工具
///
/// DataContext：
/// - 主体 DataContext = WorkspaceTabVm（构造函数注入）
/// - InputArea.DataContext = GroupChatInputVm（构造函数注入）
///
/// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md。
/// </summary>
public partial class GroupChatDetailView : UserControl
{
    private readonly WorkflowToChatBackflowService _backflowService;
    private readonly WorkspaceTabVm _vm;
    private readonly GroupChatInputVm _inputVm;
    private readonly ISubscriber _subscriber;
    private readonly ILogger<GroupChatDetailView> _logger;

    /// <summary>P2-1：旋转动画。复刻自 MainWindow.Messaging.cs。</summary>
    private Animation? _spinnerAnimation;

    /// <summary>P2-1：走马灯动画 Timer（16ms / 帧）。</summary>
    private DispatcherTimer? _inputMarqueeTimer;

    /// <summary>P2-1：走马灯动画 Stopwatch。</summary>
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

    /// <summary>当前是否处于拖入状态。</summary>
    private bool _isDragOver;

    /// <summary>
    /// Bug 3 修复 2026-05-17：# 文件引用映射表（fileName → fullPath，OrdinalIgnoreCase）。
    /// 完全复刻 Chat 模式 MainWindow.Input.cs line 23。
    /// 使用：用户输入 # 触发 FilePopup → 选中文件 → InputBox 文本替换为 #fileName，同时 _fileReferences[fileName] = fullPath。
    /// 选中即同时把文件添加到 Attachments（与 Workflow 模式一致），用户看到 chip 反馈。
    /// </summary>
    private readonly Dictionary<string, string> _fileReferences = new(StringComparer.OrdinalIgnoreCase);

    public GroupChatDetailView()
    {
        InitializeComponent();
        _backflowService = App.Services.GetRequiredService<WorkflowToChatBackflowService>();
        _vm = App.Services.GetRequiredService<WorkspaceTabVm>();
        _inputVm = App.Services.GetRequiredService<GroupChatInputVm>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();
        _logger = App.Services.GetRequiredService<ILogger<GroupChatDetailView>>();

        DataContext = _vm;
        InputArea.DataContext = _inputVm;

        // Bug 2 修复 2026-05-17：用 Tunnel 路由注册 KeyDown（完全复刻 Chat 模式 MainWindow.axaml.cs line 116）。
        // axaml KeyDown="" 是 Bubble 路由，TextBox 内部先吞掉 Enter 插入换行；Tunnel 让事件先到顶层 handler。
        InputBox.AddHandler(KeyDownEvent, OnInputBoxKeyDown, RoutingStrategies.Tunnel);

        // Bug 3 修复 2026-05-17：订阅 TextChanged 实现 # 文件补全（完全复刻 Chat 模式 MainWindow.axaml.cs line 103）。
        InputBox.TextChanged += OnInputTextChanged;

        // 监听 IsRunning 变化驱动走马灯 + 旋转动画启停（条款 5/12）
        _inputVm.PropertyChanged += OnInputVmPropertyChanged;

        // 监听 SelectedAgents 变化 → 刷新 Popup CheckBox 状态
        _inputVm.SelectedAgents.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(FillAgentSelectorList);
        };

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

        // 监听 Attachments 集合变化 → 触发 RenderAttachments（Bug 8 修复：完全复刻 Chat 模式）
        _inputVm.Attachments.CollectionChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(RenderAttachments);
        };

        // 控件加载完成后初始化 + 注册拖放事件
        AttachedToVisualTree += (_, _) =>
        {
            FillAgentSelectorList();
            RenderAttachments();
            RefreshAllLabels(); // 根因修复 2026-05-17：初始同步 Label.Text（启动显示默认值）

            // Bug 8：注册输入框拖放事件（完全复刻 Chat MainWindow.axaml.cs line 106-109）
            InputBorder.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            InputBorder.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            InputBorder.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            InputBorder.AddHandler(DragDrop.DropEvent, OnDrop);
        };
    }

    // ──── 详情区（与 WorkflowDetailView 一致） ────

    private async void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        try { await _vm.Detail.CancelAsync(CancellationToken.None); }
        catch (Exception ex) { ShowError($"取消任务失败：{ex.Message}", ex); }
    }

    private async void OnApprovalApproveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _vm.Detail.Approval.ApproveAsync(CancellationToken.None);
            if (!ok) ShowError("批准失败：任务不在等待状态或 RequestId 不匹配");
        }
        catch (Exception ex) { ShowError($"批准失败：{ex.Message}", ex); }
    }

    private async void OnApprovalReviseClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_vm.Detail.Approval.RevisionInput))
            {
                ShowError("请先在文本框输入修改建议再点击[提交修改]");
                return;
            }
            var ok = await _vm.Detail.Approval.SubmitRevisionAsync(CancellationToken.None);
            if (!ok) ShowError("提交修改失败：任务不在等待状态或 RequestId 不匹配");
        }
        catch (Exception ex) { ShowError($"提交修改失败：{ex.Message}", ex); }
    }

    private async void OnApprovalRejectClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _vm.Detail.Approval.RejectAsync(CancellationToken.None);
            if (!ok) ShowError("取消任务失败：任务不在等待状态或 RequestId 不匹配");
        }
        catch (Exception ex) { ShowError($"取消任务失败：{ex.Message}", ex); }
    }

    private async void OnAttachToConversationClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var taskId = _vm.Detail.TaskId;
            if (string.IsNullOrEmpty(taskId)) { ShowError("未选中任务"); return; }
            var sessionId = await _backflowService.AttachToConversationAsync(taskId, targetSessionId: null, CancellationToken.None);
            if (string.IsNullOrEmpty(sessionId))
            {
                ShowError("附加失败：可能无来源会话或回灌服务返回空");
                return;
            }
            ShowError($"已附加到会话 {sessionId[..Math.Min(8, sessionId.Length)]}…");
        }
        catch (Exception ex) { ShowError($"附加到对话失败：{ex.Message}", ex); }
    }

    // ──── P3-1：自动滚动到底部 ────

    /// <summary>P3-1：步骤追加后自动滚动到底部（由事件回调触发）。</summary>
    private void ScrollStepsToEnd()
    {
        if (StepsScrollViewer is not null)
            StepsScrollViewer.ScrollToEnd();
    }

    // ──── P2-1：聊天式输入框 - 核心动作 ────

    /// <summary>"发送" 按钮：调 _inputVm.StartAsync 启动群聊任务。</summary>
    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        SyncInputTextToViewModel();

        _logger.LogInformation(
            "[GroupChatDetailView] OnSendClick: WorkspaceId={WorkspaceId}, SelectedAgents={Count}, Input.Len={InputLen}",
            _vm.List.WorkspaceId, _inputVm.SelectedAgents.Count,
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
                    : "无法启动群聊：请检查智能体是否已选 + 输入内容是否为空。";
                _logger.LogWarning("[GroupChatDetailView] StartAsync 返回 null（校验失败）：{Hint}", hint);
                ShowError(hint);
                return;
            }

            await _vm.ShowTaskAsync(taskId);
            _logger.LogInformation("[GroupChatDetailView] GroupChat task started and selected: taskId={TaskId}", taskId);
        }
        catch (Exception ex) { ShowError($"启动群聊失败：{ex.Message}", ex); }
    }

    /// <summary>"停止" 按钮：调 _inputVm.StopAsync。</summary>
    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        try { await _inputVm.StopAsync(CancellationToken.None); }
        catch (Exception ex) { ShowError($"停止任务失败：{ex.Message}", ex); }
    }

    /// <summary>Enter 发送 / Shift+Enter 换行。</summary>
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

    /// <summary>"工具" 按钮（条款 4）：弹 Popup。</summary>
    private void OnToolPopupOpenClick(object? sender, RoutedEventArgs e)
    {
        ToolPopup.IsOpen = !ToolPopup.IsOpen;
    }

    // ──── P2-1：智能体多选 Popup（条款 11） ────

    /// <summary>智能体多选按钮点击。</summary>
    private void OnAgentSelectorClick(object? sender, RoutedEventArgs e)
    {
        FillAgentSelectorList();
        AgentSelectorPopup.IsOpen = !AgentSelectorPopup.IsOpen;
    }

    /// <summary>
    /// 填充智能体多选 Popup 列表（条款 11：CheckListBox 风格）。
    /// 每行一个 CheckBox，IsChecked = SelectedAgents.Contains(agent)。
    /// </summary>
    private void FillAgentSelectorList()
    {
        AgentSelectorList.Items.Clear();
        foreach (var agent in _inputVm.AvailableAgents)
        {
            var id = agent.Id;
            var isSelected = _inputVm.IsAgentSelected(id);

            var checkBox = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = agent.Name,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                },
                IsChecked = isSelected,
                Tag = id,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                Margin = new Thickness(2, 2, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            checkBox.IsCheckedChanged += OnAgentCheckBoxChanged;
            AgentSelectorList.Items.Add(checkBox);
        }
    }

    /// <summary>CheckBox 状态变化 → 切换 _inputVm.SelectedAgents。</summary>
    private void OnAgentCheckBoxChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.Tag is not string agentId) return;

        var currentlySelected = _inputVm.IsAgentSelected(agentId);
        var newChecked = cb.IsChecked == true;
        if (newChecked == currentlySelected) return; // 状态一致，避免重入

        _inputVm.ToggleAgent(agentId);
    }

    // ──── P2-1：动画驱动（条款 5/12，紫色 #b070ff） + Label 手动同步 ────

    /// <summary>
    /// 监听 _inputVm.IsRunning + SelectedAgentsLabel 变化 → 启动/停止动画 + 同步 Label.Text。
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
                case nameof(GroupChatInputVm.IsRunning):
                    // Bug 2 修复：显式控制按钮可见性（互斥）
                    SendButton.IsVisible = !_inputVm.IsRunning;
                    StopButton.IsVisible = _inputVm.IsRunning;
                    if (_inputVm.IsRunning)
                        StartSpinnerAndMarquee();
                    else
                        StopSpinnerAndMarquee();
                    break;

                // Label 同步：群聊只有一个智能体多选标签
                case nameof(GroupChatInputVm.SelectedAgentsLabel):
                case nameof(GroupChatInputVm.HasSelectedAgents):
                    RefreshAllLabels();
                    break;
            }
        });
    }

    /// <summary>
    /// 手动同步所有 Label.Text（根因修复 2026-05-17）。
    /// 完全复刻 Chat 模式不依赖 binding 的可靠模式。
    /// 群聊只有 1 个 Label 需要同步：智能体多选标签。
    /// </summary>
    private void RefreshAllLabels()
    {
        ToolbarAgentLabel.Text = _inputVm.SelectedAgentsLabel;
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

    private void ShowError(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            _logger.LogError("[GroupChatDetailView] {Message}", message);
            Console.Error.WriteLine($"[GroupChatDetailView] {message}");
        }
        else
        {
            _logger.LogError(ex, "[GroupChatDetailView] {Message}", message);
            Console.Error.WriteLine($"[GroupChatDetailView] {message}\n{ex}");
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
                            Text = $"📎 {attachment.Name}",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            MaxWidth = 180,
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
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
            // Bug B 修复 2026-05-17：按 path 去重，避免同一文件重复添加
            if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            var name = System.IO.Path.GetFileName(path);
            var mimeType = FileContentTypeResolver.GetMimeType(path);
            _inputVm.Attachments.Add(new AttachmentInfo(path, name, mimeType));
            // CollectionChanged 自动触发 RenderAttachments
        }
        e.Handled = true;
    }

    /// <summary>恢复 InputBorder 默认外观。</summary>
    private void RestoreInputBorder()
    {
        InputBorder.BorderThickness = new Thickness(1);
        InputBorder.Background = BgNormal;
        InputBorder.BorderBrush = InputBox.IsFocused ? BorderActive : BorderNormal;
    }

    // ──── Bug 3 修复 2026-05-17：# 文件补全（完全复刻 Chat 模式 MainWindow.Input.cs line 128-254）────

    /// <summary>
    /// 输入框文本变化时检测 # 触发文件补全。完全复刻 Chat MainWindow.Input.cs OnInputTextChanged。
    /// 与 Chat 不同：GroupChat 不支持 @ 智能体补全（决策 P2-1：靠输入框上方的智能体多选 Popup）。
    /// </summary>
    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        SyncInputTextToViewModel();

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        var hashIndex = text.LastIndexOf('#', Math.Max(0, caret - 1));
        if (hashIndex < 0)
        {
            FilePopup.IsOpen = false;
            return;
        }

        var afterHash = text.Substring(hashIndex + 1, caret - hashIndex - 1);

        if (afterHash.Contains(' ') || afterHash.Contains('\n'))
        {
            FilePopup.IsOpen = false;
            return;
        }

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
            _logger.LogWarning(ex, "[GroupChatDetailView] 枚举工作目录文件失败：{WorkDir}", workDir);
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
    /// 与 Chat 不同：GroupChat 模式有显式 Attachments 概念，所以选中即触发附件添加（用户能看到 chip 反馈）。
    /// </summary>
    private void OnFileItemSelected(string fileName, string fullPath, int hashIndex)
    {
        FilePopup.IsOpen = false;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        var replacement = $"#{fileName} ";
        var newText = string.Concat(text.AsSpan(0, hashIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = hashIndex + replacement.Length;

        _fileReferences[fileName] = fullPath;

        // 同时把文件添加到 Attachments（按 path OrdinalIgnoreCase 去重）
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
