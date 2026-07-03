using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.UI.ViewModels.Common;
using Netor.Cortana.UI.ViewModels.ExpertMode;
using Netor.Cortana.UI.ViewModels.Meeting;
using Netor.Cortana.UI.ViewModels.WorkModeVm;

namespace Netor.Cortana.UI.Controls.ExpertMode;

/// <summary>
/// 专家模式（Chat）共享输入区控件。
///
/// 使用方式：
///   1. 父控件构造时调用 <see cref="SetInputVm"/> 传入 IInputVm 实例。
///   2. 父控件指定 <see cref="InputMode"/> 属性（决定按钮显隐）。
///   3. 父控件指定 <see cref="MarqueeColor"/> 属性（走马灯颜色：蓝 #007acc）。
///
/// 功能：
///   @智能体补全、#文件补全、厂商/模型按钮、附件管理、拖放上传，走马灯蓝 #007acc
/// </summary>
public partial class InputAreaView : UserControl
{
    // ──── AvaloniaProperties ────

    /// <summary>输入模式（决定按钮显隐和 Popup 类型）。</summary>
    public static readonly StyledProperty<InputMode> InputModeProperty =
        AvaloniaProperty.Register<InputAreaView, InputMode>(nameof(InputMode), InputMode.Chat);

    /// <summary>走马灯颜色（默认蓝色 #007acc）。</summary>
    public static readonly StyledProperty<Color> MarqueeColorProperty =
        AvaloniaProperty.Register<InputAreaView, Color>(nameof(MarqueeColor), Color.Parse("#007acc"));

    public InputMode InputMode
    {
        get => GetValue(InputModeProperty);
        set => SetValue(InputModeProperty, value);
    }

    public Color MarqueeColor
    {
        get => GetValue(MarqueeColorProperty);
        set => SetValue(MarqueeColorProperty, value);
    }

    // ──── 私有字段 ────

    private IInputVm? _inputVm;

    /// <summary>走马灯动画 Timer（16ms / 帧）。</summary>
    private DispatcherTimer? _inputMarqueeTimer;

    /// <summary>走马灯动画 Stopwatch（计算进度）。</summary>
    private readonly Stopwatch _inputMarqueeStopwatch = new();

    /// <summary>停止按钮旋转动画 Timer（Avalonia 循环动画不能直接 Run）。</summary>
    private DispatcherTimer? _spinnerTimer;
    private readonly Stopwatch _spinnerStopwatch = new();
    private bool _isMarqueeActive;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // 附件/拖放视觉反馈静态字段
    private static readonly IBrush BorderNormal = SolidColorBrush.Parse("#3c3c3c");
    private static readonly IBrush BorderActive = SolidColorBrush.Parse("#007ACC");
    private static readonly IBrush BgNormal = SolidColorBrush.Parse("#252526");
    private static readonly IBrush BgDragOver = SolidColorBrush.Parse("#1a007ACC");
    private bool _isDragOver;

    /// <summary>文件引用映射（fileName → fullPath），#文件补全 使用。</summary>
    private readonly Dictionary<string, string> _fileReferences = new(StringComparer.OrdinalIgnoreCase);

    // @ 智能体补全（Chat 模式）
    private readonly Dictionary<string, AgentEntity> _agentMentions = new(StringComparer.OrdinalIgnoreCase);
    private List<AgentEntity> _currentAgentSuggestions = [];
    private int _agentPopupSelectedIndex = -1;
    private int _currentAgentAtIndex = -1;

    // ──── 构造 ────

    public InputAreaView()
    {
        InitializeComponent();

        // Tunnel KeyDown（先于 TextBox 内部处理 Enter）
        InputBox.AddHandler(KeyDownEvent, OnInputBoxKeyDown, RoutingStrategies.Tunnel);

        // # 文件补全 + @ 智能体补全
        InputBox.TextChanged += OnInputTextChanged;

        // 输入框焦点效果
        InputBox.GotFocus += (_, _) => { if (!_isDragOver && !_isMarqueeActive) InputBorder.BorderBrush = BorderActive; };
        InputBox.LostFocus += (_, _) => { if (!_isDragOver && !_isMarqueeActive) InputBorder.BorderBrush = BorderNormal; };

        // 注册 AvaloniaProperty 变化回调
        InputModeProperty.Changed.AddClassHandler<InputAreaView>((s, _) => s.OnInputModeChanged());
        MarqueeColorProperty.Changed.AddClassHandler<InputAreaView>((s, _) => s.UpdateMarqueeColors());

        // 控件挂载到可视树后注册拖放 + 初始化模式状态
        // 注意：必须在此处调用 OnInputModeChanged()，因为 AvaloniaProperty.Changed
        // 只在值发生 变化 时触发。若父控件设置 InputMode="Chat"（与默认值相同），
        // 则 Changed 事件不会触发，导致按钮显隐和走马灯颜色永远处于 AXAML 默认状态。
        AttachedToVisualTree += (_, _) =>
        {
            InputBorder.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            InputBorder.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
            InputBorder.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            InputBorder.AddHandler(DragDrop.DropEvent, OnDrop);

            // 强制初始化当前 InputMode 对应的按钮显隐和走马灯颜色
            OnInputModeChanged();
        };
    }

    // ──── 公共 API ────

