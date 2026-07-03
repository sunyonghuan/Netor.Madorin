using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Delegation;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Tests.Delegation;

[TestClass]
public sealed class DelegatedAgentJobRunnerTests
{
    [TestMethod]
    public async Task StartAsync_CleansCompletedJobsOlderThanRetention()
    {
        using var db = CreateDatabase();
        var jobService = new DelegatedAgentJobService(db);
        var oldJob = CreateJob("old");
        var recentJob = CreateJob("recent");

        jobService.Create(oldJob);
        jobService.Complete(oldJob.Id, "{\"result\":\"old\"}");
        jobService.Create(recentJob);
        jobService.Complete(recentJob.Id, "{\"result\":\"recent\"}");

        var oldCompletedAt = DateTimeOffset.Now.AddDays(-31).ToUnixTimeMilliseconds();
        SetCompletedAt(db, oldJob.Id, oldCompletedAt);

        var executor = new DelegatedAgentJobExecutor(
            agentFactory: null!,
            providerService: null!,
            modelService: null!,
            jobService,
            toolCatalog: null!,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var runner = new DelegatedAgentJobRunner(executor);

        await runner.StartAsync(CancellationToken.None);

        Assert.IsNull(jobService.GetById(oldJob.Id));
        Assert.IsEmpty(jobService.ListLogs(oldJob.Id));
        Assert.IsNotNull(jobService.GetById(recentJob.Id));
        Assert.IsNotEmpty(jobService.ListLogs(recentJob.Id));
    }

    private static CortanaDbContext CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortana-delegated-agent-runner-tests", $"{Guid.NewGuid():N}.db");
        return new CortanaDbContext(path);
    }

    private static DelegatedAgentJobEntity CreateJob(string suffix) => new()
    {
        Id = $"chatjob_{Guid.NewGuid():N}",
        ScopeKind = "chat",
        ScopeId = $"session-{suffix}",
        ParentAgentId = "expert",
        ChildName = "child",
        ChildInstructions = "instructions",
        TaskInputJson = "{}",
        ToolMountsJson = "[\"sys_read_file\"]"
    };

    private static void SetCompletedAt(CortanaDbContext db, string jobId, long completedAt)
    {
        db.Execute("""
            UPDATE DelegatedAgentJobs
            SET CompletedAt = @CompletedAt,
                UpdatedAt = @CompletedAt
            WHERE Id = @Id
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@CompletedAt", completedAt);
                cmd.Parameters.AddWithValue("@Id", jobId);
            });
    }
}
