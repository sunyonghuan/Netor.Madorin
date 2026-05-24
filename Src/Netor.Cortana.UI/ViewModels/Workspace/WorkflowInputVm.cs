using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P2-1：工作流 tab 聊天式输入框 ViewModel（取代 NewTaskDialog 表单）。
///
/// 职责：
/// - 维护输入框状态（InitialInput / SelectedManager / SelectedProvider / SelectedModel /
///   SubMode / MaxSubAgents / HighRiskTools / Attachments）
/// - 维护任务运行状态（IsRunning / CurrentTaskId）切换发送/停止按钮显示
/// - 调用 <see cref="TaskExecutionEngine.StartTaskAsync"/> 启动任务
/// - 调用 <see cref="TaskExecutionEngine.CancelTaskAsync"/> 停止运行中任务
///
/// 设计要点（条款 1-12，详见用户 2026-05-17 反馈）：
/// - 与 <see cref="NewTaskDialogVm"/> 不同：不显式 SelectedMembers（决策 TP-3 / TP-4 由 Manager 自主创建子智能体）
/// - 默认 Manager / Provider / Model 来自 SystemSettings（每次切换后持久化，决策 TP-2 B）
/// - Agent 未设置 ProviderId / ModelId 时，回退到全局默认 Provider / Model
/// - 默认 MaxSubAgents 来自 SystemSettings.Workflow.Magentic.MaxDynamicSubAgents（决策 TP-6）
/// - 高风险工具默认全部不勾选（决策 6-2-A 黑名单模式）
/// - 复用 <see cref="HighRiskToolItem"/>（来自 NewTaskDialogVm.cs，避免重复定义）
///
/// DI 生命周期：Singleton（与 WorkspaceTabVm 同源）。
///
/// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md §2.1 + §2.2。
/// </summary>
public sealed class WorkflowInputVm : INotifyPropertyChanged
{
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly TaskExecutionEngine _engine;
    private readonly SystemSettingsService _systemSettings;

    private string _initialInput = string.Empty;
    private AgentEntity? _selectedManager;
    private AiProviderEntity? _selectedProvider;
    private AiModelEntity? _selectedModel;
    private string _subMode = "magentic";
    private int _maxSubAgents = 5;
    private string _workspaceId = string.Empty;
    private bool _isRunning;
    private string? _currentTaskId;
    private string? _validationError;

    /// <summary>SystemSettings key：默认 Manager 智能体 ID（决策 TP-2 B）。</summary>
    private const string DefaultManagerKey = "Workflow.DefaultManagerId";

    /// <summary>SystemSettings key：默认 Provider ID（用户上次选择的厂商）。</summary>
    private const string DefaultProviderKey = "Workflow.DefaultProviderId";

    /// <summary>SystemSettings key：默认 Model ID（用户上次选择的模型）。</summary>
    private const string DefaultModelKey = "Workflow.DefaultModelId";

    /// <summary>SystemSettings key：默认子模式（"magentic" / "parallelanalysis"）。</summary>
    private const string DefaultSubModeKey = "Workflow.DefaultSubMode";

    /// <summary>SystemSettings key：Magentic 模式下 Manager 最多创建的子智能体数（决策 TP-6）。</summary>
    private const string MaxDynamicSubAgentsKey = "Workflow.Magentic.MaxDynamicSubAgents";

    public WorkflowInputVm()
    {
        _agentService = App.Services.GetRequiredService<AgentService>();
        _providerService = App.Services.GetRequiredService<AiProviderService>();
        _modelService = App.Services.GetRequiredService<AiModelService>();
        _engine = App.Services.GetRequiredService<TaskExecutionEngine>();
        _systemSettings = App.Services.GetRequiredService<SystemSettingsService>();

        // 启动时一次性读取 SystemSettings 持久化值
        _maxSubAgents = _systemSettings.GetValue(MaxDynamicSubAgentsKey, 5);
        _subMode = _systemSettings.GetValue(DefaultSubModeKey, "magentic");

        LoadAvailableAgents();
        LoadAvailableProviders();
        RestoreDefaultSelections();
    }

