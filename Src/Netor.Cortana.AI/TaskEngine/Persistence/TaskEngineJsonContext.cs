using System.Text.Json.Serialization;

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
