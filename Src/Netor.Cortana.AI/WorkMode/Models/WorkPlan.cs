using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.WorkMode.Models;

/// <summary>
/// 工作模式的工作计划。由 set_plan 工具写入 WorkTasks.CurrentPlanJson。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §3.1。
/// </summary>
/// <param name="MainSteps">主步骤列表（顺序执行）。</param>
public sealed record WorkPlan(
    [property: JsonPropertyName("main_steps")] IReadOnlyList<WorkPlanMainStep> MainSteps);

/// <summary>主步骤，包含若干子步骤。</summary>
/// <param name="Title">主步骤标题（在时间线主步骤标题行显示）。</param>
/// <param name="SubSteps">子步骤列表。</param>
public sealed record WorkPlanMainStep(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("sub_steps")] IReadOnlyList<WorkPlanSubStep> SubSteps);

/// <summary>子步骤。</summary>
/// <param name="Title">子步骤标题。</param>
/// <param name="AcceptanceCriteria">该子步骤的验收标准（self_evaluate / verify_step 时使用）。</param>
public sealed record WorkPlanSubStep(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("acceptance_criteria")] string AcceptanceCriteria);
