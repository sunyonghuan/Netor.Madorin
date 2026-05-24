using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.TaskEngine.Persistence;

/// <summary>
/// 任务文件路径解析器。
/// 所有文件路径都基于 Run ID 隔离，保证不会跨执行混淆。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/05-P4补充-上下文传递与恢复机制.md §4.3 / §4.8。
///
/// 目录结构：
/// <code>
/// {WorkspaceDir}/
///   .cortana/tasks/
///     {taskId}/
///       task.json                    ← 任务元信息
///       latest-run                   ← 文本文件，指向最新 run 目录名
///       templates/                   ← 模板目录（跨 run 共享）
///         {templateId}.json
///       runs/
///         run-{yyyyMMdd}-{HHmmss}-{hex}/
///           run.json                 ← 本次运行元信息
///           plan.json                ← 执行计划（JSON）
///           plan.md                  ← 执行计划（Markdown 摘要）
///           checkpoint.json          ← 断点检查点
///           requirements.json        ← 需求分析结果
///           steps/
///             {stepId}/
///               intent.json          ← WAL 执行意图标记
///               result.json          ← 步骤结果
///               result.summary.txt   ← 结果摘要（给后续步骤 + UI）
///               workspace/           ← 子智能体中间产物目录
/// </code>
/// </summary>
public sealed class TaskFileResolver
{
    private readonly string _workspaceDir;

    public TaskFileResolver(IAppPaths appPaths)
    {
        _workspaceDir = appPaths.WorkspaceDirectory;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 任务级路径
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>任务根目录：{workspace}/.cortana/tasks</summary>
    public string GetTasksRootDir()
        => Path.Combine(_workspaceDir, ".cortana", "tasks");

    /// <summary>单个任务目录：{root}/{taskId}</summary>
    public string GetTaskDir(string taskId)
        => Path.Combine(GetTasksRootDir(), taskId);

    /// <summary>任务元信息文件：{taskDir}/task.json</summary>
    public string GetTaskMetaPath(string taskId)
        => Path.Combine(GetTaskDir(taskId), "task.json");

    // ══════════════════════════════════════════════════════════════════════
    // Run 级路径
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>运行列表目录：{taskDir}/runs</summary>
    public string GetRunsDir(string taskId)
        => Path.Combine(GetTaskDir(taskId), "runs");

    /// <summary>单次运行目录：{taskDir}/runs/{runId}</summary>
    public string GetRunDir(string taskId, string runId)
        => Path.Combine(GetRunsDir(taskId), runId);

    /// <summary>运行元信息文件：runs/{runId}/run.json</summary>
    public string GetRunMetaPath(string taskId, string runId)
        => Path.Combine(GetRunDir(taskId, runId), "run.json");

    /// <summary>执行计划 JSON：runs/{runId}/plan.json</summary>
    public string GetPlanJsonPath(string taskId, string runId)
        => Path.Combine(GetRunDir(taskId, runId), "plan.json");

    /// <summary>执行计划 Markdown 摘要：runs/{runId}/plan.md</summary>
    public string GetPlanMdPath(string taskId, string runId)
        => Path.Combine(GetRunDir(taskId, runId), "plan.md");

    /// <summary>断点检查点：runs/{runId}/checkpoint.json</summary>
    public string GetCheckpointPath(string taskId, string runId)
        => Path.Combine(GetRunDir(taskId, runId), "checkpoint.json");

    /// <summary>需求分析结果：runs/{runId}/requirements.json</summary>
    public string GetRequirementsPath(string taskId, string runId)
        => Path.Combine(GetRunDir(taskId, runId), "requirements.json");

    // ══════════════════════════════════════════════════════════════════════
    // 步骤级路径
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>步骤目录：runs/{runId}/steps/{stepId}</summary>
    public string GetStepDir(string taskId, string runId, string stepId)
        => Path.Combine(GetRunDir(taskId, runId), "steps", stepId);

    /// <summary>WAL 执行意图标记：steps/{stepId}/intent.json</summary>
    public string GetStepIntentPath(string taskId, string runId, string stepId)
        => Path.Combine(GetStepDir(taskId, runId, stepId), "intent.json");

    /// <summary>步骤结果：steps/{stepId}/result.json</summary>
    public string GetStepResultPath(string taskId, string runId, string stepId)
        => Path.Combine(GetStepDir(taskId, runId, stepId), "result.json");

    /// <summary>步骤结果摘要：steps/{stepId}/result.summary.txt</summary>
    public string GetStepSummaryPath(string taskId, string runId, string stepId)
        => Path.Combine(GetStepDir(taskId, runId, stepId), "result.summary.txt");

    /// <summary>子智能体中间产物目录：steps/{stepId}/workspace</summary>
    public string GetStepWorkspaceDir(string taskId, string runId, string stepId)
        => Path.Combine(GetStepDir(taskId, runId, stepId), "workspace");

    // ══════════════════════════════════════════════════════════════════════
    // Run 追踪（latest-run 文件）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 读取 latest-run 指向的 runId。
    /// 返回 null 表示没有任何执行记录。
    /// </summary>
    public string? GetLatestRunId(string taskId)
    {
        var path = Path.Combine(GetTaskDir(taskId), "latest-run");
        if (!File.Exists(path)) return null;

        var content = File.ReadAllText(path).Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }

    /// <summary>更新 latest-run 指向新的 runId。</summary>
    public void SetLatestRunId(string taskId, string runId)
    {
        var dir = GetTaskDir(taskId);
        EnsureDirectoryExists(dir);
        var path = Path.Combine(dir, "latest-run");
        File.WriteAllText(path, runId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 模板路径（任务级，跨 run 共享）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>模板目录：{taskDir}/templates</summary>
    public string GetTemplatesDir(string taskId)
        => Path.Combine(GetTaskDir(taskId), "templates");

    /// <summary>模板文件：{taskDir}/templates/{templateId}.json</summary>
    public string GetTemplatePath(string taskId, string templateId)
        => Path.Combine(GetTemplatesDir(taskId), $"{templateId}.json");

    // ══════════════════════════════════════════════════════════════════════
    // 扫描辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>列出所有任务 ID（扫描 tasks 根目录下的子目录名）。</summary>
    public IReadOnlyList<string> ListTaskIds()
    {
        var root = GetTasksRootDir();
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray()!;
    }

    /// <summary>列出某任务下所有 Run ID（扫描 runs 目录下的子目录名，按时间倒序）。</summary>
    public IReadOnlyList<string> ListRunIds(string taskId)
    {
        var runsDir = GetRunsDir(taskId);
        if (!Directory.Exists(runsDir)) return [];
        return Directory.GetDirectories(runsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && n!.StartsWith("run-", StringComparison.Ordinal))
            .OrderByDescending(n => n)  // run-{yyyyMMdd}-{HHmmss} 自然排序即时间倒序
            .ToArray()!;
    }

    /// <summary>确保目录存在。</summary>
    public static void EnsureDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }
}
