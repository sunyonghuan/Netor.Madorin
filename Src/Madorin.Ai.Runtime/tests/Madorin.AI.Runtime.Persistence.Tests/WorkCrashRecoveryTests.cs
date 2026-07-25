using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class WorkCrashRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(WorkCrashBoundary.PlanCommitted)]
    [DataRow(WorkCrashBoundary.StepRunningCommitted)]
    [DataRow(WorkCrashBoundary.ToolSentCommitted)]
    [DataRow(WorkCrashBoundary.ToolResultCommitted)]
    [DataRow(WorkCrashBoundary.StepCompletedCommitted)]
    public async Task CommittedBoundaryThenCrash_RestartConvergesWithoutDuplicateWork(
        WorkCrashBoundary boundary)
    {
        var dataDirectory = CreateDataDirectory(boundary);
        try
        {
            var crash = await Assert.ThrowsExactlyAsync<SimulatedRuntimeCrashException>(async () =>
            {
                await using var connection = await DataDirectoryInitializer.InitializeAsync(
                    dataDirectory,
                    TestContext.CancellationToken);
                _ = await ExecuteToBoundaryAsync(
                    connection,
                    boundary,
                    TestContext.CancellationToken);
            });

            var sessionId = crash.SessionId;
            var runId = crash.RunId;
            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var sessionRepository = new SqliteSessionRepository(recoveredConnection);
            var workRepository = new SqliteWorkRepository(recoveredConnection);
            using var toolRepository = new SqliteToolIntentRepository(recoveredConnection);
            var run = await sessionRepository.GetRunSnapshotAsync(
                runId,
                TestContext.CancellationToken);
            Assert.IsNotNull(run);
            Assert.AreEqual(RunStatus.Interrupted, run.Status);
            var resume = await workRepository.GetResumeStateAsync(
                sessionId,
                TestContext.CancellationToken);
            Assert.IsNotNull(resume);
            Assert.AreEqual(WorkSessionStatus.Interrupted, resume.Status);
            var step = Assert.ContainsSingle(resume.Steps);
            var intent = await toolRepository.GetIntentAsync(
                ToolCallId,
                TestContext.CancellationToken);

            switch (boundary)
            {
                case WorkCrashBoundary.PlanCommitted:
                    Assert.AreEqual(WorkStepLifecycleStatus.Pending, step.Status);
                    Assert.AreEqual(0, step.AttemptCount);
                    Assert.IsNull(intent);
                    break;
                case WorkCrashBoundary.StepRunningCommitted:
                    AssertInterruptedStep(step);
                    Assert.IsNull(intent);
                    break;
                case WorkCrashBoundary.ToolSentCommitted:
                    AssertInterruptedStep(step);
                    Assert.IsNotNull(intent);
                    Assert.AreEqual(ToolIntentStatus.Sent, intent.Status);
                    CollectionAssert.Contains(
                        resume.SentToolCallIds
                            ?? throw new InvalidDataException("Resume did not return Sent call IDs."),
                        ToolCallId);
                    break;
                case WorkCrashBoundary.ToolResultCommitted:
                    AssertInterruptedStep(step);
                    AssertSucceededIntent(intent);
                    Assert.IsEmpty(
                        resume.SentToolCallIds
                            ?? throw new InvalidDataException("Resume did not return Sent call IDs."));
                    break;
                case WorkCrashBoundary.StepCompletedCommitted:
                    Assert.AreEqual(WorkStepLifecycleStatus.Completed, step.Status);
                    Assert.AreEqual(StepResultJson, step.ResultJson);
                    Assert.AreEqual(1, step.AttemptCount);
                    AssertSucceededIntent(intent);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(boundary), boundary, null);
            }

            Assert.AreEqual(
                0L,
                await CountPermanentRunningAsync(
                    recoveredConnection,
                    TestContext.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private const string ToolCallId = "work-crash-call";
    private const string StepResultJson = "{\"result\":\"complete\"}";

    private static void AssertInterruptedStep(WorkStepSnapshot step)
    {
        Assert.AreEqual(WorkStepLifecycleStatus.Interrupted, step.Status);
        Assert.AreEqual(1, step.AttemptCount);
        Assert.IsNotNull(step.CheckpointJson);
    }

    private static void AssertSucceededIntent(ToolIntentState? intent)
    {
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
        Assert.AreEqual("{\"ok\":true}", intent.ResultJson);
    }

    private static async Task<(string SessionId, string RunId)> ExecuteToBoundaryAsync(
        SqliteConnection connection,
        WorkCrashBoundary boundary,
        CancellationToken ct)
    {
        var sessionRepository = new SqliteSessionRepository(connection);
        var workRepository = new SqliteWorkRepository(connection);
        using var toolRepository = new SqliteToolIntentRepository(connection);
        var sessionId = await sessionRepository.CreateSessionAsync(
            RuntimeMode.Work,
            Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(10),
            ct);
        var runId = await sessionRepository.CreateRunAsync(
            sessionId,
            Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(10),
            ct);
        await sessionRepository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct);
        await sessionRepository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            ct);

        var plan = new WorkPlanDraft(
            "1",
            "Verify crash boundaries",
            [new WorkPlanStepDraft("crash-step", "Complete durable work", "worker")]);
        await workRepository.SavePlanRevisionAsync(
            sessionId,
            runId,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan,
            "plan-message",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["crash-step"] = "step-message"
            },
            ct);
        CrashIf(boundary, WorkCrashBoundary.PlanCommitted, sessionId, runId);

        Assert.IsTrue(await sessionRepository.TryStartWorkStepWithCheckpointAsync(
            "crash-step",
            "1",
            runId,
            sessionId,
            "worker",
            "step-input-hash",
            "crash-invocation",
            checkpointJson: "{\"phase\":\"running\"}",
            ct: ct));
        CrashIf(boundary, WorkCrashBoundary.StepRunningCommitted, sessionId, runId);

        const string argumentsJson = "{\"value\":42}";
        var argumentsHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson)));
        Assert.IsTrue(await toolRepository.TryCreateIntentAsync(
            new ToolIntentState(
                ToolCallId,
                "crash-invocation",
                runId,
                sessionId,
                "worker",
                ParentAgentId: "manager",
                ToolId: "host.crash.boundary",
                ToolCatalogVersion: "host-v1",
                ArgumentsHash: argumentsHash,
                Status: ToolIntentStatus.Pending,
                GrantId: null,
                ApprovalRequestId: null,
                ResultJson: null,
                ResultHash: null,
                ResultBlob: null,
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: true,
                DateTimeOffset.UtcNow,
                SentAt: null,
                CompletedAt: null,
                WorkStepId: "crash-step",
                PlanVersion: "1"),
            ct));
        Assert.IsTrue(await toolRepository.TryMarkSentAsync(
            ToolCallId,
            "crash-grant",
            approvalRequestId: null,
            DateTimeOffset.UtcNow,
            ct));
        CrashIf(boundary, WorkCrashBoundary.ToolSentCommitted, sessionId, runId);

        Assert.IsTrue(await toolRepository.TryCompleteIntentAsync(
            new ToolIntentCompletion(
                ToolCallId,
                ToolIntentStatus.Sent,
                ToolIntentStatus.Succeeded,
                "{\"ok\":true}",
                ResultHash: "result-hash",
                ResultBlob: null,
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: true,
                DateTimeOffset.UtcNow),
            ct));
        CrashIf(boundary, WorkCrashBoundary.ToolResultCommitted, sessionId, runId);

        await sessionRepository.CompleteWorkStepAsync(
            "crash-step",
            "1",
            "step-input-hash",
            StepResultJson,
            ct);
        CrashIf(boundary, WorkCrashBoundary.StepCompletedCommitted, sessionId, runId);
        throw new InvalidOperationException("The selected crash boundary was not reached.");
    }

    private static void CrashIf(
        WorkCrashBoundary actual,
        WorkCrashBoundary expected,
        string sessionId,
        string runId)
    {
        if (actual == expected)
        {
            throw new SimulatedRuntimeCrashException(sessionId, runId, expected);
        }
    }

    private static async Task<long> CountPermanentRunningAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM runs
                 WHERE LOWER(status) IN ('accepted', 'preparing', 'running', 'waitingfortool', 'persisting'))
              + (SELECT COUNT(*) FROM work_steps WHERE LOWER(status) = 'running')
              + (SELECT COUNT(*) FROM work_sessions
                 WHERE LOWER(status) IN ('planning', 'executing'))
              + (SELECT COUNT(*) FROM work_step_attempts WHERE LOWER(status) = 'running')
              + (SELECT COUNT(*) FROM work_background_jobs WHERE LOWER(status) = 'running');
            """;
        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException("The Running-state count returned no value."));
    }

    private static string CreateDataDirectory(WorkCrashBoundary boundary)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-work-crash-tests",
            boundary.ToString(),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public enum WorkCrashBoundary
    {
        PlanCommitted,
        StepRunningCommitted,
        ToolSentCommitted,
        ToolResultCommitted,
        StepCompletedCommitted
    }

    private sealed class SimulatedRuntimeCrashException(
        string sessionId,
        string runId,
        WorkCrashBoundary boundary)
        : Exception($"Runtime terminated after '{boundary}'.")
    {
        public string SessionId { get; } = sessionId;

        public string RunId { get; } = runId;
    }
}