    // ──── 输入字段 ────

    /// <summary>用户输入的任务描述。绑定到输入框 Text。</summary>
    public string InitialInput
    {
        get => _initialInput;
        set
        {
            if (SetField(ref _initialInput, value))
                OnPropertyChanged(nameof(CanSubmit));
        }
    }

    /// <summary>
    /// 主智能体（Manager）。绑定到智能体选择器。
    /// 切换时联动：
    /// - 若 Agent 设置了 ProviderId / ModelId → 自动切换 SelectedProvider / SelectedModel
    /// - 否则保持当前 Provider / Model（用户上次选择或全局默认）
    /// </summary>
    public AgentEntity? SelectedManager
    {
        get => _selectedManager;
        set
        {
            if (SetField(ref _selectedManager, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                OnPropertyChanged(nameof(SelectedManagerName));
                if (value is not null)
                {
                    _systemSettings.SetValue(DefaultManagerKey, value.Id);
                    // 联动 Agent.ProviderId / Agent.ModelId（条款 7：Agent 没设的用默认）
                    SyncProviderModelFromAgent(value);
                }
            }
        }
    }

    /// <summary>智能体显示名（用于工具栏按钮 Label）。Agent 为 null 时显示"未选择"。</summary>
    public string SelectedManagerName => _selectedManager?.Name ?? "未选择";

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
                {
                    _systemSettings.SetValue(DefaultProviderKey, value.Id);
                    LoadModelsForProvider(value.Id);
                    // 不重置 SelectedModel：由 LoadModelsForProvider 内部决定是否保留
                }
            }
        }
    }

    /// <summary>厂商显示名（用于工具栏按钮 Label）。</summary>
    public string SelectedProviderName => _selectedProvider?.Name ?? "未选择";

    /// <summary>当前选中的模型。</summary>
    public AiModelEntity? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetField(ref _selectedModel, value))
            {
                OnPropertyChanged(nameof(SelectedModelName));
                if (value is not null)
                    _systemSettings.SetValue(DefaultModelKey, value.Id);
            }
        }
    }

    /// <summary>模型显示名（用于工具栏按钮 Label）。优先 DisplayName，再 Name。</summary>
    public string SelectedModelName =>
        _selectedModel is null
            ? "未选择"
            : !string.IsNullOrWhiteSpace(_selectedModel.DisplayName)
                ? _selectedModel.DisplayName!
                : _selectedModel.Name;

    /// <summary>
    /// 子模式："magentic" / "parallelanalysis"（条款 2：UI 显示纯中文「自主规划」/「并行分析」）。
    /// </summary>
    public string SubMode
    {
        get => _subMode;
        set
        {
            if (SetField(ref _subMode, value))
            {
                OnPropertyChanged(nameof(SubModeDisplayName));
                OnPropertyChanged(nameof(IsMagentic));
                _systemSettings.SetValue(DefaultSubModeKey, value);
            }
        }
    }

    /// <summary>子模式中文显示名（条款 2 + 条款 9）。</summary>
    public string SubModeDisplayName => _subMode switch
    {
        "magentic" => "自主规划",
        "parallelanalysis" => "并行分析",
        _ => _subMode,
    };

    /// <summary>是否 Magentic 模式（用于决定子 Agent 数量按钮可见性，条款 10）。</summary>
    public bool IsMagentic => string.Equals(_subMode, "magentic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Magentic 模式下 Manager 最多创建的子智能体数（决策 TP-6，默认 5，下拉 1-20）。
    /// 条款 3：去掉 NumericUpDown，改为下拉框（Popup 列表 1-20）。
    /// </summary>
    public int MaxSubAgents
    {
        get => _maxSubAgents;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (SetField(ref _maxSubAgents, clamped))
            {
                OnPropertyChanged(nameof(MaxSubAgentsDisplay));
                _systemSettings.SetValue(MaxDynamicSubAgentsKey, clamped.ToString());
            }
        }
    }

    /// <summary>子 Agent 数量按钮显示文本（条款 10 + Bug 4 修复 2026-05-17）。</summary>
    public string MaxSubAgentsDisplay => $"智能体数: {_maxSubAgents}";

    /// <summary>当前工作区 ID。由调用方（WorkflowDetailView 构造时）注入。</summary>
    public string WorkspaceId
    {
        get => _workspaceId;
        set => SetField(ref _workspaceId, value);
    }

    /// <summary>可选的智能体列表。绑定到智能体选择器 Popup 列表。</summary>
    public ObservableCollection<AgentEntity> AvailableAgents { get; } = [];

    /// <summary>可选的厂商列表。绑定到厂商选择器 Popup 列表。</summary>
    public ObservableCollection<AiProviderEntity> AvailableProviders { get; } = [];

    /// <summary>可选的模型列表（当前 Provider 下）。绑定到模型选择器 Popup 列表。</summary>
    public ObservableCollection<AiModelEntity> AvailableModels { get; } = [];

    /// <summary>
    /// 高风险工具屏蔽项。复用 <see cref="HighRiskToolItem"/>（来自 NewTaskDialogVm.cs）。
    /// 决策 6-2-A 黑名单模式：默认全部不勾选 = 全部允许；用户勾选 = 屏蔽。
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
    /// - 已选 Manager / Provider / Model
    /// </summary>
    public bool CanSubmit
    {
        get
        {
            if (_isRunning) return false;
            if (string.IsNullOrWhiteSpace(_initialInput)) return false;
            if (_selectedManager is null) return false;
            if (_selectedProvider is null) return false;
            if (_selectedModel is null) return false;
            return true;
        }
    }

    // ──── 加载 / 持久化恢复 ────

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

        // 若当前 SelectedModel 不在新列表中，重置为新列表的第一项（或保持 null）
        if (_selectedModel is not null && !AvailableModels.Any(m => m.Id == _selectedModel.Id))
        {
            _selectedModel = AvailableModels.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedModelName));
            OnPropertyChanged(nameof(CanSubmit));
            if (_selectedModel is not null)
                _systemSettings.SetValue(DefaultModelKey, _selectedModel.Id);
        }
    }

    /// <summary>
    /// 从 SystemSettings 恢复用户上次选择（顺序：Manager → Provider → Model）。
    /// <summary>
    /// 从 SystemSettings 恢复用户上次选择（顺序：Manager → Provider → Model）。
    /// 若 SystemSettings 无值或对应项已删除，回退到全局默认（IsDefault=true 的项）。
    /// Bug 5 修复 2026-05-17：所有三个字段都必须有默认值兜底（用户：智能体/厂商/模型都有默认值）。
    /// </summary>
    private void RestoreDefaultSelections()
    {
        // 1. 恢复 Manager（优先用户上次，回退全局默认 IsDefault=true，再回退第一个）
        var savedManagerId = _systemSettings.GetValue<string>(DefaultManagerKey, string.Empty);
        if (!string.IsNullOrEmpty(savedManagerId))
            _selectedManager = AvailableAgents.FirstOrDefault(a => a.Id == savedManagerId);
        _selectedManager ??= AvailableAgents.FirstOrDefault(a => a.IsDefault)
                          ?? AvailableAgents.FirstOrDefault();

        // 2. 恢复 Provider（优先 Manager 配置 → 用户上次 → 全局默认 → 第一个）
        var savedProviderId = _systemSettings.GetValue<string>(DefaultProviderKey, string.Empty);
        if (!string.IsNullOrEmpty(_selectedManager?.DefaultProviderId))
            _selectedProvider = AvailableProviders.FirstOrDefault(p => p.Id == _selectedManager.DefaultProviderId);
        if (_selectedProvider is null && !string.IsNullOrEmpty(savedProviderId))
            _selectedProvider = AvailableProviders.FirstOrDefault(p => p.Id == savedProviderId);
        _selectedProvider ??= AvailableProviders.FirstOrDefault(p => p.IsDefault)
                            ?? AvailableProviders.FirstOrDefault();

        // 3. 加载当前 Provider 下的模型 + 恢复 Model（优先 Manager 配置 → 用户上次 → 全局默认 → 第一个）
        if (_selectedProvider is not null)
        {
            var models = _modelService.GetByProviderId(_selectedProvider.Id);
            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);

            var savedModelId = _systemSettings.GetValue<string>(DefaultModelKey, string.Empty);
            if (!string.IsNullOrEmpty(_selectedManager?.DefaultModelId))
                _selectedModel = AvailableModels.FirstOrDefault(m => m.Id == _selectedManager.DefaultModelId);
            if (_selectedModel is null && !string.IsNullOrEmpty(savedModelId))
                _selectedModel = AvailableModels.FirstOrDefault(m => m.Id == savedModelId);
            _selectedModel ??= AvailableModels.FirstOrDefault(m => m.IsDefault)
                              ?? AvailableModels.FirstOrDefault();
        }

        OnPropertyChanged(nameof(SelectedManager));
        OnPropertyChanged(nameof(SelectedManagerName));
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelName));
        OnPropertyChanged(nameof(CanSubmit));
    }

    /// <summary>
    /// Manager 切换时根据 Agent.DefaultProviderId / Agent.DefaultModelId 联动切换厂商/模型。
    /// 条款 7：Agent 没设的就用当前默认（不强制覆盖用户选择）。
    /// </summary>
    private void SyncProviderModelFromAgent(AgentEntity agent)
    {
        // 1. Provider：仅当 Agent 显式设置且与当前不同时才切换
        if (!string.IsNullOrEmpty(agent.DefaultProviderId) &&
            agent.DefaultProviderId != _selectedProvider?.Id)
        {
            var newProvider = AvailableProviders.FirstOrDefault(p => p.Id == agent.DefaultProviderId);
            if (newProvider is not null)
            {
                _selectedProvider = newProvider;
                _systemSettings.SetValue(DefaultProviderKey, newProvider.Id);
                LoadModelsForProvider(newProvider.Id);
                OnPropertyChanged(nameof(SelectedProvider));
                OnPropertyChanged(nameof(SelectedProviderName));
            }
        }

        // 2. Model：仅当 Agent 显式设置且与当前不同时才切换
        if (!string.IsNullOrEmpty(agent.DefaultModelId) &&
            agent.DefaultModelId != _selectedModel?.Id)
        {
            var newModel = AvailableModels.FirstOrDefault(m => m.Id == agent.DefaultModelId);
            if (newModel is not null)
            {
                _selectedModel = newModel;
                _systemSettings.SetValue(DefaultModelKey, newModel.Id);
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SelectedModelName));
            }
        }

        OnPropertyChanged(nameof(CanSubmit));
    }

    // ──── 启动 / 停止 ────

    /// <summary>启动工作流任务。成功返回 taskId 并把 IsRunning 切换到 true；失败设置 ValidationError 返回 null。</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        if (!CanSubmit)
        {
            // P2-2 修复 2026-05-17：错误提示改为"请先到设置中创建"导向（用户决策：不怪用户没勾选）。
            // 选择器初始化时会自动填充默认值，只有系统数据完全为空时才会出现 null。
            ValidationError = _selectedManager is null
                ? "未找到可用的智能体，请先到设置中创建智能体。"
                : _selectedProvider is null
                    ? "未找到可用的厂商，请先到设置中创建厂商。"
                    : _selectedModel is null
                        ? "未找到可用的模型，请先到设置中创建模型。"
                        : "请填写任务描述";
            return null;
        }

        ValidationError = null;
        IsRunning = true;
        try
        {
            // P4：直接调用 TaskExecutionEngine.StartTaskAsync（不再构造 WorkflowTaskRequest）
            // templateId 暂时为 null（后续 P4-6 模板功能接入时从 UI 传入）
            var taskId = await _engine.StartTaskAsync(
                _initialInput.Trim(),
                _workspaceId,
                templateId: null,
                cancellationToken);
            CurrentTaskId = taskId;

            // 启动成功：清空输入框（用户可继续输入下一个任务）
            _initialInput = string.Empty;
            OnPropertyChanged(nameof(InitialInput));
            OnPropertyChanged(nameof(CanSubmit));

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
