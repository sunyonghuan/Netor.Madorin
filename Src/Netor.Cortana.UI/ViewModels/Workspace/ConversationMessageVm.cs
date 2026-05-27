using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

// ════════════════════════════════════════════════════════════════════════════
// 工作流对话模式数据模型（重构版 2026-05-26）
// 文档 08 §2.2：对话模式 — 无头像纯数据流，轻量分隔线区分发言方。
// 文档 08 §2.3：执行模式 — 时间线进度面板（竖线 + 圆点节点 + 子过程行）。
//
// 设计原则：
// - 整个工作流只有一个对话流，所有信息（AI 思考、计划、执行进度、结果）
//   都以不同 Role 的消息呈现在同一个流中。
// - 没有独立的"审批面板"、"失败详情面板"、"步骤列表面板"。
// - progress 消息通过 ProgressType 细分为阶段头 / 步骤头 / 子过程行，
//   在 XAML 中以竖线时间线 + 圆点样式呈现。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 进度消息子类型（Role="progress" 时有意义）。
/// 用于在 XAML 中选择时间线节点样式。
/// </summary>
public enum ProgressKind
{
    /// <summary>非进度消息（默认）。</summary>
    None = 0,

    /// <summary>阶段开始：▶ 需求分析（蓝色实心圆 + 粗体标题）。</summary>
    PhaseStart,

    /// <summary>阶段完成：✓ 需求分析 完成（绿色实心圆 + 标题）。</summary>
    PhaseEnd,

    /// <summary>步骤开始：● 步骤 1: 数据采集（蓝色实心圆 + 标题，缩进在阶段下）。</summary>
    StepStart,

    /// <summary>步骤完成：✓ 步骤 1 完成（绿色小圆）。</summary>
    StepEnd,

    /// <summary>步骤失败：✗ 步骤 1 失败（红色小圆）。</summary>
    StepFail,

    /// <summary>步骤等待/跳过/重试等辅助状态行。</summary>
    StepAux,

    /// <summary>子过程详情行（灰色小字，└ 缩进）。</summary>
    Detail,

    /// <summary>计划生成/确认/更新等计划类消息（■ 图标）。</summary>
    Plan,

    /// <summary>验证结果行。</summary>
    Validation,

    /// <summary>任务完成/取消等生命周期终态。</summary>
    Lifecycle,
}

