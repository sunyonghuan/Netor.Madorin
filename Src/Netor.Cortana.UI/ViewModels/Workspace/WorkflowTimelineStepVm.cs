using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P3-1：工作流时间线步骤 ViewModel。
/// 继承 <see cref="StepItemVm"/>，在序号/Agent名/状态基础上增加：
/// - 时间线显示属性（TimeText / NodeColor）
/// - 思考过程折叠区（ThinkingText / IsThinkingExpanded）
/// - 工具调用折叠区（ToolCalls / IsToolCallsExpanded）
/// - 主体输出内容（ContentText，从 SummaryJson 解析或 fallback 到 Summary）
/// - Token 消耗显示（TokenText）
///
/// 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/02-P3方案设计.md §2 P3-1。
/// </summary>
public sealed class WorkflowTimelineStepVm : StepItemVm
{
    private bool _isThinkingExpanded;
    private bool _isToolCallsExpanded;

    /// <summary>从 step.completed 事件构造（运行期增量追加）。</summary>
    public WorkflowTimelineStepVm(WorkflowStepCompletedArgs args) : base(args)
    {
        TokenText = FormatTokenText(args.TokenInputCount, args.TokenOutputCount);
        ParseSummaryContent(args.SummaryJson);
    }

    /// <summary>从已落库的 step 实体构造（加载详情时批量初始化）。</summary>
    public WorkflowTimelineStepVm(OrchestrationStepEntity entity) : base(entity)
    {
        TokenText = FormatTokenText(entity.TokenInputCount, entity.TokenOutputCount);
        ParseSummaryContent(entity.SummaryJson);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 时间线展示属性
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>左侧时间列文字（HH:mm:ss）。</summary>
    public string TimeText
    {
        get
        {
            if (StartedAt <= 0) return string.Empty;
            return DateTimeOffset.FromUnixTimeMilliseconds(StartedAt).ToLocalTime().ToString("HH:mm:ss");
        }
    }

    /// <summary>节点颜色（根据步骤状态）。</summary>
    public string NodeColor => Status switch
    {
        "running" => "#007acc",
        "completed" => "#73c991",
        "failed" => "#f48771",
        "skipped" => "#858585",
        _ => "#007acc",
    };

    // ══════════════════════════════════════════════════════════════════════
    // 主体内容
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>主体输出内容（去掉思考部分后的正文）。</summary>
    public string? ContentText { get; private set; }

    /// <summary>是否有主体内容。</summary>
    public bool HasContent => !string.IsNullOrEmpty(ContentText);

    // ══════════════════════════════════════════════════════════════════════
    // 思考过程折叠区
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>思考过程文本。</summary>
    public string? ThinkingText { get; private set; }

    /// <summary>是否有思考过程。</summary>
    public bool HasThinking => !string.IsNullOrEmpty(ThinkingText);

    /// <summary>思考折叠状态。</summary>
    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set
        {
            if (_isThinkingExpanded == value) return;
            _isThinkingExpanded = value;
            OnPropertyChanged();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 工具调用折叠区
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>工具调用列表。</summary>
    public List<ToolCallDisplayInfo> ToolCalls { get; private set; } = [];

    /// <summary>是否有工具调用。</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>工具调用折叠状态。</summary>
    public bool IsToolCallsExpanded
    {
        get => _isToolCallsExpanded;
        set
        {
            if (_isToolCallsExpanded == value) return;
            _isToolCallsExpanded = value;
            OnPropertyChanged();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Token 消耗
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Token 消耗文字（如 "1.2k tokens"）。</summary>
    public string? TokenText { get; }

    /// <summary>是否有 Token 信息。</summary>
    public bool HasTokenText => !string.IsNullOrEmpty(TokenText);

    // ══════════════════════════════════════════════════════════════════════
    // 内部解析
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 SummaryJson 解析内容、思考文本和工具调用。
    /// SummaryJson 格式未统一，本期用 fallback 策略：
    /// - 尝试 JSON 解析（如果后端输出结构化数据）
    /// - 失败则将整个文本作为 ContentText
    /// </summary>
    private void ParseSummaryContent(string? summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            ContentText = null;
            return;
        }

        // 尝试 JSON 解析（后端可能输出 { "content": "...", "thinking": "...", "tool_calls": [...] }）
        if (summaryJson.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(summaryJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("content", out var contentProp))
                    ContentText = contentProp.GetString();
                else if (root.TryGetProperty("output", out var outputProp))
                    ContentText = outputProp.GetString();

                if (root.TryGetProperty("thinking", out var thinkingProp))
                    ThinkingText = thinkingProp.GetString();

                if (root.TryGetProperty("tool_calls", out var toolCallsProp) &&
                    toolCallsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsProp.EnumerateArray())
                    {
                        var name = tc.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
                        var args = tc.TryGetProperty("arguments", out var a) ? a.ToString() : null;
                        var result = tc.TryGetProperty("result", out var r) ? r.GetString() : null;
                        ToolCalls.Add(new ToolCallDisplayInfo(name ?? "unknown", args, result));
                    }
                }

                // 如果 JSON 解析成功但 ContentText 为空，fallback 到整个 JSON
                ContentText ??= summaryJson;
                return;
            }
            catch (JsonException)
            {
                // 非有效 JSON，走 fallback
            }
        }

        // Fallback：纯文本作为 ContentText
        ContentText = summaryJson;
    }

    private static string? FormatTokenText(int? inputCount, int? outputCount)
    {
        var total = (long)(inputCount ?? 0) + (outputCount ?? 0);
        if (total <= 0) return null;
        return total >= 1000 ? $"{total / 1000.0:F1}k tokens" : $"{total} tokens";
    }

}

/// <summary>工具调用的显示信息。</summary>
public sealed record ToolCallDisplayInfo(string ToolName, string? Arguments, string? Result);
