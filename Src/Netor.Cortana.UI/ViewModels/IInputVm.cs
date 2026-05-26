using System.Collections.ObjectModel;
using System.ComponentModel;

using Netor.Cortana.Entitys;
using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.ViewModels;

/// <summary>
/// 三种输入模式（专家/工作流/群聊）共享的输入框 ViewModel 抽象接口。
///
/// <see cref="InputAreaView"/> 通过此接口与具体 VM 解耦，
/// 模式专属属性（SubMode / SelectedAgents 等）由 InputAreaView 通过 as 转型访问。
///
/// 实现类：
/// <list type="bullet">
/// <item><see cref="Workspace.WorkflowInputVm"/> — 工作模式</item>
/// <item><see cref="Workspace.GroupChatInputVm"/> — 群聊模式</item>
/// <item><see cref="Chat.ChatInputVm"/> — 专家模式</item>
/// </list>
/// </summary>
public interface IInputVm : INotifyPropertyChanged
{
    /// <summary>用户输入文本，双向绑定到 TextBox.Text。</summary>
    string InitialInput { get; set; }

    /// <summary>任务是否运行中（控制 Send/Stop 按钮显隐 + 走马灯动画）。</summary>
    bool IsRunning { get; }

    /// <summary>!IsRunning 的便利属性（避免 AXAML 反向 Converter）。</summary>
    bool IsIdle { get; }

    /// <summary>是否可提交（按钮 IsEnabled 绑定）。</summary>
    bool CanSubmit { get; }

    /// <summary>表单校验错误（显示在输入框上方）。</summary>
    string? ValidationError { get; }

    /// <summary>输入框 PlaceholderText（根据模式/状态动态切换）。</summary>
    string InputPlaceholderText { get; }

    /// <summary>已添加的附件列表。</summary>
    ObservableCollection<AttachmentInfo> Attachments { get; }

    /// <summary>高风险工具屏蔽项（工具 Popup 绑定）。</summary>
    ObservableCollection<HighRiskToolItem> HighRiskTools { get; }

    /// <summary>
    /// 提交（发送/启动任务）。
    /// 专家模式：发送聊天消息；工作/群聊模式：启动任务。
    /// </summary>
    Task SubmitAsync(CancellationToken cancellationToken = default);

    /// <summary>取消当前运行中的任务/对话。</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
}
