using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.TaskEngine.Models;

namespace Netor.Cortana.AI.TaskEngine.Persistence;

/// <summary>
/// <see cref="IPlanPersistence"/> 的文件系统实现。
/// 所有数据以 JSON 文件落盘，按 Run ID 隔离。
/// 写入采用 temp-file-then-rename 原子模式，防止崩溃导致文件损坏。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §7 +
///      docs/未来版本策划/聊天式任务发起与动态智能体/05-P4补充-上下文传递与恢复机制.md §3-4。
/// </summary>
public sealed class FilePlanPersistence : IPlanPersistence
{
    private readonly TaskFileResolver _resolver;
    private readonly ILogger<FilePlanPersistence> _logger;

    /// <summary>taskId → 当前活跃 runId 的内存缓存。</summary>
    private readonly ConcurrentDictionary<string, string> _activeRuns = new();

    public FilePlanPersistence(TaskFileResolver resolver, ILogger<FilePlanPersistence> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Run 生命周期
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task<string> CreateRunAsync(string taskId, string taskTitle, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = RunIdGenerator.Generate();

        // 确保目录结构
        var runDir = _resolver.GetRunDir(taskId, runId);
        TaskFileResolver.EnsureDirectoryExists(runDir);

        // 写入 task.json（任务级元信息，幂等写入）
        var taskMeta = new TaskMeta
        {
            TaskId = taskId,
            Title = taskTitle,
            Status = "running",
            LatestRunId = runId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastModifiedAt = DateTimeOffset.UtcNow,
        };
        WriteJsonAtomic(_resolver.GetTaskMetaPath(taskId), taskMeta, TaskEngineJsonContext.Default.TaskMeta);

        // 写入 run.json
        var runMeta = new RunMeta
        {
            RunId = runId,
            TaskId = taskId,
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
        };
        WriteJsonAtomic(_resolver.GetRunMetaPath(taskId, runId), runMeta, TaskEngineJsonContext.Default.RunMeta);

        // 更新 latest-run
        _resolver.SetLatestRunId(taskId, runId);

        // 更新内存缓存
        _activeRuns[taskId] = runId;

        _logger.LogInformation("P4 已创建执行运行: {TaskId}/{RunId}", taskId, runId);
        return Task.FromResult(runId);
    }

    /// <inheritdoc/>
    public string? GetActiveRunId(string taskId)
    {
        if (_activeRuns.TryGetValue(taskId, out var cached))
            return cached;

        // 回退到文件系统
        var runId = _resolver.GetLatestRunId(taskId);
        if (runId is not null)
            _activeRuns[taskId] = runId;

        return runId;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ListTaskIds() => _resolver.ListTaskIds();

    // ══════════════════════════════════════════════════════════════════════
    // TaskMeta CRUD
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task<TaskMeta?> LoadTaskMetaAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = _resolver.GetTaskMetaPath(taskId);
        return Task.FromResult(ReadJsonOrNull<TaskMeta>(path, TaskEngineJsonContext.Default.TaskMeta));
    }

    /// <inheritdoc/>
    public Task SaveTaskMetaAsync(TaskMeta meta, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = _resolver.GetTaskMetaPath(meta.TaskId);
        WriteJsonAtomic(path, meta, TaskEngineJsonContext.Default.TaskMeta);
        _logger.LogDebug("P4 任务元信息已保存: {TaskId}", meta.TaskId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TaskMeta>> ListTaskMetasAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var metas = new List<TaskMeta>();
        foreach (var taskId in _resolver.ListTaskIds())
        {
            var path = _resolver.GetTaskMetaPath(taskId);
            var meta = ReadJsonOrNull<TaskMeta>(path, TaskEngineJsonContext.Default.TaskMeta);
            if (meta is not null)
                metas.Add(meta);
        }

        // 按创建时间倒序
        metas.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return Task.FromResult<IReadOnlyList<TaskMeta>>(metas);
    }

    /// <inheritdoc/>
    public Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var taskDir = _resolver.GetTaskDir(taskId);
        if (Directory.Exists(taskDir))
            Directory.Delete(taskDir, recursive: true);
        _activeRuns.TryRemove(taskId, out _);
        _logger.LogInformation("P4 任务已删除: {TaskId}", taskId);
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Plan
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task SavePlanAsync(ExecutionPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(plan.TaskId);
        var jsonPath = _resolver.GetPlanJsonPath(plan.TaskId, runId);
        var mdPath = _resolver.GetPlanMdPath(plan.TaskId, runId);

        TaskFileResolver.EnsureDirectoryExists(Path.GetDirectoryName(jsonPath)!);

        // 写入 plan.json
        WriteJsonAtomic(jsonPath, plan, TaskEngineJsonContext.Default.ExecutionPlan);

        // 同步生成 plan.md
        var markdown = PlanMarkdownGenerator.Generate(plan);
        WriteTextAtomic(mdPath, markdown);

        _logger.LogDebug("P4 计划已保存: {TaskId}/{RunId} v{Version}", plan.TaskId, runId, plan.Version);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ExecutionPlan?> LoadPlanAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetPlanJsonPath(taskId, runId);

        return Task.FromResult(ReadJsonOrNull<ExecutionPlan>(path, TaskEngineJsonContext.Default.ExecutionPlan));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Checkpoint
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task SaveCheckpointAsync(string taskId, ExecutionCheckpoint checkpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetCheckpointPath(taskId, runId);

        TaskFileResolver.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
        WriteJsonAtomic(path, checkpoint, TaskEngineJsonContext.Default.ExecutionCheckpoint);

        _logger.LogDebug("P4 检查点已保存: {TaskId}/{RunId} phase={Phase}", taskId, runId, checkpoint.CurrentPhase);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ExecutionCheckpoint?> LoadCheckpointAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetCheckpointPath(taskId, runId);

        return Task.FromResult(ReadJsonOrNull<ExecutionCheckpoint>(path, TaskEngineJsonContext.Default.ExecutionCheckpoint));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Step Result
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task SaveStepResultAsync(string taskId, string stepId, StepResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var stepDir = _resolver.GetStepDir(taskId, runId, stepId);
        TaskFileResolver.EnsureDirectoryExists(stepDir);

        // 写入 result.json
        var resultPath = _resolver.GetStepResultPath(taskId, runId, stepId);
        WriteJsonAtomic(resultPath, result, TaskEngineJsonContext.Default.StepResult);

        // 同步写入 result.summary.txt（给后续步骤 + UI 用）
        var summaryPath = _resolver.GetStepSummaryPath(taskId, runId, stepId);
        WriteTextAtomic(summaryPath, result.Summary);

        _logger.LogDebug("P4 步骤结果已保存: {TaskId}/{RunId}/{StepId}", taskId, runId, stepId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<StepResult?> LoadStepResultAsync(string taskId, string stepId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetStepResultPath(taskId, runId, stepId);

        return Task.FromResult(ReadJsonOrNull<StepResult>(path, TaskEngineJsonContext.Default.StepResult));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Requirements
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task SaveRequirementsAsync(string taskId, RequirementsAnalysis requirements, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetRequirementsPath(taskId, runId);

        TaskFileResolver.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
        WriteJsonAtomic(path, requirements, TaskEngineJsonContext.Default.RequirementsAnalysis);

        _logger.LogDebug("P4 需求分析已保存: {TaskId}/{RunId}", taskId, runId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<RequirementsAnalysis?> LoadRequirementsAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetRequirementsPath(taskId, runId);

        return Task.FromResult(ReadJsonOrNull<RequirementsAnalysis>(path, TaskEngineJsonContext.Default.RequirementsAnalysis));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Validation Result
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task SaveValidationResultAsync(string taskId, ValidationResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetValidationResultPath(taskId, runId);

        TaskFileResolver.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
        WriteJsonAtomic(path, result, TaskEngineJsonContext.Default.ValidationResult);

        _logger.LogDebug("P4 验证结果已保存: {TaskId}/{RunId}", taskId, runId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ValidationResult?> LoadValidationResultAsync(string taskId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runId = ResolveRunId(taskId);
        var path = _resolver.GetValidationResultPath(taskId, runId);

        return Task.FromResult(ReadJsonOrNull<ValidationResult>(path, TaskEngineJsonContext.Default.ValidationResult));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Template（任务级，跨 run 共享）
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public Task SaveTemplateAsync(ExecutionTemplate template, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 模板基于 SourceTaskId 存储
        var taskId = template.SourceTaskId;
        if (string.IsNullOrEmpty(taskId))
            throw new ArgumentException("ExecutionTemplate.SourceTaskId 不能为空", nameof(template));

        var dir = _resolver.GetTemplatesDir(taskId);
        TaskFileResolver.EnsureDirectoryExists(dir);

        var path = _resolver.GetTemplatePath(taskId, template.TemplateId);
        WriteJsonAtomic(path, template, TaskEngineJsonContext.Default.ExecutionTemplate);

        _logger.LogDebug("P4 模板已保存: {TaskId}/{TemplateId}", taskId, template.TemplateId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ExecutionTemplate?> LoadTemplateAsync(string templateId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 模板 ID 全局唯一，需要扫描所有任务目录查找
        foreach (var taskId in _resolver.ListTaskIds())
        {
            var path = _resolver.GetTemplatePath(taskId, templateId);
            var template = ReadJsonOrNull<ExecutionTemplate>(path, TaskEngineJsonContext.Default.ExecutionTemplate);
            if (template is not null)
                return Task.FromResult<ExecutionTemplate?>(template);
        }

        return Task.FromResult<ExecutionTemplate?>(null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ExecutionTemplate>> ListTemplatesAsync(string workspaceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var templates = new List<ExecutionTemplate>();

        // 扫描所有任务目录下的 templates/ 子目录
        foreach (var taskId in _resolver.ListTaskIds())
        {
            var templatesDir = _resolver.GetTemplatesDir(taskId);
            if (!Directory.Exists(templatesDir)) continue;

            foreach (var filePath in Directory.GetFiles(templatesDir, "*.json"))
            {
                var template = ReadJsonOrNull<ExecutionTemplate>(filePath, TaskEngineJsonContext.Default.ExecutionTemplate);
                if (template is not null && (string.IsNullOrEmpty(workspaceId) || template.WorkspaceId == workspaceId))
                {
                    templates.Add(template);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ExecutionTemplate>>(templates);
    }

    // ══════════════════════════════════════════════════════════════════════
    // 内部辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>解析当前活跃 Run ID，不存在时抛出。</summary>
    private string ResolveRunId(string taskId)
    {
        var runId = GetActiveRunId(taskId);
        if (runId is null)
            throw new InvalidOperationException($"任务 {taskId} 无活跃的执行运行，请先调用 CreateRunAsync");
        return runId;
    }

    /// <summary>
    /// 原子写入 JSON 文件：先写 .tmp，再 rename 覆盖。
    /// 防止崩溃时写到一半导致文件损坏。
    /// </summary>
    private static void WriteJsonAtomic<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        TaskFileResolver.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(value, typeInfo);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>原子写入文本文件。</summary>
    private static void WriteTextAtomic(string path, string content)
    {
        TaskFileResolver.EnsureDirectoryExists(Path.GetDirectoryName(path)!);
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, content);
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>
    /// 读取 JSON 文件并反序列化。文件不存在或反序列化失败时返回 null。
    /// </summary>
    private T? ReadJsonOrNull<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "P4 JSON 反序列化失败，文件可能损坏: {Path}", path);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "P4 文件读取失败: {Path}", path);
            return null;
        }
    }
}
