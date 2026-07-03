using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Delegation;

/// <summary>
/// 专家模式后台子智能体执行器。
/// </summary>
public sealed class DelegatedAgentJobExecutor(
    AIAgentFactory agentFactory,
    AiProviderService providerService,
    AiModelService modelService,
    DelegatedAgentJobService jobService,
    SystemToolCatalogService toolCatalog,
    ILogger<DelegatedAgentJobExecutor> logger)
{
    private const int MaxRunningJobs = 50;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    public int CleanupCompletedOlderThan(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.Now.Subtract(retention).ToUnixTimeMilliseconds();
        return jobService.CleanupCompletedBefore(cutoff);
    }

    public async Task<string> StartAsync(
        DelegatedAgentJobEntity job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_running.Count >= MaxRunningJobs)
        {
            throw new InvalidOperationException($"后台委派任务数量已达到上限：{MaxRunningJobs}");
        }

        var mountedTools = DeserializeToolMounts(job.ToolMountsJson);
        var missingTools = await toolCatalog
            .ValidateToolMountsAsync(mountedTools, cancellationToken)
            .ConfigureAwait(false);

        if (missingTools.Count > 0)
        {
            throw new InvalidOperationException($"工具不存在，无法挂载：{string.Join(", ", missingTools)}");
        }

        jobService.Create(job);

        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(job.Id, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"后台委派任务已在运行：{job.Id}");
        }

        _ = Task.Run(() => RunAsync(job.Id, cts.Token), CancellationToken.None);
        return job.Id;
    }

    public DelegatedAgentTaskStatusResult GetStatus(string jobId)
    {
        var job = jobService.GetById(jobId)
            ?? throw new InvalidOperationException($"后台委派任务不存在：{jobId}");

        return new DelegatedAgentTaskStatusResult(
            job.Id,
            job.State,
            job.ProgressDescription,
            DateTimeOffset.FromUnixTimeMilliseconds(job.UpdatedAt),
            job.Error);
    }

    public DelegatedAgentTaskResult ReadResult(string jobId)
    {
        var job = jobService.GetById(jobId)
            ?? throw new InvalidOperationException($"后台委派任务不存在：{jobId}");

        var result = job.ResultJson;
        if (!string.IsNullOrWhiteSpace(result))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(result, DelegatedAgentJsonContext.Default.DelegatedAgentTaskResult);
                result = parsed?.Result ?? result;
            }
            catch (JsonException)
            {
                // 兼容纯文本结果。
            }
        }

        return new DelegatedAgentTaskResult(
            job.Id,
            job.State,
            result,
            job.ProgressDescription,
            job.Error);
    }

    public DelegatedAgentTaskCancelResult Cancel(string jobId)
    {
        if (_running.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        var cancelled = jobService.Cancel(jobId);
        return new DelegatedAgentTaskCancelResult(
            jobId,
            cancelled ? DelegatedAgentJobStates.Cancelled : "not_changed",
            cancelled ? "后台子智能体任务已取消。" : "任务不存在或已结束，未执行取消。");
    }

    public void StopAll(string reason)
    {
        foreach (var pair in _running)
        {
            if (_running.TryRemove(pair.Key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        jobService.MarkAllActiveAsFailed(reason);
    }

    private async Task RunAsync(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            var job = jobService.GetById(jobId)
                ?? throw new InvalidOperationException($"后台委派任务不存在：{jobId}");

            jobService.SetRunning(jobId, "正在构建后台子智能体。");

            var provider = ResolveProvider(job.ProviderId);
            var model = ResolveModel(job.ModelId, provider.Id);
            var mountedTools = DeserializeToolMounts(job.ToolMountsJson);
            var input = JsonSerializer.Deserialize(job.TaskInputJson, DelegatedAgentJsonContext.Default.DelegatedAgentTaskInput)
                ?? throw new InvalidOperationException("后台委派任务输入无效。");

            var childAgent = new AgentEntity
            {
                Id = $"delegated-{job.Id}",
                Name = string.IsNullOrWhiteSpace(job.ChildName) ? "delegated-subagent" : job.ChildName,
                Description = "专家模式临时后台子智能体",
                Instructions = BuildChildInstructions(job.ChildInstructions, input.OutputContract),
                BoundPlugins = [],
                BoundMcp = []
            };

            var subAgent = agentFactory.BuildDelegatedSubAgent(childAgent, provider, model, mountedTools);
            jobService.UpdateProgress(jobId, "后台子智能体正在执行任务。");

            var contents = await BuildMessageContentsAsync(input, cancellationToken).ConfigureAwait(false);
            var message = new ChatMessage(ChatRole.User, contents);
            var resultParts = new List<string>();

            await foreach (var chunk in subAgent.RunStreamingAsync([message], cancellationToken: cancellationToken))
            {
                foreach (var content in chunk.Contents)
                {
                    if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                    {
                        resultParts.Add(text.Text);
                        jobService.UpdateProgress(jobId, "后台子智能体正在输出结果。");
                    }
                }
            }

            var resultText = string.Concat(resultParts).Trim();
            var result = new DelegatedAgentTaskResult(
                jobId,
                DelegatedAgentJobStates.Completed,
                resultText,
                "已完成",
                null);

            jobService.Complete(
                jobId,
                JsonSerializer.Serialize(result, DelegatedAgentJsonContext.Default.DelegatedAgentTaskResult));
        }
        catch (OperationCanceledException)
        {
            jobService.Cancel(jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "后台委派任务执行失败：{JobId}", jobId);
            jobService.Fail(jobId, ex.Message);
        }
        finally
        {
            if (_running.TryRemove(jobId, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    private AiProviderEntity ResolveProvider(string? providerId)
    {
        if (!string.IsNullOrWhiteSpace(providerId) && providerService.GetById(providerId) is { } provider)
        {
            return provider;
        }

        return providerService.GetAll().FirstOrDefault(static provider => provider.IsEnabled)
            ?? providerService.GetAll().FirstOrDefault()
            ?? throw new InvalidOperationException("没有可用的 AI 提供商。");
    }

    private AiModelEntity ResolveModel(string? modelId, string providerId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && modelService.GetById(modelId) is { } model)
        {
            return model;
        }

        return modelService.GetByProviderId(providerId).FirstOrDefault(static model => model.IsEnabled)
            ?? modelService.GetByProviderId(providerId).FirstOrDefault()
            ?? throw new InvalidOperationException($"提供商 {providerId} 没有可用模型。");
    }

    private static string[] DeserializeToolMounts(string toolMountsJson)
    {
        if (string.IsNullOrWhiteSpace(toolMountsJson))
        {
            return [];
        }

        return JsonSerializer.Deserialize(toolMountsJson, DelegatedAgentJsonContext.Default.StringArray) ?? [];
    }

    private static string BuildChildInstructions(string childInstructions, string? outputContract)
    {
        var instructions = string.IsNullOrWhiteSpace(childInstructions)
            ? "你是一个后台任务子智能体，负责独立完成主智能体委派的工作，并输出可供主智能体转述的结果。"
            : childInstructions.Trim();

        if (!string.IsNullOrWhiteSpace(outputContract))
        {
            instructions += $"\n\n输出要求：{outputContract.Trim()}";
        }

        return instructions;
    }

    private static async Task<List<AIContent>> BuildMessageContentsAsync(
        DelegatedAgentTaskInput input,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent> { new TextContent(input.Task) };
        foreach (var path in input.AttachmentPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var mime = GuessMimeFromPath(path);
            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                contents.Add(await DataContent.LoadFromAsync(path, mime, cancellationToken: cancellationToken).ConfigureAwait(false));
            }

            contents.Add(new TextContent($"附件：{path}"));
        }

        return contents;
    }

    private static string GuessMimeFromPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }
}
