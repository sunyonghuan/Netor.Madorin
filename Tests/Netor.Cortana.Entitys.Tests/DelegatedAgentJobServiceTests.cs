using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Entitys.Tests;

[TestClass]
public sealed class DelegatedAgentJobServiceTests
{
    [TestMethod]
    public void Create_ThenComplete_PersistsJobAndLogs()
    {
        using var db = CreateDatabase();
        var service = new DelegatedAgentJobService(db);
        var job = CreateJob();

        service.Create(job);
        var running = service.SetRunning(job.Id);
        var completed = service.Complete(job.Id, "{\"result\":\"ok\"}");

        var saved = service.GetById(job.Id);
        var logs = service.ListLogs(job.Id);

        Assert.IsTrue(running);
        Assert.IsTrue(completed);
        Assert.IsNotNull(saved);
        Assert.AreEqual(DelegatedAgentJobStates.Completed, saved.State);
        Assert.AreEqual("{\"result\":\"ok\"}", saved.ResultJson);
        Assert.IsNotNull(saved.CompletedAt);
        Assert.IsGreaterThanOrEqualTo(logs.Count, 3);
    }

    [TestMethod]
    public void Cancel_WhenAlreadyCompleted_ReturnsFalseAndKeepsCompleted()
    {
        using var db = CreateDatabase();
        var service = new DelegatedAgentJobService(db);
        var job = CreateJob();

        service.Create(job);
        service.Complete(job.Id, "done");
        var cancelled = service.Cancel(job.Id);

        var saved = service.GetById(job.Id);

        Assert.IsFalse(cancelled);
        Assert.IsNotNull(saved);
        Assert.AreEqual(DelegatedAgentJobStates.Completed, saved.State);
    }

    [TestMethod]
    public void MarkAllActiveAsFailed_OnlyUpdatesPendingAndRunning()
    {
        using var db = CreateDatabase();
        var service = new DelegatedAgentJobService(db);
        var pending = CreateJob("pending");
        var running = CreateJob("running");
        var completed = CreateJob("completed");

        service.Create(pending);
        service.Create(running);
        service.SetRunning(running.Id);
        service.Create(completed);
        service.Complete(completed.Id, "done");

        var affected = service.MarkAllActiveAsFailed("shutdown");

        Assert.AreEqual(2, affected);
        Assert.AreEqual(DelegatedAgentJobStates.Failed, service.GetById(pending.Id)?.State);
        Assert.AreEqual(DelegatedAgentJobStates.Failed, service.GetById(running.Id)?.State);
        Assert.AreEqual(DelegatedAgentJobStates.Completed, service.GetById(completed.Id)?.State);
    }

    [TestMethod]
    public void CleanupCompletedBefore_RemovesCompletedJobsAndLogs()
    {
        using var db = CreateDatabase();
        var service = new DelegatedAgentJobService(db);
        var completed = CreateJob("completed");
        var running = CreateJob("running");

        service.Create(completed);
        service.Complete(completed.Id, "{\"result\":\"done\"}");
        service.Create(running);
        service.SetRunning(running.Id);

        var completedLogsBefore = service.ListLogs(completed.Id);
        var affected = service.CleanupCompletedBefore(DateTimeOffset.Now.AddMinutes(1).ToUnixTimeMilliseconds());

        Assert.AreEqual(1, affected);
        Assert.IsNull(service.GetById(completed.Id));
        Assert.IsEmpty(service.ListLogs(completed.Id));
        Assert.IsNotNull(service.GetById(running.Id));
        Assert.IsNotEmpty(completedLogsBefore);
    }

    private static CortanaDbContext CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortana-delegated-agent-tests", $"{Guid.NewGuid():N}.db");
        return new CortanaDbContext(path);
    }

    private static DelegatedAgentJobEntity CreateJob(string suffix = "job") => new()
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
}
