using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.ViewModels.Workspace;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.Chat;

/// <summary>
/// 专家（Chat）模式输入框 ViewModel。实现 <see cref="IInputVm"/> 接口，
/// 供 InputAreaView 通过接口统一访问。
///
/// 职责：
/// - 维护输入框状态（InitialInput / Attachments / SelectedAgent / SelectedProvider / SelectedModel）
/// - 监听 <see cref="Events.OnAiStarted"/> / <see cref="Events.OnAiCompleted"/> 切换 IsRunning 状态
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
    private readonly ISubscriber _subscriber;

    private string _initialInput = string.Empty;
    private AgentEntity? _selectedAgent;
    private AiProviderEntity? _selectedProvider;
    private AiModelEntity? _selectedModel;
    private bool _isRunning;
    private string? _validationError;

    public ChatInputVm()
    {
        _chatService = App.Services.GetRequiredService<AiChatHostedService>();
        _agentService = App.Services.GetRequiredService<AgentService>();
        _providerService = App.Services.GetRequiredService<AiProviderService>();
        _modelService = App.Services.GetRequiredService<AiModelService>();
        _subscriber = App.Services.GetRequiredService<ISubscriber>();

        LoadAvailableAgents();
        LoadAvailableProviders();
        RestoreDefaultSelections();
        SubscribeAiEvents();
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

    // ──── IInputVm 公共属性 ────

    /// <summary>任务是否运行中（由 EventHub AI 事件驱动）。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    /// <summary>!IsRunning 便利属性（避免 AXAML Converter）。</summary>
    public bool IsIdle => !_isRunning;

    /// <summary>是否可发送：不在发送中 且 输入框非空。</summary>
    public bool CanSubmit => !_isRunning && !string.IsNullOrWhiteSpace(_initialInput);

    /// <summary>表单校验错误（发送失败时显示）。</summary>
    public string? ValidationError
    {
        get => _validationError;
        private set => SetField(ref _validationError, value);
    }

    /// <summary>输入框占位文本（Chat 模式固定，不随状态变化）。</summary>
    public string InputPlaceholderText
        => "发消息给助手（Enter 发送，Shift+Enter 换行，# 引用文件，@ 提及智能体）";

    /// <summary>附件列表（由 InputAreaView 管理添加/删除，发送时传给 AiChatHostedService）。</summary>
    public ObservableCollection<AttachmentInfo> Attachments { get; } = [];

    /// <summary>Chat 模式无高风险工具屏蔽，返回空集合（满足 IInputVm 接口）。</summary>
    public ObservableCollection<HighRiskToolItem> HighRiskTools { get; } = [];

    // ──── 加载 / 持久化 ────

    /// <summary>从 AgentService 加载所有可用智能体。</summary>
    public void LoadAvailableAgents()
    {
        var agents = _agentService.GetAll();
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

    /// <summary>恢复默认选择（回退顺序：IsDefault → 第一项）。</summary>
    private void RestoreDefaultSelections()
    {
        // Agent
        _selectedAgent = AvailableAgents.FirstOrDefault(a => a.IsDefault)
                       ?? AvailableAgents.FirstOrDefault();

        // Provider（优先 Agent.DefaultProviderId）
        if (!string.IsNullOrEmpty(_selectedAgent?.DefaultProviderId))
            _selectedProvider = AvailableProviders.FirstOrDefault(p => p.Id == _selectedAgent.DefaultProviderId);
        _selectedProvider ??= AvailableProviders.FirstOrDefault(p => p.IsDefault)
                            ?? AvailableProviders.FirstOrDefault();

        // Models + Model（优先 Agent.DefaultModelId）
        if (_selectedProvider is not null)
        {
            var models = _modelService.GetByProviderId(_selectedProvider.Id);
            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);

            if (!string.IsNullOrEmpty(_selectedAgent?.DefaultModelId))
                _selectedModel = AvailableModels.FirstOrDefault(m => m.Id == _selectedAgent.DefaultModelId);
            _selectedModel ??= AvailableModels.FirstOrDefault(m => m.IsDefault)
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
    /// Agent 切换时根据 Agent.DefaultProviderId / DefaultModelId 联动切换厂商/模型。
    /// 没有设置的字段不覆盖用户选择。
    /// </summary>
    private void SyncProviderModelFromAgent(AgentEntity agent)
    {
        if (!string.IsNullOrEmpty(agent.DefaultProviderId) &&
            agent.DefaultProviderId != _selectedProvider?.Id)
        {
            var newProvider = AvailableProviders.FirstOrDefault(p => p.Id == agent.DefaultProviderId);
            if (newProvider is not null)
            {
                _selectedProvider = newProvider;
                LoadModelsForProvider(newProvider.Id);
                OnPropertyChanged(nameof(SelectedProvider));
                OnPropertyChanged(nameof(SelectedProviderName));
            }
        }

        if (!string.IsNullOrEmpty(agent.DefaultModelId) &&
            agent.DefaultModelId != _selectedModel?.Id)
        {
            var newModel = AvailableModels.FirstOrDefault(m => m.Id == agent.DefaultModelId);
            if (newModel is not null)
            {
                _selectedModel = newModel;
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SelectedModelName));
            }
        }
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
        if (string.IsNullOrWhiteSpace(text) && Attachments.Count == 0) return;

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
        List<AttachmentInfo>? attachments = Attachments.Count > 0
            ? [.. Attachments]
            : null;

        // 清空输入框 + 附件列表（UI 响应即时）
        _initialInput = string.Empty;
        OnPropertyChanged(nameof(InitialInput));
        OnPropertyChanged(nameof(CanSubmit));
        Attachments.Clear();

        try
        {
            await _chatService.SendMessageAsync(
                text ?? string.Empty,
                cancellationToken,
                attachments);
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
    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _chatService.CancelCurrentTask();
        }
        catch { /* 引擎未启动时忽略 */ }
        return Task.CompletedTask;
    }

    // ──── EventHub 订阅 ────

    /// <summary>
    /// 订阅 AI 推理开始/完成事件，驱动 IsRunning 状态切换（走马灯 + Send/Stop 按钮）。
    /// </summary>
    private void SubscribeAiEvents()
    {
        // OnAiStarted / OnAiCompleted 是 VoiceSignalEvent，
        // 订阅泛型必须用 VoiceSignalArgs，否则类型不匹配，事件永远收不到
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiStarted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() => IsRunning = true);
            return Task.FromResult(false);
        });

        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnAiCompleted, (_, _) =>
        {
            Dispatcher.UIThread.Post(() => IsRunning = false);
            return Task.FromResult(false);
        });
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
