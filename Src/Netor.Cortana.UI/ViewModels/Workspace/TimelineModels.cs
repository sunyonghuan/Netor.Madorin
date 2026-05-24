using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Netor.Cortana.UI.ViewModels.Workspace;

// ════════════════════════════════════════════════════════════════════════════
// P4 时间线 UI 数据模型
// 用于 P4TimelinePreviewView（mock 数据预览）和未来生产环境。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 时间线上的单个事件项。驱动 UI 时间线渲染。
/// </summary>
public sealed class TimelineEventVm : INotifyPropertyChanged
{
    /// <summary>事件 ID（用于去重）。</summary>
    public required string EventId { get; init; }

    /// <summary>时间戳。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 事件类型：
    /// task_started / task_completed /
    /// phase_started / phase_completed /
    /// step_started / step_completed / step_failed / step_retrying / step_progress /
    /// user_message / agent_message /
    /// plan_created / plan_confirmed /
    /// waiting_user
    /// </summary>
    public required string EventType { get; init; }

    /// <summary>
    /// 节点级别：
    /// "primary" → ● 圆点（关键事件，10px）
    /// "secondary" → ├ 小圆点（中间信息，6px）
    /// </summary>
    public string NodeLevel { get; init; } = "primary";

    /// <summary>关联的步骤 ID（如果有）。</summary>
    public string? StepId { get; init; }

    /// <summary>关联的步骤序号。</summary>
    public int? StepSequence { get; init; }

    /// <summary>标题行（加粗显示）。</summary>
    public required string Title { get; init; }

    /// <summary>详情文本（正常字重）。</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 状态标记：running / completed / failed / retrying / waiting / null。
    /// 决定圆点颜色。
    /// </summary>
    public string? Status { get; init; }

    /// <summary>进度百分比（step_progress 事件用）。</summary>
    public int? ProgressPercent { get; init; }

    /// <summary>内嵌卡片（计划确认、工具授权、执行报表等）。</summary>
    public TimelineCardVm? Card { get; init; }

    /// <summary>是否与上一步骤并行执行（显示并行标签）。</summary>
    public bool IsParallel { get; init; }

    // ──── 派生显示属性 ────

    /// <summary>左侧时间列文字。</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    /// <summary>圆点大小：primary=10，secondary=6。</summary>
    public double NodeSize => NodeLevel == "primary" ? 10.0 : 6.0;

    /// <summary>根据 Status 确定的语义颜色（primary 节点用）。</summary>
    public string NodeColor => Status switch
    {
        "running" => "#007acc",
        "completed" => "#73c991",
        "failed" => "#f48771",
        "retrying" => "#e0c074",
        "waiting" => "#858585",
        _ => "#007acc",
    };

    /// <summary>实际圆点颜色：primary 用语义色，secondary 统一灰色。</summary>
    public string ActualNodeColor => NodeLevel == "primary" ? NodeColor : "#666666";

    /// <summary>标题颜色：primary=#cccccc，secondary=#999999。</summary>
    public string TitleColor => NodeLevel == "primary" ? "#cccccc" : "#999999";

    /// <summary>标题字重：primary=SemiBold，secondary=Normal。</summary>
    public FontWeight TitleFontWeight => NodeLevel == "primary" ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>是否有详情文本。</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>是否有进度条。</summary>
    public bool HasProgress => ProgressPercent.HasValue;

    /// <summary>是否有内嵌卡片。</summary>
    public bool HasCard => Card is not null;

    // ──── INotifyPropertyChanged ────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 时间线中的内嵌卡片（计划确认、工具授权、智能体创建审批、执行报表等）。
/// </summary>
public sealed class TimelineCardVm
{
    /// <summary>
    /// 卡片类型：
    /// "plan_confirmation" — 计划确认卡片
    /// "tool_call_auth" — 工具调用逐次授权卡片
    /// "agent_creation_auth" — 动态子智能体创建审批卡片
    /// "execution_report" — 执行报表卡片
    /// </summary>
    public required string CardType { get; init; }

    /// <summary>卡片标题。</summary>
    public required string Title { get; init; }

    /// <summary>卡片内容行（每行一条信息，通用渲染）。</summary>
    public List<string> ContentLines { get; init; } = [];

    /// <summary>操作按钮列表。</summary>
    public List<TimelineCardActionVm> Actions { get; init; } = [];

    // ── tool_call_auth 专用字段 ──

    /// <summary>被调用的工具名。</summary>
    public string? ToolName { get; init; }

    /// <summary>调用该工具的子智能体名称。</summary>
    public string? CallerAgentName { get; init; }

    /// <summary>工具调用参数（key=value）。</summary>
    public Dictionary<string, string>? ToolParameters { get; init; }

    /// <summary>风险描述（展示给用户）。</summary>
    public string? RiskDescription { get; init; }

    // ── agent_creation_auth 专用字段 ──

    /// <summary>拟创建的子智能体名称。</summary>
    public string? ProposedAgentName { get; init; }

    /// <summary>拟创建的子智能体职责。</summary>
    public string? ProposedResponsibility { get; init; }

    /// <summary>拟创建的子智能体工具列表文本。</summary>
    public string? ProposedToolsText { get; init; }

    // ── 类型判断（用于 XAML IsVisible 绑定） ──

    public bool IsPlanConfirmation => CardType == "plan_confirmation";
    public bool IsToolCallAuth => CardType == "tool_call_auth";
    public bool IsAgentCreationAuth => CardType == "agent_creation_auth";
    public bool IsExecutionReport => CardType == "execution_report";
}

/// <summary>卡片操作按钮。</summary>
public sealed class TimelineCardActionVm
{
    /// <summary>按钮文字。</summary>
    public required string Label { get; init; }

    /// <summary>按钮样式：primary / secondary / danger。</summary>
    public string Style { get; init; } = "primary";

    /// <summary>点击后的动作标识（由 View 层处理）。</summary>
    public required string ActionId { get; init; }
}

/// <summary>
/// 计划概览面板中的单个步骤（Plan Overview Panel）。
/// </summary>
public sealed class PlanStepOverviewVm
{
    /// <summary>步骤序号。</summary>
    public int Sequence { get; init; }

    /// <summary>步骤标题。</summary>
    public required string Title { get; init; }

    /// <summary>状态图标（✅ / 🔄 / ⏳ / ❌）。</summary>
    public required string StatusIcon { get; init; }

    /// <summary>状态颜色（hex）。</summary>
    public required string StatusColor { get; init; }

    /// <summary>是否并行执行。</summary>
    public bool IsParallel { get; init; }

    /// <summary>依赖标注文本（如 "依赖1+2"）。</summary>
    public string? DependsOnText { get; init; }

    /// <summary>并行/依赖标注的展示文本。</summary>
    public string ModeText => IsParallel ? "并行" : DependsOnText ?? "顺序";
}

/// <summary>
/// hex 字符串 → SolidColorBrush 转换器。
/// 用于 Foreground / Fill 等颜色属性的 binding。
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
            return SolidColorBrush.Parse(hex);
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
