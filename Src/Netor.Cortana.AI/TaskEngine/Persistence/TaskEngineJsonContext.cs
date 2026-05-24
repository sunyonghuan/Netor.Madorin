using System.Text.Json.Serialization;

using Netor.Cortana.AI.TaskEngine.Agents;
using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine.Persistence;

/// <summary>
/// AOT 兼容的 JSON 序列化上下文，为 P4 任务引擎所有持久化模型提供源生成器支持。
/// 遵循项目现有模式（参见 AppSettingsJsonContext.cs）。
/// </summary>
[JsonSerializable(typeof(ExecutionPlan))]
[JsonSerializable(typeof(PlanStep))]
[JsonSerializable(typeof(PlanSubTask))]
[JsonSerializable(typeof(RequirementsAnalysis))]
[JsonSerializable(typeof(StepResult))]
[JsonSerializable(typeof(ExecutionCheckpoint))]
[JsonSerializable(typeof(ExecutionTemplate))]
[JsonSerializable(typeof(TemplateStep))]
[JsonSerializable(typeof(TemplateSubTask))]
[JsonSerializable(typeof(RunMeta))]
[JsonSerializable(typeof(TaskMeta))]
[JsonSerializable(typeof(StepIntent))]
[JsonSerializable(typeof(RequirementsAnalysisDto))]
[JsonSerializable(typeof(PlanningResponseDto))]
[JsonSerializable(typeof(PlanningStepDto))]
[JsonSerializable(typeof(PlanningSubTaskDto))]
[JsonSerializable(typeof(StepExecutionResponseDto))]
[JsonSerializable(typeof(ValidationResponseDto))]
[JsonSerializable(typeof(PlanDiffResult))]
[JsonSerializable(typeof(List<PlanningStepDto>))]
[JsonSerializable(typeof(List<PlanningSubTaskDto>))]
[JsonSerializable(typeof(List<PlanStep>))]
[JsonSerializable(typeof(List<PlanSubTask>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TemplateStep>))]
[JsonSerializable(typeof(List<TemplateSubTask>))]
[JsonSerializable(typeof(Dictionary<string, PlanStepStatus>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<PlanStatus>),
                  typeof(JsonStringEnumConverter<PlanStepStatus>)])]
internal partial class TaskEngineJsonContext : JsonSerializerContext;

// ══════════════════════════════════════════════════════════════════════
// 持久化辅助模型
// ══════════════════════════════════════════════════════════════════════

/// <summary>
/// 运行元信息（run.json）。
/// 每次启动执行时写入，记录本次运行的生命周期。
/// </summary>
public sealed class RunMeta
{
    /// <summary>Run ID（run-{yyyyMMdd}-{HHmmss}-{hex}）。</summary>
    public required string RunId { get; init; }

    /// <summary>所属任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>关联的计划版本号。</summary>
    public int PlanVersion { get; set; }

    /// <summary>运行状态：running / completed / failed / cancelled。</summary>
    public string Status { get; set; } = "running";

    /// <summary>运行开始时间。</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>运行结束时间（完成/失败/取消时填充）。</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// 任务元信息（task.json）。
/// 跨 run 共享的任务级元数据。
/// </summary>
public sealed class TaskMeta
{
    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>任务标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>任务状态：running / completed / failed / cancelled。</summary>
    public string Status { get; set; } = "running";

    /// <summary>最新的 Run ID。</summary>
    public string? LatestRunId { get; set; }

    /// <summary>任务创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <summary>是否置顶（UI 列表排序用）。</summary>
    public bool IsPinned { get; set; }

    /// <summary>是否已归档（UI 列表过滤用）。</summary>
    public bool IsArchived { get; set; }

    /// <summary>任务启动时用户选择的 Provider ID（可选）。</summary>
    public string? ProviderId { get; set; }

    /// <summary>任务启动时用户选择的 Model ID（可选）。</summary>
    public string? ModelId { get; set; }

    /// <summary>任务子模式（magentic / group_chat / parallel_analysis）。</summary>
    public string? SubMode { get; set; }
}

/// <summary>
/// WAL 步骤执行意图标记（intent.json）。
/// 在步骤开始执行前写入，用于断点恢复时推断步骤状态。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/05-P4补充-上下文传递与恢复机制.md §3.2。
/// </summary>
public sealed class StepIntent
{
    /// <summary>步骤 ID。</summary>
    public required string StepId { get; init; }

    /// <summary>步骤标题。</summary>
    public required string StepTitle { get; init; }

    /// <summary>步骤开始时间。</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>写入 intent 时的计划版本号。</summary>
    public int PlanVersion { get; init; }
}

// ══════════════════════════════════════════════════════════════════════
// P4-3: 子智能体 JSON 响应 DTO（LLM 输出的 camelCase JSON 反序列化目标）
// ══════════════════════════════════════════════════════════════════════

/// <summary>需求分析师子智能体的 JSON 输出 DTO。</summary>
public sealed class RequirementsAnalysisDto
{
    public string? OriginalInput { get; set; }
    public List<string>? KeyPoints { get; set; }
    public List<string>? Constraints { get; set; }
    public string? ExpectedDeliverable { get; set; }
    public string? ComplexityLevel { get; set; }
}

/// <summary>计划制定师子智能体的 JSON 输出 DTO。</summary>
public sealed class PlanningResponseDto
{
    public string? TaskSummary { get; set; }
    public string? FinalGoal { get; set; }
    public List<PlanningStepDto>? Steps { get; set; }
}

/// <summary>计划步骤 DTO（LLM 输出格式）。</summary>
public sealed class PlanningStepDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ExecutionMode { get; set; }
    public List<string>? DependsOn { get; set; }
    public string? AgentTypeDescription { get; set; }
    public List<string>? RequiredTools { get; set; }
    public bool RequireUserConfirmation { get; set; }
    public int? EstimatedDurationSeconds { get; set; }
    public List<PlanningSubTaskDto>? SubTasks { get; set; }
}

/// <summary>计划子任务 DTO（LLM 输出格式）。</summary>
public sealed class PlanningSubTaskDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? AgentTypeDescription { get; set; }
}

/// <summary>步骤执行师子智能体的 JSON 输出 DTO。</summary>
public sealed class StepExecutionResponseDto
{
    public string? Summary { get; set; }
    public string? Detail { get; set; }
}

/// <summary>验证审查员子智能体的 JSON 输出 DTO。</summary>
public sealed class ValidationResponseDto
{
    public bool Passed { get; set; }
    public int Score { get; set; }
    public string? Summary { get; set; }
    public List<string>? Issues { get; set; }
    public List<string>? Suggestions { get; set; }
}
