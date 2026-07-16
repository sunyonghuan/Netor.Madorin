using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI;
using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.ViewModels.Common;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.ExpertMode;

/// <summary>
/// 专家（Chat）模式输入框 ViewModel。实现 <see cref="IInputVm"/> 接口，
/// 供 InputAreaView 通过接口统一访问。
///
/// 职责：
/// - 维护输入框状态（InitialInput / Attachments / SelectedAgent / SelectedProvider / SelectedModel）
/// - 监听 <see cref="Events.OnConversationTurnStarted"/> / <see cref="Events.OnConversationTurnCompleted"/> 切换 IsRunning 状态
/// - 提供 <see cref="SubmitAsync"/> 调用 <see cref="AiChatHostedService.SendMessageAsync"/>
/// - 提供 <see cref="CancelAsync"/> 调用 <see cref="AiChatHostedService.CancelCurrentTask"/>
///
/// 注意：
/// - 附件预览渲染（气泡 / 标签）和 #文件引用、@智能体 解析由 InputAreaView code-behind 负责；
///   VM 仅持有最终要发送的 Attachments 列表（供 View 读取后调用 SubmitAsync）。
/// - Chat 模式无"高风险工具屏蔽"功能，<see cref="HighRiskTools"/> 返回空集合。
///
/// DI 生命周期：Singleton（由 App.ConfigureServices 注册）。
/// </summary>
public sealed class ChatInputVm : IInputVm
{
    private readonly AiChatHostedService _chatService;
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly ChatOrchestrationDiagnosticsService _orchestrationDiagnosticsService;
    private readonly ISubscriber _subscriber;

    private string _initialInput = string.Empty;
    private AgentEntity? _selectedAgent;
    private AiProviderEntity? _selectedProvider;
    private AiModelEntity? _selectedModel;
    private bool _isRunning;
    private string? _validationError;
    private ChatSubmitMode _submitMode = ChatSubmitMode.Chat;
    private string _orchestrationDiagnosticsText = "编排：默认单智能体";
    private bool _hasOrchestrationWarnings;
    private bool _showOrchestrationDiagnostics;
    private bool _isOrchestrationDiagnosticsExpanded = true;
    private OrchestrationDiagnosticsFilter _orchestrationDiagnosticsFilter = OrchestrationDiagnosticsFilter.All;
    private readonly List<ChatTurnDiagnosticsItemVm> _recentTurnDiagnosticsSource = [];

    public ChatInputVm()
    {
        _chatService = App.Services.GetRequiredService<AiChatHostedService>();
        _agentService = App.Services.GetRequiredService<AgentService>();
        _providerService = App.Services.GetRequiredService<AiProviderService>();
        _modelService = App.Services.GetRequiredService<AiModelService>();
        _orchestrationDiagnosticsService = App.Services.GetRequiredService<ChatOrchestrationDiagnosticsService>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();

        LoadAvailableAgents();
        LoadAvailableProviders();
        RestoreDefaultSelections();
        SubscribeAiEvents();
        RefreshOrchestrationDiagnostics();
    }

    // ──── 输入字段 ────

    /// <summary>用户输入文本，双向绑定到 TextBox.Text。</summary>
    public string InitialInput
    {
        get => _initialInput;
        set
        {
            if (SetField(ref _initialInput, value))
                OnPropertyChanged(nameof(CanSubmit));
        }
    }

    // ──── Agent / Provider / Model 选择 ────

    /// <summary>可选的智能体列表（绑定到 Agent 选择器 Popup）。</summary>
    public ObservableCollection<AgentEntity> AvailableAgents { get; } = [];

    /// <summary>可选的厂商列表（绑定到厂商选择器 Popup）。</summary>
    public ObservableCollection<AiProviderEntity> AvailableProviders { get; } = [];

    /// <summary>可选的模型列表（当前 Provider 下）。</summary>
    public ObservableCollection<AiModelEntity> AvailableModels { get; } = [];

