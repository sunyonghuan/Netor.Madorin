using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.Background;

/// <summary>
/// 子智能体背景任务执行器。
/// </summary>
public sealed class SubAgentJobExecutor : IBackgroundJobExecutor
{
    private readonly AIAgentFactory _agentFactory;
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly WorkBackgroundJobsService _jobsService;
    private readonly WorkExecutionLogService _logService;
    private readonly IPublisher _publisher;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public SubAgentJobExecutor(
        AIAgentFactory agentFactory,
        AgentService agentService,
        AiProviderService providerService,
        AiModelService modelService,
        WorkBackgroundJobsService jobsService,
        WorkExecutionLogService logService,
        IPublisher publisher)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _jobsService = jobsService ?? throw new ArgumentNullException(nameof(jobsService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public string JobKind => WorkBackgroundJobKinds.SubAgent;

    public Task<string> StartAsync(BackgroundJobStartRequest request, CancellationToken cancellationToken = default)
    {
        var input = JsonSerializer.Deserialize(request.InputJson, WorkModeJsonContext.Default.SubAgentJobInput)
            ?? throw new InvalidOperationException("子智能体背景任务参数无效。");

        var agent = _agentService.GetByName(input.AgentId)
            ?? throw new InvalidOperationException($"子智能体不存在：{input.AgentId}");

        var jobId = Guid.NewGuid().ToString("N");
        _jobsService.Create(new WorkBackgroundJobEntity
        {
            Id = jobId,
            TaskId = request.TaskId,
            JobKind = JobKind,
            OwnerName = agent.Name,
            InputJson = request.InputJson,
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[jobId] = cts;
        _ = Task.Run(() => RunJobAsync(jobId, request.TaskId, agent, input, cts.Token), CancellationToken.None);

        return Task.FromResult(jobId);
    }

    public async Task<BackgroundJobStatus> WaitForAsync(string jobId, int maxSeconds, CancellationToken cancellationToken = default)
    {
        var delaySeconds = Math.Clamp(maxSeconds, 1, 60);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = GetStatus(jobId);
            if (status.State is BackgroundJobState.Completed or BackgroundJobState.Failed or BackgroundJobState.Cancelled)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return GetStatus(jobId);
    }

    public Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_running.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        _jobsService.SetCancelled(jobId);
        return Task.CompletedTask;
    }

    private async Task RunJobAsync(
        string jobId,
        string taskId,
        AgentEntity agent,
        SubAgentJobInput input,
        CancellationToken cancellationToken)
    {
        using var nonExpertScope = NonExpertProcessScope.Enter();

        try
        {
            _jobsService.SetRunning(jobId);
            _jobsService.UpdateProgress(jobId, "子智能体已启动。");
            var startRecord = new WorkExecutionLogRecords.SubAgentJobStart(jobId, agent.Name, input.Query);
            _logService.Append(taskId, WorkExecutionLogTypes.SubAgentJobStart,
                JsonSerializer.Serialize(startRecord, WorkModeJsonContext.Default.SubAgentJobStart));
            await _publisher.PublishAsync(Events.OnWorkSubAgentJobStarted, new WorkSubAgentJobStartedArgs(taskId, jobId, agent.Name, input.Query));

            var mainProvider = ResolveProvider(input.MainProviderId);
            var mainModel = ResolveModel(input.MainModelId);
            var (provider, model) = AIAgentFactory.ResolveSubAgentProviderAndModel(agent, mainProvider, mainModel, _providerService, _modelService);
            if (provider is null || model is null)
            {
                throw new InvalidOperationException($"子智能体 {agent.Name} 缺少有效模型配置。");
            }

            var subAgent = _agentFactory.BuildWorkModeSubAgent(agent, provider, model);
            var contents = await BuildMessageContentsAsync(input, cancellationToken);
            var message = new ChatMessage(ChatRole.User, contents);
            var streamProcessor = new WorkModeStreamProcessor(
                taskId,
                _publisher,
                _logService,
                authorName: agent.Name,
                callIdPrefix: $"job_{jobId}");

            await foreach (var chunk in subAgent.RunStreamingAsync([message], cancellationToken: cancellationToken))
            {
                if (chunk.Contents.Count <= 0)
                {
                    continue;
                }

                await streamProcessor.ProcessChunkAsync(chunk.Contents, cancellationToken);
                _jobsService.UpdateProgress(jobId, "子智能体正在输出。");
                await _publisher.PublishAsync(
                    Events.OnWorkSubAgentJobProgress,
                    new WorkSubAgentJobProgressArgs(taskId, jobId, "子智能体正在输出。"));
            }

            await streamProcessor.FlushAsync(cancellationToken);
            var result = streamProcessor.GetAccumulatedText();
            var resultJson = JsonSerializer.Serialize(
                new BackgroundJobStatus(jobId, BackgroundJobState.Completed, "已完成", result, null),
                WorkModeJsonContext.Default.BackgroundJobStatus);

            _jobsService.SetCompleted(jobId, resultJson);
            var endRecord = new WorkExecutionLogRecords.SubAgentJobEnd(jobId, WorkBackgroundJobStates.Completed, resultJson, null);
            _logService.Append(taskId, WorkExecutionLogTypes.SubAgentJobEnd,
                JsonSerializer.Serialize(endRecord, WorkModeJsonContext.Default.SubAgentJobEnd));
            await _publisher.PublishAsync(Events.OnWorkSubAgentJobCompleted, new WorkSubAgentJobCompletedArgs(taskId, jobId, resultJson));
        }
        catch (OperationCanceledException)
        {
            _jobsService.SetCancelled(jobId);
            var endRecord = new WorkExecutionLogRecords.SubAgentJobEnd(jobId, WorkBackgroundJobStates.Cancelled, null, "任务已取消。");
            _logService.Append(taskId, WorkExecutionLogTypes.SubAgentJobEnd,
                JsonSerializer.Serialize(endRecord, WorkModeJsonContext.Default.SubAgentJobEnd));
            await _publisher.PublishAsync(Events.OnWorkSubAgentJobFailed, new WorkSubAgentJobFailedArgs(taskId, jobId, "任务已取消。"));
        }
        catch (Exception ex)
        {
            _jobsService.SetFailed(jobId, ex.Message);
            var endRecord = new WorkExecutionLogRecords.SubAgentJobEnd(jobId, WorkBackgroundJobStates.Failed, null, ex.Message);
            _logService.Append(taskId, WorkExecutionLogTypes.SubAgentJobEnd,
                JsonSerializer.Serialize(endRecord, WorkModeJsonContext.Default.SubAgentJobEnd));
            await _publisher.PublishAsync(Events.OnWorkSubAgentJobFailed, new WorkSubAgentJobFailedArgs(taskId, jobId, ex.Message));
        }
        finally
        {
            if (_running.TryRemove(jobId, out var cts))
            {
                cts.Dispose();
            }
        }
    }

    private BackgroundJobStatus GetStatus(string jobId)
    {
        var job = _jobsService.GetById(jobId)
            ?? throw new InvalidOperationException($"背景任务不存在：{jobId}");

        return new BackgroundJobStatus(
            job.Id,
            ParseState(job.State),
            job.ProgressDescription,
            job.ResultJson,
            job.Error);
    }

    private AiProviderEntity ResolveProvider(string? providerId)
    {
        if (!string.IsNullOrWhiteSpace(providerId) && _providerService.GetById(providerId) is { } provider)
        {
            return provider;
        }

        return _providerService.GetAll().FirstOrDefault()
            ?? throw new InvalidOperationException("没有可用的 AI 提供商。");
    }

    private AiModelEntity ResolveModel(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && _modelService.GetById(modelId) is { } model)
        {
            return model;
        }

        var provider = ResolveProvider(null);
        return _modelService.GetByProviderId(provider.Id).FirstOrDefault()
            ?? throw new InvalidOperationException($"提供商 {provider.Name} 没有可用模型。");
    }

    private static async Task<List<AIContent>> BuildMessageContentsAsync(SubAgentJobInput input, CancellationToken cancellationToken)
    {
        var contents = new List<AIContent> { new TextContent(input.Query) };
        if (input.AttachmentPaths is not { Length: > 0 })
        {
            return contents;
        }

        for (var i = 0; i < input.AttachmentPaths.Length; i++)
        {
            var path = input.AttachmentPaths[i];
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var description = input.AttachmentDescriptions is { Length: > 0 } && i < input.AttachmentDescriptions.Length
                ? input.AttachmentDescriptions[i]
                : Path.GetFileName(path);

            var mime = GuessMimeFromPath(path);
            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                contents.Add(await DataContent.LoadFromAsync(path, mime, cancellationToken: cancellationToken));
            }

            contents.Add(new TextContent($" [{description}]({path}) "));
        }

        return contents;
    }

    private static BackgroundJobState ParseState(string state) => state switch
    {
        WorkBackgroundJobStates.Pending => BackgroundJobState.Pending,
        WorkBackgroundJobStates.Running => BackgroundJobState.Running,
        WorkBackgroundJobStates.Completed => BackgroundJobState.Completed,
        WorkBackgroundJobStates.Failed => BackgroundJobState.Failed,
        WorkBackgroundJobStates.Cancelled => BackgroundJobState.Cancelled,
        _ => BackgroundJobState.Failed,
    };

    private static string GuessMimeFromPath(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
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
