using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P2-1：群聊 tab 聊天式输入框 ViewModel（取代 NewTaskDialog 表单）。
///
/// 职责：
/// - 维护输入框状态（InitialInput / SelectedAgents 多选 / HighRiskTools / Attachments）
/// - 维护任务运行状态（IsRunning / CurrentTaskId）切换发送/停止按钮 + 走马灯动画显示
/// - 调用 <see cref="TaskExecutionEngine.StartTaskAsync"/> 启动任务（SubMode 固定 "groupchat"）
/// - 调用 <see cref="TaskExecutionEngine.CancelTaskAsync"/> 停止运行中任务
///
/// 与 WorkflowInputVm 的差异（条款 11 + 群聊澄清 A）：
/// - 没有 SelectedManager / SelectedProvider / SelectedModel（用户澄清 A：群聊不显示厂商/模型，
///   Agent 未设置 DefaultProviderId / DefaultModelId 时由后端 AIAgentFactory 用全局默认兜底）
/// - 没有 SubMode（固定 "groupchat"）
/// - 没有 MaxSubAgents（不支持动态 Agent 创建）
/// - 有 SelectedAgents 多选（ObservableCollection&lt;AgentEntity&gt;），UI 渲染为单按钮 + Popup CheckListBox
/// - 启动校验：SelectedAgents.Count &gt;= 2
///
/// DI 生命周期：Singleton（与 WorkspaceTabVm 同源）。
///
/// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md §2.1（条款 11 + 群聊 A）。
/// </summary>
public sealed class GroupChatInputVm : IInputVm
{
    private readonly AgentService _agentService;
    private readonly TaskExecutionEngine _engine;
    private readonly SystemSettingsService _systemSettings;

    private string _initialInput = string.Empty;
    private string _workspaceId = string.Empty;
    private bool _isRunning;
    private string? _currentTaskId;
    private string? _validationError;

    /// <summary>SystemSettings key：上次选中的群聊智能体 ID 列表（逗号分隔，跨会话恢复）。</summary>
    private const string SelectedAgentsKey = "GroupChat.SelectedAgentIds";