/// <summary>
/// 工作流对话流中的单条消息。
/// 所有内容统一为消息，通过 Role 区分样式。
/// </summary>
public sealed class ConversationMessageVm : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isStreaming;
    private bool _isExpanded;
    private bool _isCardCompleted;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>消息唯一 ID（用于去重/流式更新）。</summary>
    public required string MessageId { get; init; }

    /// <summary>消息时间戳。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// 消息角色：
    /// "user"       → 用户消息（气泡样式）
    /// "ai"         → AI 回复（纯文本，支持流式光标）
    /// "result"     → 执行结果摘要（含产出文件列表）
    /// "system"     → 系统提示（灰色小字）
    /// "progress"   → 执行进度行（时间线步骤状态）
    /// "tool_auth"  → 工具授权请求（内联在对话流中）
    /// </summary>
    public required string Role { get; init; }

    /// <summary>当卡片内容更新时触发，用于通知视图层滚动 ScrollViewer。</summary>
    public event Action? ContentUpdated;

    /// <summary>消息文本内容（流式 AI 回复时动态追加）。</summary>
    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasContent));
            if (IsStepCard && _isExpanded)
                ContentUpdated?.Invoke();
        }
    }

    /// <summary>是否正在流式输出（AI 回复中）。</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value) return;
            _isStreaming = value;
            OnPropertyChanged();
        }
    }

    // ──── 时间线专用属性（Role="progress" 时有值） ────

    /// <summary>进度消息子类型，决定时间线圆点样式 / 缩进层级 / 字体大小。</summary>
    public ProgressKind ProgressType { get; init; }

    /// <summary>时间线圆点颜色（蓝色=执行中 / 绿色=完成 / 红色=失败 / 灰色=等待）。</summary>
    public string DotColor { get; init; } = "#858585";

    /// <summary>是否为"阶段"级节点（PhaseStart / PhaseEnd）— 大圆点 + 粗体。</summary>
    public bool IsPhaseNode => ProgressType is ProgressKind.PhaseStart or ProgressKind.PhaseEnd;

    /// <summary>是否为"步骤"级节点（StepStart / StepEnd / StepFail / StepAux）— 中圆点。</summary>
    public bool IsStepNode => ProgressType is ProgressKind.StepStart or ProgressKind.StepEnd
                                           or ProgressKind.StepFail or ProgressKind.StepAux;

    /// <summary>是否为子过程详情行 — 无圆点，└ 缩进灰色小字。</summary>
    public bool IsDetailLine => ProgressType == ProgressKind.Detail;

    /// <summary>是否为计划类消息。</summary>
    public bool IsPlanNode => ProgressType == ProgressKind.Plan;

    /// <summary>是否为验证类消息。</summary>
    public bool IsValidationNode => ProgressType == ProgressKind.Validation;

    /// <summary>是否为生命周期终态消息。</summary>
    public bool IsLifecycleNode => ProgressType == ProgressKind.Lifecycle;

    /// <summary>是否为 Plan / Validation / Lifecycle 节点之一（XAML 中控制圆点可见性）。</summary>
    public bool IsOtherTimelineNode => IsPlanNode || IsValidationNode || IsLifecycleNode;

    /// <summary>是否需要显示时间线圆点（非 Detail 的 progress 消息都显示圆点）。</summary>
    public bool ShowTimelineDot => Role == "progress" && ProgressType != ProgressKind.Detail;

    /// <summary>时间线标题文字（圆点右侧的主文本，阶段/步骤名称）。</summary>
    public string? TimelineTitle { get; init; }

    /// <summary>时间线副文本（圆点右侧第二行，如步骤的结果摘要）。</summary>
    public string? TimelineSubText { get; init; }

    /// <summary>是否为最后一条进度消息（决定是否绘制向下的竖线）。由 VM 层在追加时维护。</summary>
    public bool IsLastProgress
    {
        get => _isLastProgress;
        set
        {
            if (_isLastProgress == value) return;
            _isLastProgress = value;
            OnPropertyChanged();
        }
    }
    private bool _isLastProgress;

    // ──── 执行结果专用属性（Role="result" 时有值） ────

    /// <summary>执行结果摘要。</summary>
    public string? ResultSummary { get; init; }

    /// <summary>执行产出文件列表。</summary>
    public List<ResultFileVm>? ResultFiles { get; init; }

    // ──── 工具授权专用属性（Role="tool_auth" 时有值） ────

    /// <summary>授权请求 ID。</summary>
    public string? AuthRequestId { get; init; }

    /// <summary>工具名称。</summary>
    public string? ToolName { get; init; }

    /// <summary>工具风险等级（2=高风险可恢复, 3=极高风险不可逆）。</summary>
    public int RiskLevel { get; init; }

    /// <summary>调用参数摘要。</summary>
    public string? ParametersSummary { get; init; }

    // ──── 步骤执行卡片属性（Role="step_card" 时有值） ────

    /// <summary>关联的步骤 ID（用于将 AI 消息归组到此卡片）。</summary>
    public string? StepId { get; init; }

    /// <summary>卡片标题（步骤名称）。</summary>
    public string? CardTitle { get; init; }

    /// <summary>卡片是否展开（默认展开，完成后自动折叠）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ArrowAngle));
        }
    }

    /// <summary>卡片是否已完成（完成后自动折叠、显示完成标记）。</summary>
    public bool IsCardCompleted
    {
        get => _isCardCompleted;
        set
        {
            if (_isCardCompleted == value) return;
            _isCardCompleted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardStatusText));
            OnPropertyChanged(nameof(CardStatusColor));
            OnPropertyChanged(nameof(ArrowAngle));
        }
    }

    /// <summary>卡片展开时同步更新箭头角度。</summary>
    public double ArrowAngle => _isExpanded ? 90 : 0;

    /// <summary>卡片状态文字（XAML 绑定用，避免不存在的 BoolConverters.ToString）。</summary>
    public string CardStatusText => _isCardCompleted ? "✓" : "…";

    /// <summary>卡片状态颜色（XAML 绑定用）。</summary>
    public string CardStatusColor => _isCardCompleted ? "#cccccc" : "#858585";

    /// <summary>是否为步骤执行卡片消息。</summary>
    public bool IsStepCard => Role == "step_card";

    // ──── 派生显示属性 ────

    /// <summary>内容文字颜色。</summary>
    public string ContentColor => Role switch
    {
        "user"      => "#d4d4d4",
        "ai"        => "#cccccc",
        "result"    => "#e0c074",
        "system"    => "#858585",
        "progress"  => "#858585",
        "tool_auth" => "#e0c074",
        _           => "#cccccc",
    };

    /// <summary>是否有内容。</summary>
    public bool HasContent => !string.IsNullOrEmpty(_content);

    /// <summary>是否有执行结果文件。</summary>
    public bool HasResultFiles => ResultFiles is { Count: > 0 };

    // ──── 角色判断（供 View DataTemplate 选择器使用） ────

    public bool IsUserMessage => Role == "user";
    public bool IsAiMessage   => Role == "ai";
    public bool IsResult      => Role == "result";
    public bool IsSystem      => Role == "system";
    public bool IsProgress    => Role == "progress";
    public bool IsToolAuth    => Role == "tool_auth";

    /// <summary>是否显示"本任务全部授权"选项（仅 Level 2）。</summary>
    public bool ShowGrantAllOption => Role == "tool_auth" && RiskLevel == 2;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 执行产出文件项（文档 08 §6.1 文件列表）。
/// </summary>
public sealed class ResultFileVm
{
    /// <summary>文件完整路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>显示名称（相对路径或文件名）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>文件大小文本（如 "12 KB"）。</summary>
    public string? SizeText { get; init; }

    /// <summary>文件类型图标（Emoji）。</summary>
    public string IconText => System.IO.Path.GetExtension(FilePath).ToLowerInvariant() switch
    {
        ".md" or ".txt" or ".doc" or ".docx" => "📄",
        ".xlsx" or ".xls" or ".csv"          => "📊",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "📈",
        ".pdf"  => "📋",
        _       => "📁",
    };

    /// <summary>hover 时显示的完整路径（tooltip）。</summary>
    public string FullPathTooltip => FilePath;
}
