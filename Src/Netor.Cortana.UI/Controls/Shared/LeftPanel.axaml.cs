using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Netor.Cortana.UI.ViewModels.Shared;

namespace Netor.Cortana.UI.Controls.Shared;

/// <summary>
/// 左侧面板用户控件。
///
/// 容器结构（Grid Rows = 36 / * / 32）：
/// - Row 0：标题栏占位
/// - Row 1：Tab 内容区（Tab1 = WorkspaceExplorer 文件树，Tab2 = ChatHistoryPanel）
/// - Row 2：底部 Tab 切换栏
/// </summary>
public partial class LeftPanel : UserControl
{
    private LeftPanelVm? _vm;

    public LeftPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // 内部 WorkspaceExplorer 的事件转发到本控件对外暴露的 event
        FileExplorerHost.AttachmentRequested += paths => AttachmentRequested?.Invoke(paths);
        FileExplorerHost.WorkflowAttachmentRequested += paths => WorkflowAttachmentRequested?.Invoke(paths);
        FileExplorerHost.GroupChatAttachmentRequested += paths => GroupChatAttachmentRequested?.Invoke(paths);
        FileExplorerHost.WorkspacePanelCollapseRequested += () => WorkspacePanelCollapseRequested?.Invoke();

        // ChatHistoryPanel 的事件转发到本控件对外暴露的 event
        ChatHistoryPanelHost.SessionSelected += (id, title) => SessionSelected?.Invoke(id, title);
        ChatHistoryPanelHost.RequestNewSession += () => RequestNewSession?.Invoke();
        WorkTaskHistoryPanelHost.TaskSelected += (id, title) => WorkTaskSelected?.Invoke(id, title);
        MeetingHistoryPanelHost.MeetingSelected += (id, title) => MeetingSelected?.Invoke(id, title);
    }

    /// <summary>当前工作目录路径。转发到内部 WorkspaceExplorer。</summary>
    public string WorkspaceDirectory
    {
        get => FileExplorerHost.WorkspaceDirectory;
        set => FileExplorerHost.WorkspaceDirectory = value;
    }

    /// <summary>用户在文件树右键选择 "引用为附件" 时触发。</summary>
    public event Action<IReadOnlyList<string>>? AttachmentRequested;

    /// <summary>工作流附件回调（待重构后恢复）。</summary>
    public event Action<IReadOnlyList<string>>? WorkflowAttachmentRequested;

    /// <summary>群聊附件回调（待重构后恢复）。</summary>
    public event Action<IReadOnlyList<string>>? GroupChatAttachmentRequested;

    /// <summary>请求折叠左侧工作台面板。</summary>
    public event Action? WorkspacePanelCollapseRequested;

    // ──── ChatHistoryPanel 转发 API ────

    public event Action<string, string>? SessionSelected;
    public event Action? RequestNewSession;
    public event Action<string, string>? WorkTaskSelected;
    public event Action<string, string>? MeetingSelected;

    public string CurrentSessionId
    {
        get => ChatHistoryPanelHost.CurrentSessionId;
        set => ChatHistoryPanelHost.CurrentSessionId = value;
    }

    public void ReloadHistory()
    {
        ChatHistoryPanelHost.Reload();
        WorkTaskHistoryPanelHost.Reload();
        MeetingHistoryPanelHost.Reload();
    }

    public void AttachHistoryScrollHandler()
    {
        ChatHistoryPanelHost.AttachScrollHandler();
    }

    // ──── VM 绑定 ────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as LeftPanelVm;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            ApplyActiveTabClass(_vm.ActiveTabIndex);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftPanelVm.ActiveTabIndex) && _vm is not null)
            ApplyActiveTabClass(_vm.ActiveTabIndex);
    }

    private void ApplyActiveTabClass(int index)
    {
        var tab1Active = index == 0;
        Tab1Button.Classes.Set("left-tab-active", tab1Active);
        Tab2Button.Classes.Set("left-tab-active", !tab1Active);
    }

    private void OnTab1Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ActiveTabIndex = 0;
    }

    private void OnTab2Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ActiveTabIndex = 1;
    }
}