    public GroupChatInputVm()
    {
        _agentService = App.Services.GetRequiredService<AgentService>();
        _engine = App.Services.GetRequiredService<TaskExecutionEngine>();
        _systemSettings = App.Services.GetRequiredService<SystemSettingsService>();

        // 监听多选变化 → 通知 UI 刷新 CanSubmit / 标签文本
        SelectedAgents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(SelectedAgentsLabel));
            OnPropertyChanged(nameof(HasSelectedAgents));
            PersistSelectedAgents();
        };

        LoadAvailableAgents();
        RestoreSelectedAgents();
    }

    // ──── 输入字段 ────

    /// <summary>用户输入的讨论话题。绑定到输入框 Text。</summary>
    public string InitialInput
    {
        get => _initialInput;
        set
        {
            if (SetField(ref _initialInput, value))
                OnPropertyChanged(nameof(CanSubmit));
        }
    }

    /// <summary>当前工作区 ID。由调用方（GroupChatDetailView 构造时）注入。</summary>
    public string WorkspaceId
    {
        get => _workspaceId;
        set => SetField(ref _workspaceId, value);
    }

    /// <summary>所有可用的智能体（绑定到 Popup 列表 ItemsSource）。</summary>
    public ObservableCollection<AgentEntity> AvailableAgents { get; } = [];

    /// <summary>
    /// 已选中的智能体（多选）。条款 11：单 Popup + CheckListBox。
    /// 持久化：每次变化写入 SystemSettings.GroupChat.SelectedAgentIds（逗号分隔）。
    /// </summary>
    public ObservableCollection<AgentEntity> SelectedAgents { get; } = [];

    /// <summary>是否已选中至少一个智能体（用于 UI 区分"未选择"和"已选 N 个"显示态）。</summary>
    public bool HasSelectedAgents => SelectedAgents.Count > 0;

    /// <summary>
    /// 智能体选择器按钮的标签文本（Bug 6 修复 2026-05-17，用户最终澄清）：
    /// - 0 个：「智能体」（默认值，上三角已暗示需要选择）
    /// - N 个：「已选 N 个」（紧凑）
    /// </summary>
    public string SelectedAgentsLabel => SelectedAgents.Count == 0
        ? "智能体"
        : $"已选 {SelectedAgents.Count} 个";

    /// <summary>校验提示（仅在 0 / 1 个时给提示，&gt;=2 不打扰）。</summary>
    public string SelectionHint => SelectedAgents.Count switch
    {
        0 => "请至少选择 2 个智能体参与讨论",
        1 => "已选 1 个智能体（至少需要 2 个才能开始讨论）",
        _ => string.Empty,
    };

    /// <summary>输入框占位文本（IInputVm 接口，群聊模式固定文本）。</summary>
    public string InputPlaceholderText => "输入讨论话题（Enter 发送，Shift+Enter 换行，# 引用文件）";

    /// <summary>
    /// 高风险工具屏蔽项（复用 <see cref="HighRiskToolItem"/>，决策 6-2-A 黑名单模式）。
    /// </summary>
    public ObservableCollection<HighRiskToolItem> HighRiskTools { get; } =
    [
        new HighRiskToolItem(
            "C# 脚本执行 (sys_csharp_script)",
            "sys_csharp_script",
            "可执行任意 C# 代码（包括读写文件、调用 API、修改注册表）；屏蔽后本任务不会执行 C# 脚本。"),
        new HighRiskToolItem(
            "进程启动 (sys_process)",
            "sys_process",
            "可启动本机任意可执行文件，可能导致进程注入或权限提升；屏蔽后本任务不会启动外部应用。"),
        new HighRiskToolItem(
            "窗口管理 (sys_window_manager)",
            "sys_window_manager",
            "可读取/操作其他应用的窗口与输入焦点；屏蔽后本任务不会跨进程操作窗口。"),
        new HighRiskToolItem(
            "Office 文档操作 (sys_office)",
            "sys_office",
            "可读写本机 Office 文档；屏蔽后本任务不会修改 .docx / .xlsx / .pptx 文件。"),
    ];

    /// <summary>附件列表（P2-1 仅文件，P3-2 加文件夹）。</summary>
    public ObservableCollection<AttachmentInfo> Attachments { get; } = [];

    // ──── 任务运行状态 ────

    /// <summary>任务是否运行中。运行中切换为停止按钮 + 输入框只读 + 走马灯动画（条款 5 + 12）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    /// <summary>!IsRunning 的便利属性（避免 axaml 反向 binding converter）。</summary>
    public bool IsIdle => !_isRunning;

    /// <summary>当前运行中的任务 ID。Stop 按钮点击时用于 CancelTaskAsync。</summary>
    public string? CurrentTaskId
    {
        get => _currentTaskId;
        private set => SetField(ref _currentTaskId, value);
    }

    /// <summary>表单校验错误（启动失败时显示）。</summary>
    public string? ValidationError
    {
        get => _validationError;
        private set => SetField(ref _validationError, value);
    }

    /// <summary>
    /// 是否可启动任务：
    /// - 不在运行中
    /// - InitialInput 非空
    /// - SelectedAgents.Count &gt;= 2
    /// </summary>
    public bool CanSubmit
    {
        get
        {
            if (_isRunning) return false;
            if (string.IsNullOrWhiteSpace(_initialInput)) return false;
            if (SelectedAgents.Count < 2) return false;
            return true;
        }
    }

    // ──── 加载 / 多选管理 / 持久化 ────

    /// <summary>从 AgentService 加载所有可用智能体。</summary>
    public void LoadAvailableAgents()
    {
        var agents = _agentService.GetAll();
        AvailableAgents.Clear();
        foreach (var a in agents) AvailableAgents.Add(a);
    }

    /// <summary>
    /// 切换某个 Agent 的勾选状态（条款 11：Popup CheckListBox 用）。
    /// 已选 → 移除；未选 → 添加。
    /// </summary>
    public bool ToggleAgent(string agentId)
    {
        if (string.IsNullOrEmpty(agentId)) return false;

        var existing = SelectedAgents.FirstOrDefault(a => a.Id == agentId);
        if (existing is not null)
        {
            SelectedAgents.Remove(existing);
            return false; // 现在是未选中状态
        }

        var agent = AvailableAgents.FirstOrDefault(a => a.Id == agentId);
        if (agent is null) return false;

        SelectedAgents.Add(agent);
        return true; // 现在是选中状态
    }

    /// <summary>判断某个 Agent 是否在 SelectedAgents 中（Popup CheckBox 初始勾选状态用）。</summary>
    public bool IsAgentSelected(string agentId)
        => !string.IsNullOrEmpty(agentId)
           && SelectedAgents.Any(a => a.Id == agentId);

    /// <summary>持久化已选 Agent ID 列表到 SystemSettings（逗号分隔）。</summary>
    private void PersistSelectedAgents()
    {
        var ids = SelectedAgents.Select(a => a.Id).Where(s => !string.IsNullOrEmpty(s));
        _systemSettings.SetValue(SelectedAgentsKey, string.Join(",", ids));
    }

    /// <summary>从 SystemSettings 恢复已选 Agent 列表（启动时调用一次）。</summary>
    private void RestoreSelectedAgents()
    {
        var raw = _systemSettings.GetValue<string>(SelectedAgentsKey, string.Empty);
        if (string.IsNullOrEmpty(raw)) return;

        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
        {
            var agent = AvailableAgents.FirstOrDefault(a => a.Id == id);
            if (agent is not null && !SelectedAgents.Any(a => a.Id == id))
                SelectedAgents.Add(agent);
        }
        // 触发器：构造期 CollectionChanged 会被订阅，恢复期 Add 会自动触发 PersistSelectedAgents
        // 但此时持久化是重写已有值，不影响（持久化只是写一次 raw 自身）
    }

    // ──── 启动 / 停止 ────

    /// <summary>启动 GroupChat 任务。成功返回 taskId 并把 IsRunning 切换到 true；失败设置 ValidationError 返回 null。</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        if (!CanSubmit)
        {
            ValidationError = SelectedAgents.Count < 2
                ? "请至少选择 2 个智能体参与讨论"
                : "请填写讨论话题";
            return null;
        }

        ValidationError = null;
        IsRunning = true;
        try
        {
            var userInput = _initialInput.Trim();

            // P4 过渡：使用 TaskExecutionEngine.StartTaskAsync 启动任务
            var taskId = await _engine.StartTaskAsync(
                userInput,
                _workspaceId,
                templateId: null,
                cancellationToken);
            CurrentTaskId = taskId;

            // 启动成功：清空输入框（用户可继续输入下一个任务）+ 保留已选 Agent（便于连续发起多个群聊）
            _initialInput = string.Empty;
            OnPropertyChanged(nameof(InitialInput));
            OnPropertyChanged(nameof(CanSubmit));

            // 启动成功后立即切换 Feed 区到新任务详情，并写入首条用户消息
            var workspaceVm = App.Services.GetRequiredService<WorkspaceTabVm>();
            await workspaceVm.ShowTaskAsync(taskId, title: null, initialUserInput: userInput);

            return taskId;
        }
        catch (Exception ex)
        {
            ValidationError = $"启动任务失败：{ex.Message}";
            IsRunning = false;
            CurrentTaskId = null;
            return null;
        }
    }

    /// <summary>停止当前运行中的任务。</summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        if (!_isRunning || string.IsNullOrEmpty(_currentTaskId)) return false;

        try
        {
            return await _engine.CancelTaskAsync(_currentTaskId, cancellationToken);
        }
        catch (Exception ex)
        {
            ValidationError = $"停止任务失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>由 View 层订阅 task.completed/failed/cancelled 事件后调用，复位运行状态。</summary>
    public void OnTaskFinished()
    {
        IsRunning = false;
        CurrentTaskId = null;
    }

    // ──── IInputVm 接口显式实现（SubmitAsync / CancelAsync） ────

    /// <inheritdoc cref="IInputVm.SubmitAsync"/>
    /// <remarks>群聊模式下委托到 <see cref="StartAsync"/>，忽略返回的 taskId。</remarks>
    public async Task SubmitAsync(CancellationToken cancellationToken = default)
        => await StartAsync(cancellationToken);

    /// <inheritdoc cref="IInputVm.CancelAsync"/>
    /// <remarks>群聊模式下委托到 <see cref="StopAsync"/>，忽略返回值。</remarks>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
        => await StopAsync(cancellationToken);

    // ──── INotifyPropertyChanged ────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
