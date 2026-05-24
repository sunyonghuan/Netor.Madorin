using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine.Persistence;

/// <summary>
/// 计划持久化接口。
/// 落盘策略：JSON 文件（程序恢复用） + Markdown 文件（用户查看用）。
/// P4-1 仅定义接口，P4-2 提供实现。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §7.2。
/// </summary>
public interface IPlanPersistence
{
    /// <summary>保存/更新计划（同时写 JSON + 生成 Markdown 摘要）。</summary>
    Task SavePlanAsync(ExecutionPlan plan, CancellationToken ct);

    /// <summary>加载计划（从 JSON 恢复）。</summary>
    Task<ExecutionPlan?> LoadPlanAsync(string taskId, CancellationToken ct);

    /// <summary>保存断点（执行状态快照，用于进程重启恢复）。</summary>
    Task SaveCheckpointAsync(string taskId, ExecutionCheckpoint checkpoint, CancellationToken ct);

    /// <summary>加载断点。</summary>
    Task<ExecutionCheckpoint?> LoadCheckpointAsync(string taskId, CancellationToken ct);

    /// <summary>保存步骤结果详情。</summary>
    Task SaveStepResultAsync(string taskId, string stepId, StepResult result, CancellationToken ct);

    /// <summary>加载步骤结果详情。</summary>
    Task<StepResult?> LoadStepResultAsync(string taskId, string stepId, CancellationToken ct);

    /// <summary>保存需求分析结果。</summary>
    Task SaveRequirementsAsync(string taskId, RequirementsAnalysis requirements, CancellationToken ct);

    /// <summary>加载需求分析结果。</summary>
    Task<RequirementsAnalysis?> LoadRequirementsAsync(string taskId, CancellationToken ct);

    /// <summary>保存执行模板。</summary>
    Task SaveTemplateAsync(ExecutionTemplate template, CancellationToken ct);

    /// <summary>加载执行模板。</summary>
    Task<ExecutionTemplate?> LoadTemplateAsync(string templateId, CancellationToken ct);

    /// <summary>列出工作区下的所有模板。</summary>
    Task<IReadOnlyList<ExecutionTemplate>> ListTemplatesAsync(string workspaceId, CancellationToken ct);

    // ──── Run 生命周期（P4-2 新增） ────

    /// <summary>
    /// 创建新的执行运行。生成 Run ID + 创建目录结构 + 写入 task.json / run.json / latest-run。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="taskTitle">任务标题（写入 task.json）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新生成的 Run ID。</returns>
    Task<string> CreateRunAsync(string taskId, string taskTitle, CancellationToken ct);

    /// <summary>
    /// 获取任务的当前活跃 Run ID（从内存缓存或 latest-run 文件）。
    /// 返回 null 表示该任务无活跃 run。
    /// </summary>
    string? GetActiveRunId(string taskId);

    /// <summary>列出所有任务 ID（用于启动时断点恢复扫描）。</summary>
    IReadOnlyList<string> ListTaskIds();

    // ──── TaskMeta CRUD（P4 新增） ────

    /// <summary>加载任务元信息。</summary>
    Task<TaskMeta?> LoadTaskMetaAsync(string taskId, CancellationToken ct);

    /// <summary>保存/更新任务元信息。</summary>
    Task SaveTaskMetaAsync(TaskMeta meta, CancellationToken ct);

    /// <summary>列出所有任务的元信息（按创建时间倒序）。</summary>
    Task<IReadOnlyList<TaskMeta>> ListTaskMetasAsync(CancellationToken ct);

    /// <summary>删除任务及其所有运行数据。</summary>
    Task DeleteTaskAsync(string taskId, CancellationToken ct);
}
