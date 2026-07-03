using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.ProjectLead;

/// <summary>
/// B 层项目组长看门狗，负责重启心跳超时的 running 编排器。
/// </summary>
public sealed class ProjectLeadWatchdogService : IHostedService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);

    private readonly WorkTaskService _taskService;
    private readonly ProjectLeadService _projectLeadService;
    private readonly ILogger<ProjectLeadWatchdogService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleAfter;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ProjectLeadWatchdogService(
        WorkTaskService taskService,
        ProjectLeadService projectLeadService,
        ILogger<ProjectLeadWatchdogService> logger)
        : this(taskService, projectLeadService, logger, DefaultInterval, DefaultStaleAfter)
    {
    }

    public ProjectLeadWatchdogService(
        WorkTaskService taskService,
        ProjectLeadService projectLeadService,
        ILogger<ProjectLeadWatchdogService> logger,
        TimeSpan interval,
        TimeSpan staleAfter)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _projectLeadService = projectLeadService ?? throw new ArgumentNullException(nameof(projectLeadService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval;
        _staleAfter = staleAfter;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
        }
    }

    public async Task<int> ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        var staleBefore = DateTimeOffset.Now.Subtract(_staleAfter).ToUnixTimeMilliseconds();
        var staleTasks = _taskService.ListStaleRunningOrchestrators(staleBefore);
        foreach (var task in staleTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartTask(task.Id);
        }

        return staleTasks.Count;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "项目组长看门狗扫描失败。");
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void RestartTask(string taskId)
    {
        _taskService.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
        _logger.LogWarning("项目组长看门狗检测到心跳超时，准备重启：TaskId={TaskId}", taskId);

        _ = Task.Run(async () =>
        {
            using var nonExpertScope = NonExpertProcessScope.Enter();
            try
            {
                await _projectLeadService.RunAsync(taskId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "项目组长看门狗重启失败：TaskId={TaskId}", taskId);
                _taskService.RecordExecutionFailed(taskId, ex.Message);
            }
        }, CancellationToken.None);
    }
}