    /// <summary>
    /// 接受外部文件路径列表，添加到 _inputVm.Attachments（供 LeftPanel 文件树回调或 MainWindow 代理）。
    /// 支持文件和文件夹（文件夹通过 FolderAttachmentScanner 扫描后汇总文件数 + 总大小）。
    /// </summary>
    public void AddExternalAttachments(IReadOnlyList<string> paths)
    {
        if (_inputVm is null) return;

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
                _inputVm.Attachments.Add(new AttachmentInfo(
                    path,
                    System.IO.Path.GetFileName(path),
                    FileContentTypeResolver.GetMimeType(path)));
            }
        }
        // Attachments.CollectionChanged 已订阅 → 自动触发 RenderAttachments
    }

    /// <summary>
    /// 设置 IInputVm 实例（由父控件构造时调用）。
    /// 同时订阅 VM 的 PropertyChanged，驱动动画 + ValidationError 显隐 + Label 同步。
    /// </summary>
    public void SetInputVm(IInputVm vm)
    {
        if (_inputVm is not null)
            _inputVm.PropertyChanged -= OnInputVmPropertyChanged;

        _inputVm = vm;
        DataContext = vm;
        _inputVm.PropertyChanged += OnInputVmPropertyChanged;

        // 监听 Attachments 变化 → 触发渲染
        _inputVm.Attachments.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                RenderAttachments();
                UpdateSendButtonEnabled();
            });

        // 初始填充 Popup 列表 + 标签
        Dispatcher.UIThread.Post(InitializePopupsAndLabels);
    }

    // ──── InputMode 控制 ────

    private void OnInputModeChanged()
    {
        ChatSubmitModePanel.IsVisible = InputMode == InputMode.Chat && _inputVm is ChatInputVm;

        // 厂商/模型按钮（Chat 模式始终显示）
        ProviderSelectorBtn.IsVisible = true;
        ModelSelectorBtn.IsVisible = true;

        // 工具按钮（Chat 模式不显示）
        ToolButton.IsVisible = false;

        // @ 智能体补全 Popup（仅 Chat 可见，实际打开由 OnInputTextChanged 控制）
        // AgentPopup 本身 IsVisible 不设，由 IsOpen 控制

        // 走马灯颜色初始化
        UpdateMarqueeColors();
    }

    // ──── 走马灯颜色 ────

    private void UpdateMarqueeColors()
    {
        var c = MarqueeColor;
        var transparent = new Color(0, c.R, c.G, c.B);
        var opaque = new Color(255, c.R, c.G, c.B);

        SetRectangleGradient(InputMarqueeTop, transparent, opaque, transparent, horizontal: true, reverse: false);
        SetRectangleGradient(InputMarqueeRight, transparent, opaque, transparent, horizontal: false, reverse: false);
        SetRectangleGradient(InputMarqueeBottom, transparent, opaque, transparent, horizontal: true, reverse: true);
        SetRectangleGradient(InputMarqueeLeft, transparent, opaque, transparent, horizontal: false, reverse: true);
    }

    private static void SetRectangleGradient(Avalonia.Controls.Shapes.Rectangle rect, Color start, Color mid, Color end, bool horizontal, bool reverse)
    {
        var s = reverse ? end : start;
        var e = reverse ? start : end;
        rect.Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, 0, RelativeUnit.Relative)
                : new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(s, 0),
                new GradientStop(mid, 0.5),
                new GradientStop(e, 1),
            }
        };
    }

    // ──── VM 事件响应 ────

    private void OnInputVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(IInputVm.IsRunning):
                    // IsRunning 变化只影响按钮显隐和发送按钮启用状态
                    if (_inputVm!.IsTaskActive && _inputVm.IsRunning)
                        StartSpinnerAndMarquee();
                    else
                        StopSpinnerAndMarquee();
                    UpdateButtonVisibility();
                    UpdateSendButtonEnabled();
                    break;

                case nameof(IInputVm.IsTaskActive):
                    // 工作模式等待用户确认时 IsTaskActive=true、IsRunning=false，此时应停止动画。
                    if (_inputVm!.IsTaskActive && _inputVm.IsRunning)
                        StartSpinnerAndMarquee();
                    else
                        StopSpinnerAndMarquee();
                    UpdateButtonVisibility();
                    UpdateSendButtonEnabled();
                    break;

                case nameof(WorkModeInputVm.IsStepExecutionPhase):
                    UpdateButtonVisibility();
                    UpdateSendButtonEnabled();
                    break;

                case nameof(IInputVm.ValidationError):
                    var err = _inputVm?.ValidationError;
                    ValidationErrorText.IsVisible = !string.IsNullOrEmpty(err);
                    ValidationErrorText.Text = err ?? string.Empty;
                    break;

                case nameof(IInputVm.InitialInput):
                    // VM 的 InitialInput 被清空（发送后）→ 清空 TextBox
                    var vmText = _inputVm?.InitialInput ?? string.Empty;
                    if (string.IsNullOrEmpty(vmText) && !string.IsNullOrEmpty(InputBox.Text))
                    {
                        InputBox.Text = string.Empty;
                    }
                    UpdateSendButtonEnabled();
                    break;
            }

            // Chat / Meeting 专属标签同步
            if (_inputVm is ChatInputVm cvm)
            {
                switch (e.PropertyName)
                {
                    case nameof(ChatInputVm.SubmitMode):
                    case nameof(ChatInputVm.IsChatSubmitMode):
                    case nameof(ChatInputVm.IsImageSubmitMode):
                    case nameof(ChatInputVm.IsVideoSubmitMode):
                        SyncChatSubmitModeButtons(cvm);
                        UpdateButtonVisibility();
                        break;
                    case nameof(ChatInputVm.SelectedAgentName):
                        ToolbarAgentLabel.Text = cvm.SelectedAgentName;
                        break;
                    case nameof(ChatInputVm.SelectedProviderName):
                        ToolbarProviderLabel.Text = cvm.SelectedProviderName;
                        break;
                    case nameof(ChatInputVm.SelectedModelName):
                        ToolbarModelLabel.Text = cvm.SelectedModelName;
                        break;
                }
            }
            else if (_inputVm is MeetingInputVm mvm)
            {
                switch (e.PropertyName)
                {
                    case nameof(MeetingInputVm.SelectedAgentsLabel):
                        ToolbarAgentLabel.Text = mvm.SelectedAgentsLabel;
                        break;
                    case nameof(MeetingInputVm.SelectedProviderName):
                        ToolbarProviderLabel.Text = mvm.SelectedProviderName;
                        break;
                    case nameof(MeetingInputVm.SelectedModelName):
                        ToolbarModelLabel.Text = mvm.SelectedModelName;
                        break;
                }
            }
            else if (_inputVm is WorkModeInputVm wvm)
            {
                switch (e.PropertyName)
                {
                    case nameof(WorkModeInputVm.SelectedAgentName):
                        ToolbarAgentLabel.Text = wvm.SelectedAgentName;
                        break;
                    case nameof(WorkModeInputVm.SelectedProviderName):
                        ToolbarProviderLabel.Text = wvm.SelectedProviderName;
                        break;
                    case nameof(WorkModeInputVm.SelectedModelName):
                        ToolbarModelLabel.Text = wvm.SelectedModelName;
                        break;
                }
            }
        });
    }

    // ──── 初始化 Popup 和标签 ────

    private void InitializePopupsAndLabels()
    {
        if (_inputVm is null) return;

        // 工具屏蔽列表（已移除，待重构）
        RenderAttachments();

        if (_inputVm is ChatInputVm cvm)
        {
            ToolbarAgentLabel.Text = cvm.SelectedAgentName;
            ToolbarProviderLabel.Text = cvm.SelectedProviderName;
            ToolbarModelLabel.Text = cvm.SelectedModelName;
            SyncChatSubmitModeButtons(cvm);
            FillAgentSelectorListSingle(cvm.AvailableAgents, cvm.SelectedAgent?.Id);
            FillProviderSelectorList(cvm.AvailableProviders, cvm.SelectedProvider?.Id);
            FillModelSelectorList(cvm.AvailableModels, cvm.SelectedModel?.Id);
        }
        else if (_inputVm is MeetingInputVm mvm)
        {
            ToolbarAgentLabel.Text = mvm.SelectedAgentsLabel;
            ToolbarProviderLabel.Text = mvm.SelectedProviderName;
            ToolbarModelLabel.Text = mvm.SelectedModelName;
            FillAgentSelectorListMulti(mvm.AvailableAgents, mvm.SelectedAgentIds);
            FillProviderSelectorList(mvm.AvailableProviders, mvm.SelectedProvider?.Id);
            FillModelSelectorList(mvm.AvailableModels, mvm.SelectedModel?.Id);
        }
        else if (_inputVm is WorkModeInputVm wvm)
        {
            ToolbarAgentLabel.Text = wvm.SelectedAgentName;
            ToolbarProviderLabel.Text = wvm.SelectedProviderName;
            ToolbarModelLabel.Text = wvm.SelectedModelName;
            FillAgentSelectorListSingle(wvm.AvailableAgents, wvm.SelectedAgent?.Id);
            FillProviderSelectorList(wvm.AvailableProviders, wvm.SelectedProvider?.Id);
            FillModelSelectorList(wvm.AvailableModels, wvm.SelectedModel?.Id);
        }
    }

    // ──── 按钮显隐 ────

    /// <summary>
    /// 更新发送/停止按钮的可见性。
    /// - 有活跃任务（IsTaskActive）：停止按钮始终可见（带旋转动画），发送按钮也可见（用户可发送消息）
    /// - 无活跃任务：只显示发送按钮
    /// </summary>
    private void UpdateButtonVisibility()
    {
        if (_inputVm is null) return;

        AttachButton.IsEnabled = _inputVm.IsIdle
            && _inputVm is not ChatInputVm { IsChatSubmitMode: false };

        if (_inputVm.IsTaskActive)
        {
            // 工作模式只有步骤执行阶段才显示暂停按钮；其他阶段用户按普通对话发送。
            StopButton.IsVisible = _inputVm is not WorkModeInputVm workModeVm || workModeVm.IsStepExecutionPhase;
            SendButton.IsVisible = true;
        }
        else
        {
            // 无任务：只显示发送按钮
            StopButton.IsVisible = false;
            SendButton.IsVisible = true;
        }
    }

    // ──── 发送 / 取消 ────

    /// <summary>
    /// 根据当前输入框内容和 VM 状态，更新发送按钮的 IsEnabled。
    /// 完全由 code-behind 控制（AXAML 里 IsEnabled="False"）。
    /// 工作模式下有活跃任务时（对话模式），只要有文本就可发送。
    /// </summary>
    private void UpdateSendButtonEnabled()
    {
        var hasText = !string.IsNullOrWhiteSpace(InputBox.Text);
        if (_inputVm is null)
        {
            SendButton.IsEnabled = false;
            return;
        }

        var canSubmitByContent = _inputVm is ChatInputVm { IsChatSubmitMode: false }
            ? hasText
            : hasText || _inputVm.Attachments.Count > 0;

        // 会议运行中允许用户插话；工作模式只有步骤执行阶段才允许插话。
        if (_inputVm is MeetingInputVm meetingVm && meetingVm.HasActiveMeeting)
        {
            SendButton.IsEnabled = canSubmitByContent;
            return;
        }

        if (_inputVm is WorkModeInputVm workModeVm && workModeVm.IsStepExecutionPhase)
        {
            SendButton.IsEnabled = canSubmitByContent;
            return;
        }

        if (_inputVm.IsTaskActive && !_inputVm.IsRunning)
        {
            SendButton.IsEnabled = canSubmitByContent;
            return;
        }

        // 无任务时：空闲 + 有文本
        var enabled = _inputVm.IsIdle && canSubmitByContent;
        SendButton.IsEnabled = enabled;
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is null) return;
        if (!await _sendGate.WaitAsync(0))
        {
            return;
        }

        try
        {
        var rawText = InputBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(rawText) && _inputVm.Attachments.Count == 0) return;

        _inputVm.InitialInput = rawText;
        if (_inputVm is WorkModeInputVm workModeVm)
        {
            workModeVm.SetPendingMentions(CollectAgentMentions(rawText));
        }

        try
        {
            await _inputVm.SubmitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ValidationErrorText.IsVisible = true;
            ValidationErrorText.Text = $"发送失败：{ex.Message}";
        }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is null) return;
        try { await _inputVm.CancelAsync(CancellationToken.None); }
        catch { /* 忽略 */ }
    }

    private void OnChatModeClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is not ChatInputVm chatVm) return;

        chatVm.SubmitMode = ChatSubmitMode.Chat;
        SyncChatSubmitModeButtons(chatVm);
    }

    private void OnImageModeClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is not ChatInputVm chatVm) return;

        chatVm.SubmitMode = ChatSubmitMode.Image;
        chatVm.Attachments.Clear();
        SyncChatSubmitModeButtons(chatVm);
    }

    private void OnVideoModeClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is not ChatInputVm chatVm) return;

        chatVm.SubmitMode = ChatSubmitMode.Video;
        chatVm.Attachments.Clear();
        SyncChatSubmitModeButtons(chatVm);
    }

    private void SyncChatSubmitModeButtons(ChatInputVm chatVm)
    {
        ChatSubmitModePanel.IsVisible = InputMode == InputMode.Chat;
        ApplyActiveClass(ChatModeButton, chatVm.IsChatSubmitMode);
        ApplyActiveClass(ImageModeButton, chatVm.IsImageSubmitMode);
        ApplyActiveClass(VideoModeButton, chatVm.IsVideoSubmitMode);
        AttachButton.IsEnabled = chatVm.IsIdle && chatVm.IsChatSubmitMode;
        UpdateSendButtonEnabled();
    }

    // ──── 键盘快捷键 ────

    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // @ 智能体补全弹出时的键盘导航（Chat 模式）
        if (AgentPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (_currentAgentSuggestions.Count > 0)
                {
                    _agentPopupSelectedIndex = (_agentPopupSelectedIndex + 1) % _currentAgentSuggestions.Count;
                    HighlightAgentItem(_agentPopupSelectedIndex);
                }
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (_currentAgentSuggestions.Count > 0)
                {
                    _agentPopupSelectedIndex = _agentPopupSelectedIndex <= 0
                        ? _currentAgentSuggestions.Count - 1
                        : _agentPopupSelectedIndex - 1;
                    HighlightAgentItem(_agentPopupSelectedIndex);
                }
                return;
            }
            if (e.Key is Key.Enter or Key.Tab)
            {
                e.Handled = true;
                var idx = _agentPopupSelectedIndex >= 0 && _agentPopupSelectedIndex < _currentAgentSuggestions.Count
                    ? _agentPopupSelectedIndex : 0;
                if (_currentAgentSuggestions.Count > 0)
                    OnAgentMentionSelected(_currentAgentSuggestions[idx], _currentAgentAtIndex);
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AgentPopup.IsOpen = false;
                return;
            }
        }

        // FilePopup 键盘
        if (FilePopup.IsOpen && e.Key == Key.Escape)
        {
            e.Handled = true;
            FilePopup.IsOpen = false;
            return;
        }

        // Enter 发送（非 Shift+Enter）
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            FilePopup.IsOpen = false;
            AgentPopup.IsOpen = false;

            if (_inputVm is MeetingInputVm { HasActiveMeeting: true } && !string.IsNullOrWhiteSpace(InputBox.Text))
            {
                OnSendClick(sender, new RoutedEventArgs());
                return;
            }

            if (_inputVm is WorkModeInputVm { IsStepExecutionPhase: true } && !string.IsNullOrWhiteSpace(InputBox.Text))
            {
                OnSendClick(sender, new RoutedEventArgs());
                return;
            }

            if (_inputVm is not null && _inputVm.IsIdle && !string.IsNullOrWhiteSpace(InputBox.Text))
            {
                OnSendClick(sender, new RoutedEventArgs());
            }
        }
    }

    // ──── # 文件补全 ────

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 同步到 VM 并更新发送按钮状态
        SyncInputTextToViewModel();
        UpdateSendButtonEnabled();

        // 三种模式都支持 @ 智能体补全。
        if (TryShowAgentPopup(text, caret))
        {
            FilePopup.IsOpen = false;
            return;
        }
        AgentPopup.IsOpen = false;

        // # 文件补全（三种模式都有）
        var hashIndex = text.LastIndexOf('#', Math.Max(0, caret - 1));
        if (hashIndex < 0) { FilePopup.IsOpen = false; return; }

        var afterHash = text.Substring(hashIndex + 1, caret - hashIndex - 1);
        if (afterHash.Contains(' ') || afterHash.Contains('\n')) { FilePopup.IsOpen = false; return; }

        var appPaths = App.Services.GetRequiredService<IAppPaths>();
        var workDir = appPaths.WorkspaceDirectory;
        if (!Directory.Exists(workDir)) { FilePopup.IsOpen = false; return; }

        try
        {
            var files = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                .Select(f => (FullPath: f, RelativePath: Path.GetRelativePath(workDir, f)))
                .Where(f => !f.RelativePath.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal)
                         && !f.RelativePath.StartsWith('.'))
                .Where(f => string.IsNullOrEmpty(afterHash)
                         || f.RelativePath.Contains(afterHash, StringComparison.OrdinalIgnoreCase))
                .Take(30).ToList();

            if (files.Count == 0) { FilePopup.IsOpen = false; return; }

            FillFileList(files, hashIndex);
            FilePopup.IsOpen = true;
        }
        catch { FilePopup.IsOpen = false; }
    }

    private void FillFileList(List<(string FullPath, string RelativePath)> files, int hashIndex)
    {
        FileList.Items.Clear();
        foreach (var (fullPath, relativePath) in files)
        {
            var fileName = Path.GetFileName(fullPath);
            var dirPart = Path.GetDirectoryName(relativePath) ?? string.Empty;

            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 1 };
            sp.Children.Add(new TextBlock { Text = fileName, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#cccccc")) });
            if (!string.IsNullOrEmpty(dirPart))
                sp.Children.Add(new TextBlock { Text = dirPart, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")) });

            var btn = new Button { Classes = { "selector-item" }, Content = sp, Tag = fullPath };
            btn.Click += (_, _) => OnFileItemSelected(fileName, fullPath, hashIndex);
            FileList.Items.Add(btn);
        }
    }

    private void OnFileItemSelected(string fileName, string fullPath, int hashIndex)
    {
        FilePopup.IsOpen = false;
        if (_inputVm is null) return;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;
        var replacement = $"#{fileName} ";
        var newText = string.Concat(text.AsSpan(0, hashIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = hashIndex + replacement.Length;

        _fileReferences[fileName] = fullPath;

        // 同时把文件加入附件（按 path 去重）
        if (File.Exists(fullPath) &&
            !_inputVm.Attachments.Any(a => string.Equals(a.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            _inputVm.Attachments.Add(new AttachmentInfo(fullPath, fileName, FileContentTypeResolver.GetMimeType(fullPath)));
        }
    }

    // ──── @ 智能体补全（Chat 模式） ────

    private bool TryShowAgentPopup(string text, int caret)
    {
        var atIdx = text.LastIndexOf('@', Math.Max(0, caret - 1));
        if (atIdx < 0) return false;

        var afterAt = text.Substring(atIdx + 1, caret - atIdx - 1);
        if (afterAt.Contains(' ') || afterAt.Contains('\n')) return false;

        var availableAgents = _inputVm switch
        {
            ChatInputVm cvm => cvm.AvailableAgents,
            MeetingInputVm mvm => mvm.AvailableAgents,
            WorkModeInputVm wvm => wvm.AvailableAgents,
            _ => []
        };

        _currentAgentAtIndex = atIdx;
        _currentAgentSuggestions = availableAgents
            .Where(a => string.IsNullOrEmpty(afterAt) || a.Name.Contains(afterAt, StringComparison.OrdinalIgnoreCase))
            .Take(10).ToList();

        if (_currentAgentSuggestions.Count == 0) return false;

        FillAgentPopupList(_currentAgentSuggestions);
        _agentPopupSelectedIndex = -1;
        AgentPopup.IsOpen = true;
        return true;
    }

    private void FillAgentPopupList(List<AgentEntity> agents)
    {
        AgentList.Items.Clear();
        foreach (var agent in agents)
        {
            var btn = new Button
            {
                Classes = { "selector-item" },
                Content = new TextBlock { Text = agent.Name, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#cccccc")) },
                Tag = agent,
            };
            var captured = agent;
            btn.Click += (_, _) => OnAgentMentionSelected(captured, _currentAgentAtIndex);
            AgentList.Items.Add(btn);
        }
    }

    private void HighlightAgentItem(int index)
    {
        var items = AgentList.Items.OfType<Button>().ToList();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Classes.Clear();
            items[i].Classes.Add(i == index ? "selector-item-active" : "selector-item");
        }
    }

    private void OnAgentMentionSelected(AgentEntity agent, int atIndex)
    {
        AgentPopup.IsOpen = false;
        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;
        var replacement = $"@{agent.Name} ";
        var newText = string.Concat(text.AsSpan(0, atIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = atIndex + replacement.Length;
        _agentMentions[agent.Name] = agent;
    }

    private List<AgentMention>? CollectAgentMentions(string text)
    {
        if (_agentMentions.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        List<AgentMention>? mentions = null;
        foreach (var (name, agent) in _agentMentions)
        {
            var token = $"@{name}";
            var start = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;

            mentions ??= [];
            mentions.Add(new AgentMention(agent, start, start + token.Length));
        }

        return mentions;
    }

    // ──── 附件 ────

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is null) return;
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
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                _inputVm.Attachments.Add(new AttachmentInfo(path, file.Name, FileContentTypeResolver.GetMimeType(path)));
            }
        }
        catch { /* 用户取消或错误，忽略 */ }
    }

    private void OnRemoveAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is null) return;
        if (sender is Button btn && btn.Tag is int index
            && index >= 0 && index < _inputVm.Attachments.Count)
        {
            _inputVm.Attachments.RemoveAt(index);
        }
    }

    private void RenderAttachments()
    {
        if (_inputVm is null) return;
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes / 1024.0 / 1024.0:F1}MB";
    }

    // ──── 拖放 ────

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        _isDragOver = true;
        InputBorder.BorderBrush = BorderActive;
        InputBorder.BorderThickness = new Thickness(2);
        InputBorder.Background = BgDragOver;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();
        if (_inputVm is null) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (string.IsNullOrEmpty(path)) continue;
            if (_inputVm.Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            if (Directory.Exists(path))
            {
                var scanner = new Netor.Cortana.Entitys.Services.FolderAttachmentScanner();
                var result = scanner.Scan(path);
                var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                _inputVm.Attachments.Add(new AttachmentInfo(path, folderName, "inode/directory",
                    IsFolder: true, FileCount: result.FileCount, TotalBytes: result.TotalBytes));
            }
            else if (File.Exists(path))
            {
                _inputVm.Attachments.Add(new AttachmentInfo(path, Path.GetFileName(path), FileContentTypeResolver.GetMimeType(path)));
            }
        }
        e.Handled = true;
    }

    private void RestoreInputBorder()
    {
        if (_isMarqueeActive)
        {
            ApplyMarqueeChrome();
            return;
        }

        InputBorder.BorderThickness = new Thickness(1);
        InputBorder.Background = BgNormal;
        InputBorder.BorderBrush = InputBox.IsFocused ? BorderActive : BorderNormal;
        InputBorder.BoxShadow = default;
    }

    // ──── 工具弹出（已移除，待重构） ────

    // ──── 动画 ────

    private void StartSpinnerAndMarquee()
    {
        if (_isMarqueeActive)
        {
            return;
        }

        _spinnerStopwatch.Restart();
        _spinnerTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => UpdateSpinner());
        _spinnerTimer.Start();
        UpdateSpinner();

        _isMarqueeActive = true;
        InputMarqueeBorder.IsVisible = true;
        ApplyMarqueeChrome();
        _inputMarqueeStopwatch.Restart();
        _inputMarqueeTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => UpdateInputMarquee());
        _inputMarqueeTimer.Start();
        UpdateInputMarquee();
    }

    private void StopSpinnerAndMarquee()
    {
        _spinnerTimer?.Stop();
        _spinnerStopwatch.Reset();
        if (SpinnerIcon.RenderTransform is RotateTransform rotate)
        {
            rotate.Angle = 0;
        }

        _inputMarqueeTimer?.Stop();
        _inputMarqueeStopwatch.Reset();
        _isMarqueeActive = false;
        InputMarqueeBorder.IsVisible = false;
        RestoreInputBorder();
    }

    private void UpdateSpinner()
    {
        if (SpinnerIcon.RenderTransform is RotateTransform rotate)
        {
            rotate.Angle = (_spinnerStopwatch.Elapsed.TotalSeconds % 1d) * 360d;
        }
    }

    private void UpdateInputMarquee()
    {
        ApplyMarqueeChrome();
        var width = Math.Max(InputBorderHost.Bounds.Width, 1);
        var height = Math.Max(InputBorderHost.Bounds.Height, 1);
        InputMarqueeCanvas.Width = width;
        InputMarqueeCanvas.Height = height;
        var segmentLength = GetMarqueeSegmentLength();
        var segmentThickness = GetMarqueeSegmentThickness();
        InputMarqueeTop.Width = segmentLength;
        InputMarqueeTop.Height = segmentThickness;
        InputMarqueeRight.Width = segmentThickness;
        InputMarqueeRight.Height = segmentLength;
        InputMarqueeBottom.Width = segmentLength;
        InputMarqueeBottom.Height = segmentThickness;
        InputMarqueeLeft.Width = segmentThickness;
        InputMarqueeLeft.Height = segmentLength;

        var durationSeconds = InputMode == InputMode.Meeting ? 1.65d : 1.9d;
        var linearProgress = _inputMarqueeStopwatch.Elapsed.TotalSeconds % durationSeconds / durationSeconds;
        var progress = EaseInOutCubic(linearProgress);

        Canvas.SetLeft(InputMarqueeTop, -segmentLength + (width + segmentLength) * progress);
        Canvas.SetTop(InputMarqueeTop, 0);
        Canvas.SetLeft(InputMarqueeRight, width - segmentThickness);
        Canvas.SetTop(InputMarqueeRight, -segmentLength + (height + segmentLength) * progress);
        Canvas.SetLeft(InputMarqueeBottom, width - segmentLength - (width + segmentLength) * progress);
        Canvas.SetTop(InputMarqueeBottom, height - segmentThickness);
        Canvas.SetLeft(InputMarqueeLeft, 0);
        Canvas.SetTop(InputMarqueeLeft, height - segmentLength - (height + segmentLength) * progress);
    }

    private static double EaseInOutCubic(double v)
        => v < 0.5 ? 4 * v * v * v : 1 - Math.Pow(-2 * v + 2, 3) / 2;

    private void ApplyMarqueeChrome()
    {
        var color = MarqueeColor;
        var borderAlpha = InputMode == InputMode.Meeting ? (byte)78 : (byte)110;
        var shadowAlpha = InputMode == InputMode.Meeting ? (byte)62 : (byte)90;
        InputBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, color.R, color.G, color.B));
        InputBorder.BorderThickness = new Thickness(1);
        InputBorder.BoxShadow = new BoxShadows(new BoxShadow
        {
            Color = Color.FromArgb(shadowAlpha, color.R, color.G, color.B),
            Blur = InputMode == InputMode.Meeting ? 10 : 14,
            Spread = 0,
            OffsetX = 0,
            OffsetY = 0,
        });
    }

    private double GetMarqueeSegmentLength()
        => InputMode == InputMode.Meeting ? 180d : 120d;

    private double GetMarqueeSegmentThickness()
        => InputMode == InputMode.Meeting ? 3d : 2d;

    // ──── 选择器 Popup 填充 ────

    private void OnAgentSelectorClick(object? sender, RoutedEventArgs e)
    {
        var (agents, activeId) = _inputVm switch
        {
            ChatInputVm cvm => (cvm.AvailableAgents.Cast<AgentEntity>().ToList(), cvm.SelectedAgent?.Id),
            MeetingInputVm mvm => (mvm.AvailableAgents.Cast<AgentEntity>().ToList(), (string?)null),
            WorkModeInputVm wvm => (wvm.AvailableAgents.Cast<AgentEntity>().ToList(), wvm.SelectedAgent?.Id),
            _ => (new List<AgentEntity>(), null)
        };
        if (_inputVm is MeetingInputVm meetingVm)
        {
            FillAgentSelectorListMulti(agents, meetingVm.SelectedAgentIds);
        }
        else
        {
            FillAgentSelectorListSingle(agents, activeId);
        }
        AgentSelectorPopup.IsOpen = !AgentSelectorPopup.IsOpen;
    }

    private void FillAgentSelectorListSingle(IEnumerable<AgentEntity> agents, string? activeId)
    {
        AgentSelectorList.Items.Clear();
        foreach (var agent in agents)
        {
            var id = agent.Id;
            var isActive = id == activeId;
            var btn = new Button
            {
                Classes = { isActive ? "selector-item-active" : "selector-item" },
                Tag = id,
                Content = new TextBlock
                {
                    Text = agent.Name, FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse(isActive ? "#007acc" : "#cccccc")),
                },
            };
            var captured = agent;
            btn.Click += (_, _) => OnAgentSingleItemClick(captured);
            AgentSelectorList.Items.Add(btn);
        }
    }

    private void OnAgentSingleItemClick(AgentEntity agent)
    {
        AgentSelectorPopup.IsOpen = false;
        if (_inputVm is ChatInputVm cvm)
        {
            cvm.SelectedAgent = agent;
            FillAgentSelectorListSingle(cvm.AvailableAgents, cvm.SelectedAgent?.Id);
            FillProviderSelectorList(cvm.AvailableProviders, cvm.SelectedProvider?.Id);
            FillModelSelectorList(cvm.AvailableModels, cvm.SelectedModel?.Id);
        }
        else if (_inputVm is WorkModeInputVm wvm)
        {
            wvm.SelectedAgent = agent;
            FillAgentSelectorListSingle(wvm.AvailableAgents, wvm.SelectedAgent?.Id);
            FillProviderSelectorList(wvm.AvailableProviders, wvm.SelectedProvider?.Id);
            FillModelSelectorList(wvm.AvailableModels, wvm.SelectedModel?.Id);
        }
    }

    private void FillAgentSelectorListMulti(IEnumerable<AgentEntity> agents, IReadOnlySet<string> selectedIds)
    {
        AgentSelectorList.Items.Clear();
        foreach (var agent in agents)
        {
            var id = agent.Id;
            var checkBox = new CheckBox
            {
                Content = agent.Name,
                IsChecked = selectedIds.Contains(id),
                Tag = id,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                FontSize = 12,
                Padding = new Thickness(8, 4),
            };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (_inputVm is not MeetingInputVm meetingVm)
                {
                    return;
                }

                meetingVm.ToggleAgent(id, checkBox.IsChecked == true);
                ToolbarAgentLabel.Text = meetingVm.SelectedAgentsLabel;
            };
            AgentSelectorList.Items.Add(checkBox);
        }
    }

    private void OnProviderSelectorClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is MeetingInputVm { HasActiveMeeting: true } meetingVm)
        {
            meetingVm.ShowSelectionLockedNotice();
        }

        var (providers, activeId) = _inputVm switch
        {
            ChatInputVm cvm => (cvm.AvailableProviders.Cast<AiProviderEntity>().ToList(), cvm.SelectedProvider?.Id),
            MeetingInputVm mvm => (mvm.AvailableProviders.Cast<AiProviderEntity>().ToList(), mvm.SelectedProvider?.Id),
            WorkModeInputVm wvm => (wvm.AvailableProviders.Cast<AiProviderEntity>().ToList(), wvm.SelectedProvider?.Id),
            _ => (new List<AiProviderEntity>(), null)
        };
        FillProviderSelectorList(providers, activeId);
        ProviderPopup.IsOpen = !ProviderPopup.IsOpen;
    }

    private void FillProviderSelectorList(IEnumerable<AiProviderEntity> providers, string? activeId)
    {
        ProviderList.Items.Clear();
        foreach (var provider in providers)
        {
            var id = provider.Id;
            var isActive = id == activeId;
            var btn = new Button
            {
                Classes = { isActive ? "selector-item-active" : "selector-item" },
                Tag = id,
                Content = new TextBlock
                {
                    Text = provider.Name, FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse(isActive ? "#007acc" : "#cccccc")),
                },
            };
            var captured = provider;
            btn.Click += (_, _) => OnProviderItemClick(captured);
            ProviderList.Items.Add(btn);
        }
    }

    private void OnProviderItemClick(AiProviderEntity provider)
    {
        ProviderPopup.IsOpen = false;
        if (_inputVm is ChatInputVm cvm)
        {
            cvm.SelectedProvider = provider;
            FillProviderSelectorList(cvm.AvailableProviders, cvm.SelectedProvider?.Id);
            FillModelSelectorList(cvm.AvailableModels, cvm.SelectedModel?.Id);
        }
        else if (_inputVm is MeetingInputVm mvm)
        {
            mvm.SelectedProvider = provider;
            FillProviderSelectorList(mvm.AvailableProviders, mvm.SelectedProvider?.Id);
            FillModelSelectorList(mvm.AvailableModels, mvm.SelectedModel?.Id);
        }
        else if (_inputVm is WorkModeInputVm wvm)
        {
            wvm.SelectedProvider = provider;
            FillProviderSelectorList(wvm.AvailableProviders, wvm.SelectedProvider?.Id);
            FillModelSelectorList(wvm.AvailableModels, wvm.SelectedModel?.Id);
        }
    }

    private void OnModelSelectorClick(object? sender, RoutedEventArgs e)
    {
        if (_inputVm is MeetingInputVm { HasActiveMeeting: true } meetingVm)
        {
            meetingVm.ShowSelectionLockedNotice();
        }

        var (models, activeId) = _inputVm switch
        {
            ChatInputVm cvm => (cvm.AvailableModels.Cast<AiModelEntity>().ToList(), cvm.SelectedModel?.Id),
            MeetingInputVm mvm => (mvm.AvailableModels.Cast<AiModelEntity>().ToList(), mvm.SelectedModel?.Id),
            WorkModeInputVm wvm => (wvm.AvailableModels.Cast<AiModelEntity>().ToList(), wvm.SelectedModel?.Id),
            _ => (new List<AiModelEntity>(), null)
        };
        FillModelSelectorList(models, activeId);
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    private void FillModelSelectorList(IEnumerable<AiModelEntity> models, string? activeId)
    {
        ModelList.Items.Clear();
        foreach (var model in models)
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
                    Text = display, FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse(isActive ? "#007acc" : "#cccccc")),
                },
            };
            var captured = model;
            btn.Click += (_, _) => OnModelItemClick(captured);
            ModelList.Items.Add(btn);
        }
    }

    private void OnModelItemClick(AiModelEntity model)
    {
        ModelPopup.IsOpen = false;
        if (_inputVm is ChatInputVm cvm)
        {
            cvm.SelectedModel = model;
            FillModelSelectorList(cvm.AvailableModels, cvm.SelectedModel?.Id);
        }
        else if (_inputVm is MeetingInputVm mvm)
        {
            mvm.SelectedModel = model;
            FillModelSelectorList(mvm.AvailableModels, mvm.SelectedModel?.Id);
        }
        else if (_inputVm is WorkModeInputVm wvm)
        {
            wvm.SelectedModel = model;
            FillModelSelectorList(wvm.AvailableModels, wvm.SelectedModel?.Id);
        }
    }

    // ──── 工具方法 ────

    private static void ApplyActiveClass(Button btn, bool isActive)
    {
        btn.Classes.Clear();
        btn.Classes.Add(isActive ? "selector-item-active" : "selector-item");
    }

    private void SyncInputTextToViewModel()
    {
        if (_inputVm is null) return;
        var text = InputBox.Text ?? string.Empty;
        if (string.Equals(_inputVm.InitialInput, text, StringComparison.Ordinal)) return;
        _inputVm.InitialInput = text;
    }
}

/// <summary>输入模式枚举。</summary>
public enum InputMode
{
    Chat,
    Meeting,
}