    /// <summary>当前选中的智能体。</summary>
    public AgentEntity? SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (SetField(ref _selectedAgent, value))
            {
                OnPropertyChanged(nameof(SelectedAgentName));
                if (value is not null)
                    SyncProviderModelFromAgent(value);
            }
        }
    }

    /// <summary>智能体显示名（工具栏标签用）。</summary>
    public string SelectedAgentName => _selectedAgent?.Name ?? "智能体";

    /// <summary>当前选中的厂商。</summary>
    public AiProviderEntity? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetField(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(SelectedProviderName));
                if (value is not null)
                    LoadModelsForProvider(value.Id);
            }
        }
    }

    /// <summary>厂商显示名（工具栏标签用）。</summary>
    public string SelectedProviderName => _selectedProvider?.Name ?? "厂商";

    /// <summary>当前选中的模型。</summary>
    public AiModelEntity? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetField(ref _selectedModel, value))
                OnPropertyChanged(nameof(SelectedModelName));
        }
    }

    /// <summary>模型显示名（工具栏标签用）。优先 DisplayName，再 Name。</summary>
    public string SelectedModelName
        => _selectedModel is null
            ? "模型"
            : !string.IsNullOrWhiteSpace(_selectedModel.DisplayName)
                ? _selectedModel.DisplayName!
                : _selectedModel.Name;

    public ChatSubmitMode SubmitMode
    {
        get => _submitMode;
        set
        {
            if (SetField(ref _submitMode, value))
            {
                OnPropertyChanged(nameof(IsChatSubmitMode));
                OnPropertyChanged(nameof(IsImageSubmitMode));
                OnPropertyChanged(nameof(IsVideoSubmitMode));
                OnPropertyChanged(nameof(InputPlaceholderText));
            }
        }
    }

    public bool IsChatSubmitMode => _submitMode == ChatSubmitMode.Chat;

    public bool IsImageSubmitMode => _submitMode == ChatSubmitMode.Image;

    public bool IsVideoSubmitMode => _submitMode == ChatSubmitMode.Video;

    // ──── IInputVm 公共属性 ────

    /// <summary>任务是否运行中（由 Conversation turn 生命周期事件驱动）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(IsTaskActive));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    /// <summary>!IsRunning 便利属性（避免 AXAML Converter）。</summary>
    public bool IsIdle => !_isRunning;

    /// <summary>专家模式下 IsTaskActive 等同于 IsRunning（无多阶段任务概念）。</summary>
    public bool IsTaskActive => _isRunning;

    /// <summary>是否可发送：不在发送中 且 输入框非空。</summary>
    public bool CanSubmit => !_isRunning
        && (_submitMode == ChatSubmitMode.Chat
            ? !string.IsNullOrWhiteSpace(_initialInput) || Attachments.Count > 0
            : !string.IsNullOrWhiteSpace(_initialInput));

    /// <summary>表单校验错误（发送失败时显示）。</summary>
    public string? ValidationError
    {
        get => _validationError;
        private set => SetField(ref _validationError, value);
    }

    /// <summary>输入框占位文本（Chat 模式固定，不随状态变化）。</summary>
    public string InputPlaceholderText
        => _submitMode switch
        {
            ChatSubmitMode.Image => "输入图片提示词（Enter 生成，Shift+Enter 换行）",
            ChatSubmitMode.Video => "输入视频提示词（Enter 生成，Shift+Enter 换行）",
            _ => "发消息给助手（Enter 发送，Shift+Enter 换行，# 引用文件，@ 提及智能体）"
        };

    /// <summary>开发态编排诊断是否可见。</summary>
    public bool ShowOrchestrationDiagnostics
    {
        get => _showOrchestrationDiagnostics;
        private set => SetField(ref _showOrchestrationDiagnostics, value);
    }

    /// <summary>开发态编排诊断文案。</summary>
    public string OrchestrationDiagnosticsText
    {
        get => _orchestrationDiagnosticsText;
        private set => SetField(ref _orchestrationDiagnosticsText, value);
    }

    /// <summary>当前诊断是否包含 warning。</summary>
    public bool HasOrchestrationWarnings
    {
        get => _hasOrchestrationWarnings;
        private set => SetField(ref _hasOrchestrationWarnings, value);
    }

    /// <summary>开发态编排观察面板是否展开。</summary>
    public bool IsOrchestrationDiagnosticsExpanded
    {
        get => _isOrchestrationDiagnosticsExpanded;
        set
        {
            if (SetField(ref _isOrchestrationDiagnosticsExpanded, value))
            {
                OnPropertyChanged(nameof(OrchestrationDiagnosticsToggleText));
            }
        }
    }

    /// <summary>观察面板折叠/展开按钮文案。</summary>
    public string OrchestrationDiagnosticsToggleText
        => _isOrchestrationDiagnosticsExpanded ? "收起" : "展开";

    /// <summary>当前观察面板过滤模式。</summary>
    public OrchestrationDiagnosticsFilter OrchestrationDiagnosticsFilter
    {
        get => _orchestrationDiagnosticsFilter;
        set
        {
            if (SetField(ref _orchestrationDiagnosticsFilter, value))
            {
                OnPropertyChanged(nameof(AllOrchestrationDiagnosticsFilterText));
                OnPropertyChanged(nameof(WarningOrchestrationDiagnosticsFilterText));
                OnPropertyChanged(nameof(FailedOrchestrationDiagnosticsFilterText));
                OnPropertyChanged(nameof(CanSwitchToAllOrchestrationDiagnosticsFilter));
                OnPropertyChanged(nameof(CanSwitchToWarningOrchestrationDiagnosticsFilter));
                OnPropertyChanged(nameof(CanSwitchToFailedOrchestrationDiagnosticsFilter));
                OnPropertyChanged(nameof(OrchestrationDiagnosticsEmptyText));
                OnPropertyChanged(nameof(OrchestrationDiagnosticsFilterSummaryText));
                ApplyRecentTurnDiagnosticsFilter();
            }
        }
    }

    /// <summary>全部过滤按钮文案。</summary>
    public string AllOrchestrationDiagnosticsFilterText
        => _orchestrationDiagnosticsFilter == OrchestrationDiagnosticsFilter.All
            ? $"全部 ({AllOrchestrationDiagnosticsCount}) ✓"
            : $"全部 ({AllOrchestrationDiagnosticsCount})";

    /// <summary>仅 warning 过滤按钮文案。</summary>
    public string WarningOrchestrationDiagnosticsFilterText
        => _orchestrationDiagnosticsFilter == OrchestrationDiagnosticsFilter.WarningOnly
            ? $"仅 Warning ({WarningOrchestrationDiagnosticsCount}) ✓"
            : $"仅 Warning ({WarningOrchestrationDiagnosticsCount})";

    /// <summary>仅失败过滤按钮文案。</summary>
    public string FailedOrchestrationDiagnosticsFilterText
        => _orchestrationDiagnosticsFilter == OrchestrationDiagnosticsFilter.FailedOnly
            ? $"仅失败 ({FailedOrchestrationDiagnosticsCount}) ✓"
            : $"仅失败 ({FailedOrchestrationDiagnosticsCount})";

    /// <summary>全部筛选下的匹配数量。</summary>
    public int AllOrchestrationDiagnosticsCount => _recentTurnDiagnosticsSource.Count;

    /// <summary>仅 warning 筛选下的匹配数量。</summary>
    public int WarningOrchestrationDiagnosticsCount
        => _recentTurnDiagnosticsSource.Count(static item => item.HasWarnings);

    /// <summary>仅失败筛选下的匹配数量。</summary>
    public int FailedOrchestrationDiagnosticsCount
        => _recentTurnDiagnosticsSource.Count(static item => item.IsFailed);

    /// <summary>是否可切到全部过滤。</summary>
    public bool CanSwitchToAllOrchestrationDiagnosticsFilter
        => _orchestrationDiagnosticsFilter != OrchestrationDiagnosticsFilter.All;

    /// <summary>是否可切到 warning 过滤。</summary>
    public bool CanSwitchToWarningOrchestrationDiagnosticsFilter
        => _orchestrationDiagnosticsFilter != OrchestrationDiagnosticsFilter.WarningOnly;

    /// <summary>是否可切到失败过滤。</summary>
    public bool CanSwitchToFailedOrchestrationDiagnosticsFilter
        => _orchestrationDiagnosticsFilter != OrchestrationDiagnosticsFilter.FailedOnly;

    /// <summary>当前会话最近几轮 turn 的编排诊断摘要。</summary>
    public ObservableCollection<ChatTurnDiagnosticsItemVm> RecentTurnDiagnostics { get; } = [];

    /// <summary>过滤后是否没有可展示的 turn 诊断。</summary>
    public bool ShowOrchestrationDiagnosticsEmptyState
        => _recentTurnDiagnosticsSource.Count > 0 && RecentTurnDiagnostics.Count == 0;

    /// <summary>过滤空结果提示文案。</summary>
    public string OrchestrationDiagnosticsEmptyText
        => _orchestrationDiagnosticsFilter switch
        {
            OrchestrationDiagnosticsFilter.WarningOnly => "当前会话最近几轮没有包含 warning 的编排 turn。",
            OrchestrationDiagnosticsFilter.FailedOnly => "当前会话最近几轮没有失败的编排 turn。",
            _ => "当前会话最近几轮暂无可展示的编排 turn。"
        };

    /// <summary>当前过滤摘要文案。</summary>
    public string OrchestrationDiagnosticsFilterSummaryText
        => $"最近 {AllOrchestrationDiagnosticsCount} 轮，当前筛选命中 {RecentTurnDiagnostics.Count} 轮";

    /// <summary>附件列表（由 InputAreaView 管理添加/删除，发送时传给 AiChatHostedService）。</summary>
    public ObservableCollection<AttachmentInfo> Attachments { get; } = [];

    /// <summary>Chat 模式无高风险工具屏蔽，返回空集合（满足 IInputVm 接口）。</summary>

    // ──── 加载 / 持久化 ────

    /// <summary>从 AgentService 加载所有可用智能体。</summary>
    public void LoadAvailableAgents()
    {
        var agents = _agentService.GetSelectable();
        AvailableAgents.Clear();
        foreach (var a in agents) AvailableAgents.Add(a);
    }

    /// <summary>从 AiProviderService 加载所有可用厂商。</summary>
    public void LoadAvailableProviders()
    {
        var providers = _providerService.GetAll();
        AvailableProviders.Clear();
        foreach (var p in providers) AvailableProviders.Add(p);
    }

    /// <summary>根据 ProviderId 加载该厂商下的模型列表。</summary>
    public void LoadModelsForProvider(string providerId)
    {
        var models = _modelService.GetByProviderId(providerId);
        AvailableModels.Clear();
        foreach (var m in models) AvailableModels.Add(m);

        // 若当前 SelectedModel 不在新列表中，重置为第一项
        if (_selectedModel is not null && !AvailableModels.Any(m => m.Id == _selectedModel.Id))
        {
            _selectedModel = AvailableModels.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedModelName));
        }
    }

    /// <summary>恢复默认选择（回退顺序：Agent.DefaultName → 第一项）。</summary>
    private void RestoreDefaultSelections()
    {
        var defaultAgent = _agentService.GetDefaultOrFirst();
        _selectedAgent = AvailableAgents.FirstOrDefault(a => a.Id == defaultAgent?.Id)
                       ?? AvailableAgents.FirstOrDefault();

        _selectedProvider = AvailableProviders.FirstOrDefault(p => p.IsDefault)
                            ?? AvailableProviders.FirstOrDefault();

        if (_selectedProvider is not null)
        {
            var models = _modelService.GetByProviderId(_selectedProvider.Id);
            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);

            _selectedModel = AvailableModels.FirstOrDefault(m => m.IsDefault)
                              ?? AvailableModels.FirstOrDefault();
        }

        OnPropertyChanged(nameof(SelectedAgent));
        OnPropertyChanged(nameof(SelectedAgentName));
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelName));
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary>
    /// 文件版 Agent 不再携带厂商/模型默认值，切换 Agent 时保留当前模型选择。
    /// </summary>
    private void SyncProviderModelFromAgent(AgentEntity agent)
    {
    }

    // ──── 发送 / 取消 ────

    /// <summary>
    /// 发送消息（IInputVm.SubmitAsync）。
    /// 将 <see cref="InitialInput"/> 和 <see cref="Attachments"/> 转发给 AiChatHostedService。
    /// 附件中的 #文件引用 路径替换由 InputAreaView code-behind 在调用前完成。
    /// </summary>
    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        var text = _initialInput?.Trim();
        if (_submitMode != ChatSubmitMode.Chat && string.IsNullOrWhiteSpace(text)) return;
        if (_submitMode == ChatSubmitMode.Chat && string.IsNullOrWhiteSpace(text) && Attachments.Count == 0) return;

        ValidationError = null;

        // 发送前把 UI 选择的 Provider/Agent/Model 同步到 AiChatHostedService
        // AiChatHostedService 用的是自己 LoadDefaults() 加载的配置，两边必须显式同步
        if (_selectedAgent is not null)
            _chatService.ChangeAgent(_selectedAgent.Id);
        if (_selectedProvider is not null)
            _chatService.ChangeProvider(_selectedProvider.Id);
        if (_selectedModel is not null)
            _chatService.ChangeModel(_selectedModel.Id);

        // 收集附件并清空（发送前快照）
        List<AttachmentInfo>? attachments = _submitMode == ChatSubmitMode.Chat && Attachments.Count > 0
            ? [.. Attachments]
            : null;

        // 清空输入框 + 附件列表（UI 响应即时）
        _initialInput = string.Empty;
        OnPropertyChanged(nameof(InitialInput));
        OnPropertyChanged(nameof(CanSubmit));
        Attachments.Clear();

        try
        {
            if (_submitMode == ChatSubmitMode.Image)
            {
                await _chatService.GenerateImageAsync(text ?? string.Empty, cancellationToken);
            }
            else if (_submitMode == ChatSubmitMode.Video)
            {
                await _chatService.GenerateVideoAsync(text ?? string.Empty, cancellationToken);
            }
            else
            {
                await _chatService.SendMessageAsync(
                    text ?? string.Empty,
                    cancellationToken,
                    attachments);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消，静默忽略
        }
        catch (Exception ex)
        {
            ValidationError = $"发送失败：{ex.Message}";
        }
    }

    /// <summary>取消当前 AI 对话（IInputVm.CancelAsync）。</summary>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        // 乐观即时恢复：UI 立刻切回空闲态，无需等待后台确认。
        // OnConversationTurnCompleted 事件仍是最终真相，幂等不冲突。
        IsRunning = false;

        try
        {
            await _chatService.CancelCurrentTaskAsync(cancellationToken);
        }
        catch { /* 引擎未启动或取消清理失败时忽略 */ }
    }

    // ──── EventHub 订阅 ────

    /// <summary>
    /// 订阅 Conversation turn 开始/完成事件，驱动 IsRunning 状态切换（走马灯 + Send/Stop 按钮）。
    /// </summary>
    private void SubscribeAiEvents()
    {
        _subscriber.Subscribe<ConversationTurnStartedArgs>(Events.OnConversationTurnStarted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsRunning = true;
                RefreshOrchestrationDiagnostics();
            });
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<ConversationTurnCompletedArgs>(Events.OnConversationTurnCompleted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsRunning = false;
                RefreshOrchestrationDiagnostics();
            });
            return Task.FromResult(false);
        });
    }

    /// <summary>
    /// 供外部在切换会话后主动刷新开发态编排诊断。
    /// </summary>
    public void RefreshDiagnosticsForCurrentSession()
    {
        RefreshOrchestrationDiagnostics();
    }

    /// <summary>
    /// 导出当前会话开发态编排诊断文本，供复制到剪贴板。
    /// </summary>
    public string ExportOrchestrationDiagnosticsText()
    {
        if (!IsDevelopmentBuild)
        {
            return string.Empty;
        }

#if DEBUG
        var lines = new List<string>
        {
            "专家模式编排诊断",
            OrchestrationDiagnosticsText,
            $"过滤：{GetOrchestrationDiagnosticsFilterLabel()}"
        };

        foreach (var item in RecentTurnDiagnostics)
        {
            lines.Add($"- {item.TimeText} {item.SummaryText}");
            lines.Add($"  {item.AgentText}");
            if (!string.IsNullOrWhiteSpace(item.WarningText))
            {
                lines.Add($"  {item.WarningText}");
            }
        }

        return string.Join(Environment.NewLine, lines);
#else
        return string.Empty;
#endif
    }

    /// <summary>
    /// 导出单条 turn 编排诊断文本，供复制到剪贴板。
    /// </summary>
    public string ExportTurnDiagnosticsText(ChatTurnDiagnosticsItemVm? item)
    {
        if (!IsDevelopmentBuild)
        {
            return string.Empty;
        }

#if DEBUG
        if (item is null)
        {
            return string.Empty;
        }

        var lines = new List<string>
        {
            "专家模式单轮编排诊断",
            OrchestrationDiagnosticsText,
            $"过滤：{GetOrchestrationDiagnosticsFilterLabel()}",
            OrchestrationDiagnosticsFilterSummaryText,
            $"TurnId: {item.TurnId}",
            $"时间: {item.TimeText}",
            item.SummaryText,
            item.AgentText
        };

        if (!string.IsNullOrWhiteSpace(item.WarningText))
        {
            lines.Add(item.WarningText);
        }

        return string.Join(Environment.NewLine, lines);
#else
        return string.Empty;
#endif
    }

    /// <summary>
    /// 刷新开发态编排诊断摘要。
    /// </summary>
    private void RefreshOrchestrationDiagnostics()
    {
        if (!IsDevelopmentBuild)
        {
            ShowOrchestrationDiagnostics = false;
            OrchestrationDiagnosticsText = "编排：默认单智能体";
            HasOrchestrationWarnings = false;
            _recentTurnDiagnosticsSource.Clear();
            RecentTurnDiagnostics.Clear();
            return;
        }

#if DEBUG
        var snapshot = _orchestrationDiagnosticsService.GetSnapshot(_chatService.CurrentSessionId)
            ?? _orchestrationDiagnosticsService.GetLatestSnapshot();
        var recentSnapshots = _orchestrationDiagnosticsService.GetRecentSnapshots(_chatService.CurrentSessionId);
        ShowOrchestrationDiagnostics = snapshot is not null;
        if (snapshot is null)
        {
            OrchestrationDiagnosticsText = "编排：默认单智能体";
            HasOrchestrationWarnings = false;
            _recentTurnDiagnosticsSource.Clear();
            RecentTurnDiagnostics.Clear();
            return;
        }

        var modeText = snapshot.Mode == AgentOrchestrationMode.None
            ? "默认单智能体"
            : snapshot.Mode.ToString();
        var agentText = snapshot.AgentIds.Count == 0
            ? "当前 Agent"
            : string.Join(", ", snapshot.AgentIds);
        var statusText = snapshot.IsCompleted
            ? snapshot.Status switch
            {
                ConversationTurnStatus.Cancelled => "已取消",
                ConversationTurnStatus.Failed => "已失败",
                _ => "已完成"
            }
            : "进行中";
        var warningText = snapshot.Warnings.Count == 0
            ? string.Empty
            : $" · Warnings: {string.Join(" | ", snapshot.Warnings)}";

        OrchestrationDiagnosticsText = $"编排：{modeText} · Agents: {agentText} · 状态：{statusText}{warningText}";
        HasOrchestrationWarnings = snapshot.Warnings.Count > 0;
        SyncRecentTurnDiagnostics(recentSnapshots);
#else
        ShowOrchestrationDiagnostics = false;
        OrchestrationDiagnosticsText = "编排：默认单智能体";
        HasOrchestrationWarnings = false;
        _recentTurnDiagnosticsSource.Clear();
        RecentTurnDiagnostics.Clear();
#endif
    }

    private static bool IsDevelopmentBuild
        => string.Equals(
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Debug",
            StringComparison.OrdinalIgnoreCase);

    private void SyncRecentTurnDiagnostics(IReadOnlyList<ChatOrchestrationDiagnosticsSnapshot> snapshots)
    {
        _recentTurnDiagnosticsSource.Clear();
        foreach (var snapshot in snapshots)
        {
            var modeText = snapshot.Mode == AgentOrchestrationMode.None
                ? "默认单智能体"
                : snapshot.Mode.ToString();
            var agentText = snapshot.AgentIds.Count == 0
                ? "当前 Agent"
                : string.Join(", ", snapshot.AgentIds);
            var statusText = snapshot.IsCompleted
                ? snapshot.Status switch
                {
                    ConversationTurnStatus.Cancelled => "已取消",
                    ConversationTurnStatus.Failed => "已失败",
                    _ => "已完成"
                }
                : "进行中";

            _recentTurnDiagnosticsSource.Add(new ChatTurnDiagnosticsItemVm(
                snapshot.TurnId,
                BuildTurnIdShortText(snapshot.TurnId),
                $"TurnId: {snapshot.TurnId}",
                snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm:ss"),
                $"[{statusText}] {modeText}",
                $"Agents: {agentText}",
                snapshot.Warnings.Count == 0 ? null : $"Warnings: {string.Join(" | ", snapshot.Warnings)}",
                snapshot.Warnings.Count > 0,
                snapshot.Status == ConversationTurnStatus.Failed));
        }

        ApplyRecentTurnDiagnosticsFilter();
    }

    private void ApplyRecentTurnDiagnosticsFilter()
    {
        RecentTurnDiagnostics.Clear();
        OnPropertyChanged(nameof(AllOrchestrationDiagnosticsFilterText));
        OnPropertyChanged(nameof(WarningOrchestrationDiagnosticsFilterText));
        OnPropertyChanged(nameof(FailedOrchestrationDiagnosticsFilterText));
        OnPropertyChanged(nameof(OrchestrationDiagnosticsFilterSummaryText));

        IEnumerable<ChatTurnDiagnosticsItemVm> items = _recentTurnDiagnosticsSource;
        items = _orchestrationDiagnosticsFilter switch
        {
            OrchestrationDiagnosticsFilter.WarningOnly => items.Where(static item => item.HasWarnings),
            OrchestrationDiagnosticsFilter.FailedOnly => items.Where(static item => item.IsFailed),
            _ => items
        };

        foreach (var item in items)
        {
            RecentTurnDiagnostics.Add(item);
        }

        OnPropertyChanged(nameof(OrchestrationDiagnosticsFilterSummaryText));
        OnPropertyChanged(nameof(ShowOrchestrationDiagnosticsEmptyState));
    }

    private string GetOrchestrationDiagnosticsFilterLabel()
    {
        return _orchestrationDiagnosticsFilter switch
        {
            OrchestrationDiagnosticsFilter.WarningOnly => "仅 Warning",
            OrchestrationDiagnosticsFilter.FailedOnly => "仅失败",
            _ => "全部"
        };
    }

    private static string BuildTurnIdShortText(string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return "TurnId: 未知";
        }

        return turnId.Length <= 8
            ? $"TurnId: {turnId}"
            : $"TurnId: {turnId[..8]}...";
    }

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

public sealed record ChatTurnDiagnosticsItemVm(
    string TurnId,
    string TurnIdShortText,
    string TurnIdTooltipText,
    string TimeText,
    string SummaryText,
    string AgentText,
    string? WarningText,
    bool HasWarnings,
    bool IsFailed);

public enum OrchestrationDiagnosticsFilter
{
    All,
    WarningOnly,
    FailedOnly,
}

public enum ChatSubmitMode
{
    Chat,
    Image,
    Video,
}
