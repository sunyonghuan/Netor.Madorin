using System.Diagnostics;
using System.Globalization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SqliteSessionRepositoryTests
{
    private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(1);
    private static readonly string[] WorkSchemaElevenTables =
    [
        "work_sessions",
        "work_step_dependencies",
        "work_step_attempts",
        "work_background_jobs"
    ];
    private static readonly string[] WorkToolIntentColumns = ["work_step_id", "plan_version"];
    private static readonly string[] WorkStepSchemaElevenColumns =
    [
        "parent_step_id",
        "invocation_id",
        "depth",
        "step_index",
        "attempt_count",
        "error_message",
        "started_at",
        "title",
        "goal",
        "is_background",
        "depends_json"
    ];
    private static readonly string[] WorkSchemaElevenIndexes =
    [
        "idx_work_steps_session",
        "idx_work_steps_run",
        "idx_work_steps_status",
        "idx_work_steps_agent",
        "idx_work_step_dependencies_step",
        "idx_work_step_dependencies_depends",
        "idx_work_step_attempts_step",
        "idx_work_background_jobs_session",
        "idx_work_background_jobs_step",
        "idx_tool_intents_work_step"
    ];
    private static readonly string[] ResumeSentToolCallIds = ["call-step-a"];

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task CreateSessionAsync_WithSameIdempotencyKey_ReturnsSameSessionId()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var first = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);
        var second = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task CreateRunAsync_WithSameIdempotencyKey_ReturnsSameRunId()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Work,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);

        var first = await repository.CreateRunAsync(
            sessionId,
            "run-key",
            KeyRetention,
            TestContext.CancellationToken);
        var second = await repository.CreateRunAsync(
            sessionId,
            "run-key",
            KeyRetention,
            TestContext.CancellationToken);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task TryDeleteSessionAsync_WorkSession_RemovesOwnedRowsAndKeepsOtherSession()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var sessionRepository = new SqliteSessionRepository(connection);
        var workRepository = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepository.CreateSessionAsync(
            RuntimeMode.Work,
            "delete-work-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await sessionRepository.CreateRunAsync(
            sessionId,
            "delete-work-run",
            KeyRetention,
            TestContext.CancellationToken);
        var retainedSessionId = await sessionRepository.CreateSessionAsync(
            RuntimeMode.Expert,
            "retained-session",
            KeyRetention,
            TestContext.CancellationToken);
        var plan = new WorkPlanDraft(
            "1",
            "Delete complete Work state",
            [
                new WorkPlanStepDraft("delete-step-a", "Prepare", "worker"),
                new WorkPlanStepDraft(
                    "delete-step-b",
                    "Review",
                    "reviewer",
                    DependsOn: ["delete-step-a"],
                    IsBackground: true)
            ]);
        await workRepository.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            TestContext.CancellationToken);
        await workRepository.SavePlanStepsAsync(
            sessionId,
            runId,
            plan,
            TestContext.CancellationToken);
        var stepInputHash = await ReadStepInputHashAsync(
            connection,
            "delete-step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(await sessionRepository.TryStartWorkStepAsync(
            "delete-step-a",
            plan.PlanVersion,
            runId,
            sessionId,
            "worker",
            stepInputHash,
            "delete-invocation",
            TestContext.CancellationToken));
        await workRepository.CreateBackgroundJobAsync(
            "delete-job",
            sessionId,
            runId,
            "delete-step-b",
            plan.PlanVersion,
            TestContext.CancellationToken);

        var deleted = await sessionRepository.TryDeleteSessionAsync(
            sessionId,
            TestContext.CancellationToken);

        Assert.IsTrue(deleted);
        Assert.IsNull(await sessionRepository.GetSessionSnapshotAsync(
            sessionId,
            TestContext.CancellationToken));
        Assert.IsNotNull(await sessionRepository.GetSessionSnapshotAsync(
            retainedSessionId,
            TestContext.CancellationToken));
        Assert.IsFalse(await sessionRepository.TryDeleteSessionAsync(
            sessionId,
            TestContext.CancellationToken));
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM sessions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM runs WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM session_idempotency WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM run_idempotency WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_sessions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_plan_revisions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_steps WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_background_jobs WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_step_dependencies
                 WHERE step_id IN ('delete-step-a', 'delete-step-b')
                    OR depends_on_step_id IN ('delete-step-a', 'delete-step-b'))
              + (SELECT COUNT(*) FROM work_step_attempts
                 WHERE step_id IN ('delete-step-a', 'delete-step-b'));
            """;
        countCommand.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            0L,
            Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task CreateSessionAsync_SameKeyWithDifferentRequestHash_ThrowsConflict()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            "hash-1",
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "session-key",
                KeyRetention,
                "hash-2",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CreateRunAsync_SameKeyWithDifferentRequestHash_ThrowsConflict()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);
        await repository.CreateRunAsync(
            sessionId,
            "run-key",
            KeyRetention,
            "hash-1",
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.CreateRunAsync(
                sessionId,
                "run-key",
                KeyRetention,
                "hash-2",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TransitionRunStatusAsync_WithLegalPath_PersistsTargetStatus()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);

        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.Running,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TransitionRunStatusAsync_WithIllegalPath_ThrowsInvalidOperationException()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Accepted,
                RunStatus.Completed,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TransitionRunToTerminalAsync_PersistsTextWithTerminalStateAtomically()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(
            connection,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.Persisting,
            TestContext.CancellationToken);

        var active = await repository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(active);
        Assert.AreEqual(RunStatus.Persisting, active.Status);
        Assert.IsNull(active.TerminalText);

        await repository.TransitionRunToTerminalAsync(
            runId,
            RunStatus.Persisting,
            RunStatus.Completed,
            "  最终答案\nline 2  ",
            TestContext.CancellationToken);

        var completed = await repository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(completed);
        Assert.AreEqual(RunStatus.Completed, completed.Status);
        Assert.AreEqual("  最终答案\nline 2  ", completed.TerminalText);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.TransitionRunToTerminalAsync(
                runId,
                RunStatus.Persisting,
                RunStatus.Failed,
                "replacement",
                TestContext.CancellationToken));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task TransitionRunToTerminalAsync_WithConcurrentWriters_CommitsExactlyOneTerminalState()
    {
        const int iterationCount = 10;
        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-terminal-race-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var connectionString = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        try
        {
            await using var setupConnection = new SqliteConnection(connectionString);
            await setupConnection.OpenAsync(TestContext.CancellationToken);
            await SqliteSchema.EnsureCreatedAsync(
                setupConnection,
                TestContext.CancellationToken);

            for (var iteration = 0; iteration < iterationCount; iteration++)
            {
                var (setupRepository, runId) = await CreateRunAsync(
                    setupConnection,
                    TestContext.CancellationToken);
                await setupRepository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Accepted,
                    RunStatus.Preparing,
                    TestContext.CancellationToken);
                await setupRepository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Preparing,
                    RunStatus.Running,
                    TestContext.CancellationToken);
                await setupRepository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Running,
                    RunStatus.Persisting,
                    TestContext.CancellationToken);

                TerminalAttempt[] attempts =
                [
                    new(RunStatus.Completed, $"completed-{iteration}-1"),
                    new(RunStatus.Failed, $"timeout-{iteration}-1"),
                    new(RunStatus.Cancelled, $"cancelled-{iteration}-1"),
                    new(RunStatus.Completed, $"completed-{iteration}-2"),
                    new(RunStatus.Failed, $"disconnect-{iteration}-2"),
                    new(RunStatus.Cancelled, $"cancelled-{iteration}-2")
                ];
                var readyCount = 0;
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var start = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                async Task<TerminalAttempt?> CompeteAsync(TerminalAttempt attempt)
                {
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync(TestContext.CancellationToken);
                    await using (var busyTimeout = connection.CreateCommand())
                    {
                        busyTimeout.CommandText = "PRAGMA busy_timeout = 5000;";
                        await busyTimeout.ExecuteNonQueryAsync(TestContext.CancellationToken);
                    }

                    if (Interlocked.Increment(ref readyCount) == attempts.Length)
                    {
                        ready.TrySetResult();
                    }

                    await start.Task.WaitAsync(TestContext.CancellationToken);
                    var repository = new SqliteSessionRepository(connection);
                    try
                    {
                        await repository.TransitionRunToTerminalAsync(
                            runId,
                            RunStatus.Persisting,
                            attempt.Status,
                            attempt.Text,
                            TestContext.CancellationToken);
                        return attempt;
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                }

                var competitors = attempts.Select(CompeteAsync).ToArray();
                await ready.Task.WaitAsync(TestContext.CancellationToken);
                start.TrySetResult();
                var winners = (await Task.WhenAll(competitors))
                    .OfType<TerminalAttempt>()
                    .ToArray();

                Assert.HasCount(1, winners);
                var snapshot = await setupRepository.GetRunSnapshotAsync(
                    runId,
                    TestContext.CancellationToken);
                Assert.IsNotNull(snapshot);
                Assert.AreEqual(winners[0].Status, snapshot.Status);
                Assert.AreEqual(winners[0].Text, snapshot.TerminalText);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_FromVersionFive_AddsTerminalTextWithoutLosingRuns()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(connection, 5, TestContext.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                VALUES('session-v5', 'Expert', 'Active', '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
                INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
                VALUES('run-v5', 'session-v5', 'Completed', 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetRunSnapshotAsync(
            "run-v5",
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(RunStatus.Completed, snapshot.Status);
        Assert.IsNull(snapshot.TerminalText);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_FromVersionTen_AddsWorkModeSchemaEleven()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(connection, 10, TestContext.CancellationToken);

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            await GetSchemaVersionAsync(connection, TestContext.CancellationToken));
        CollectionAssert.IsSubsetOf(
            WorkSchemaElevenTables,
            await ListTableNamesAsync(connection, TestContext.CancellationToken));
        CollectionAssert.IsSubsetOf(
            WorkToolIntentColumns,
            await ListColumnNamesAsync(connection, "tool_intents", TestContext.CancellationToken));
        CollectionAssert.IsSubsetOf(
            WorkStepSchemaElevenColumns,
            await ListColumnNamesAsync(connection, "work_steps", TestContext.CancellationToken));
        CollectionAssert.IsSubsetOf(
            WorkSchemaElevenIndexes,
            await ListIndexNamesAsync(connection, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SavePlanRevisionAsync_IncrementsVersionAndPreservesCompletedHistory()
    {
        var ct = TestContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var sessionRepo = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Work,
            "work-plan-revision-session",
            KeyRetention,
            ct);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            "work-plan-revision-run",
            KeyRetention,
            ct);
        var first = new WorkPlanDraft(
            "1",
            "Ship release",
            [new WorkPlanStepDraft("step-a", "Prepare notes", "worker")]);
        await workRepo.SavePlanRevisionAsync(
            sessionId,
            runId,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            first,
            "plan-message-1",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["step-a"] = "step-message-1"
            },
            ct);
        Assert.IsTrue(await sessionRepo.TryStartWorkStepAsync(
            "step-a",
            "1",
            runId,
            sessionId,
            "worker",
            await ReadStepInputHashAsync(connection, "step-a", "1", ct),
            "invocation-a",
            ct));
        await sessionRepo.CompleteWorkStepAsync(
            "step-a",
            "1",
            await ReadStepInputHashAsync(connection, "step-a", "1", ct),
            "{}",
            ct);

        var second = new WorkPlanDraft(
            "2",
            "Ship release",
            [
                new WorkPlanStepDraft(
                    "step-a-reused",
                    "Reuse prepared notes",
                    "worker",
                    ReusesStepId: "step-a")
            ],
            PreviousPlanVersion: "1");
        await workRepo.SavePlanRevisionAsync(
            sessionId,
            runId,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            second,
            "plan-message-2",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["step-a-reused"] = "step-message-2"
            },
            ct);

        var revisions = await workRepo.ListPlanRevisionsAsync(sessionId, ct);
        Assert.HasCount(2, revisions);
        Assert.AreEqual("1", revisions[1].PreviousPlanVersion);
        var allSteps = await workRepo.ListStepsAsync(sessionId, ct: ct);
        Assert.HasCount(2, allSteps);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, allSteps[0].Status);
        Assert.AreEqual("step-a", allSteps[1].ReusesStepId);
        Assert.AreEqual("step-message-2", allSteps[1].StepMessageId);

        var invalid = second with
        {
            PlanVersion = "4",
            PreviousPlanVersion = "2",
            Steps = [new WorkPlanStepDraft("step-invalid", "Invalid", "worker")]
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => workRepo.SavePlanRevisionAsync(
                sessionId,
                runId,
                "manager",
                WorkflowPolicy.Default,
                WorkContextPolicy.Default,
                invalid,
                "plan-message-invalid",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["step-invalid"] = "step-message-invalid"
                },
                ct));
        Assert.HasCount(2, await workRepo.ListPlanRevisionsAsync(sessionId, ct));
        Assert.HasCount(2, await workRepo.ListStepsAsync(sessionId, ct: ct));
    }

    [TestMethod]
    public async Task GetResumeStateAsync_ReturnsWorkPlanStepsBackgroundJobsAndSentToolCalls()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var sessionRepo = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Work,
            "work-resume-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            "work-resume-run",
            KeyRetention,
            TestContext.CancellationToken);
        var plan = new WorkPlanDraft(
            "2",
            "Ship release",
            [
                new WorkPlanStepDraft("step-a", "Prepare notes", "worker"),
                new WorkPlanStepDraft("step-b", "Review notes", "reviewer", DependsOn: ["step-a"])
            ]);

        await workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: "message-plan",
            plan,
            TestContext.CancellationToken);
        await workRepo.SavePlanStepsAsync(sessionId, runId, plan, TestContext.CancellationToken);
        await sessionRepo.TryStartWorkStepAsync(
            "step-a",
            plan.PlanVersion,
            runId,
            sessionId,
            "worker",
            await ReadStepInputHashAsync(connection, "step-a", plan.PlanVersion, TestContext.CancellationToken),
            "invocation-a",
            TestContext.CancellationToken);
        await InsertSentToolIntentAsync(
            connection,
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        await workRepo.CreateBackgroundJobAsync(
            "job-step-a",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(
            await workRepo.TryStartBackgroundJobAsync("job-step-a", TestContext.CancellationToken));

        var resume = await workRepo.GetResumeStateAsync(sessionId, TestContext.CancellationToken);

        Assert.IsNotNull(resume);
        Assert.AreEqual(WorkSessionStatus.Executing, resume.Status);
        Assert.AreEqual("manager", resume.GeneralManagerId);
        Assert.AreEqual("2", resume.PlanVersion);
        Assert.AreEqual("message-plan", resume.PlanMessageId);
        Assert.HasCount(2, resume.Steps);
        Assert.IsNotNull(resume.CurrentStep);
        Assert.AreEqual("step-a", resume.CurrentStep.StepId);
        Assert.AreEqual(WorkStepLifecycleStatus.Running, resume.CurrentStep.Status);
        Assert.IsNotNull(resume.BackgroundJobs);
        var backgroundJob = Assert.ContainsSingle(resume.BackgroundJobs);
        Assert.AreEqual("job-step-a", backgroundJob.JobId);
        Assert.AreEqual("step-a", backgroundJob.StepId);
        Assert.AreEqual(WorkBackgroundJobStatus.Running, backgroundJob.Status);
        Assert.IsNotNull(backgroundJob.StartedAt);
        Assert.IsNull(backgroundJob.CompletedAt);
        CollectionAssert.AreEqual(ResumeSentToolCallIds, resume.SentToolCallIds);
    }

    [TestMethod]
    public async Task BackgroundJobLifecycle_WithStartCompleteCancelAndTimeout_PersistsState()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var sessionRepo = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Work,
            "work-background-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            "work-background-run",
            KeyRetention,
            TestContext.CancellationToken);
        var plan = new WorkPlanDraft(
            "1",
            "Background job lifecycle",
            [new WorkPlanStepDraft("step-a", "Run background work", "worker", IsBackground: true)]);
        await workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            TestContext.CancellationToken);
        await workRepo.SavePlanStepsAsync(sessionId, runId, plan, TestContext.CancellationToken);

        var pending = await workRepo.CreateBackgroundJobAsync(
            "job-complete",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.AreEqual(WorkBackgroundJobStatus.Pending, pending.Status);

        Assert.IsTrue(
            await workRepo.TryStartBackgroundJobAsync("job-complete", TestContext.CancellationToken));
        await workRepo.CompleteBackgroundJobAsync("job-complete", TestContext.CancellationToken);
        var completed = await workRepo.GetBackgroundJobAsync("job-complete", TestContext.CancellationToken);
        Assert.IsNotNull(completed);
        Assert.AreEqual(WorkBackgroundJobStatus.Completed, completed.Status);
        Assert.IsNotNull(completed.StartedAt);
        Assert.IsNotNull(completed.CompletedAt);

        var reused = await workRepo.CreateBackgroundJobAsync(
            "job-complete",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.AreEqual(WorkBackgroundJobStatus.Completed, reused.Status);

        await workRepo.CreateBackgroundJobAsync(
            "job-cancel",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(
            await workRepo.CancelBackgroundJobAsync(
                "job-cancel",
                "user cancelled",
                TestContext.CancellationToken));
        var cancelled = await workRepo.GetBackgroundJobAsync("job-cancel", TestContext.CancellationToken);
        Assert.IsNotNull(cancelled);
        Assert.AreEqual(WorkBackgroundJobStatus.Cancelled, cancelled.Status);
        Assert.AreEqual("user cancelled", cancelled.ErrorMessage);

        await workRepo.CreateBackgroundJobAsync(
            "job-timeout",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(
            await workRepo.TryStartBackgroundJobAsync("job-timeout", TestContext.CancellationToken));
        await BackdateBackgroundJobAsync(
            connection,
            "job-timeout",
            "2026-07-23T00:00:00.000Z",
            TestContext.CancellationToken);

        var cancelledCount = await workRepo.CancelTimedOutBackgroundJobsAsync(
            TimeSpan.FromMinutes(1),
            "lease timeout",
            TestContext.CancellationToken);

        Assert.AreEqual(1, cancelledCount);
        var timedOut = await workRepo.GetBackgroundJobAsync("job-timeout", TestContext.CancellationToken);
        Assert.IsNotNull(timedOut);
        Assert.AreEqual(WorkBackgroundJobStatus.Cancelled, timedOut.Status);
        Assert.AreEqual("lease timeout", timedOut.ErrorMessage);
        var jobs = await workRepo.ListBackgroundJobsAsync(
            sessionId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.HasCount(3, jobs);
    }

    [TestMethod]
    public async Task MarkSessionInterruptedAsync_WithWaitingStepAndRunningJob_ConvergesAtomically()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var sessionRepo = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Work,
            "work-cancel-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            "work-cancel-run",
            KeyRetention,
            TestContext.CancellationToken);
        var plan = new WorkPlanDraft(
            "1",
            "Cancel active work",
            [new WorkPlanStepDraft("step-a", "Run cancellable work", "worker", IsBackground: true)]);
        await workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            TestContext.CancellationToken);
        await workRepo.SavePlanStepsAsync(sessionId, runId, plan, TestContext.CancellationToken);
        var stepInputHash = await ReadStepInputHashAsync(
            connection,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(
            await sessionRepo.TryStartWorkStepAsync(
                "step-a",
                plan.PlanVersion,
                runId,
                sessionId,
                "worker",
                stepInputHash,
                "invocation-a",
                TestContext.CancellationToken));
        await workRepo.MarkStepWaitingForApprovalAsync(
            sessionId,
            "step-a",
            plan.PlanVersion,
            "approval-a",
            "{\"kind\":\"tool\"}",
            TestContext.CancellationToken);
        await workRepo.CreateBackgroundJobAsync(
            "job-a",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(await workRepo.TryStartBackgroundJobAsync("job-a", TestContext.CancellationToken));

        await workRepo.MarkSessionInterruptedAsync(
            sessionId,
            "parent run cancelled",
            TestContext.CancellationToken);

        var resume = await workRepo.GetResumeStateAsync(sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(resume);
        Assert.AreEqual(WorkSessionStatus.Interrupted, resume.Status);
        Assert.IsNull(resume.PendingApprovalRequestId);
        Assert.IsNull(resume.CurrentStep);
        var step = Assert.ContainsSingle(resume.Steps);
        Assert.AreEqual(WorkStepLifecycleStatus.Interrupted, step.Status);
        Assert.AreEqual("parent run cancelled", step.ErrorMessage);
        Assert.IsNotNull(resume.BackgroundJobs);
        var job = Assert.ContainsSingle(resume.BackgroundJobs);
        Assert.AreEqual(WorkBackgroundJobStatus.Cancelled, job.Status);
        Assert.AreEqual("parent run cancelled", job.ErrorMessage);
        Assert.IsNotNull(job.CompletedAt);

        await using var attempt = connection.CreateCommand();
        attempt.CommandText = """
            SELECT status, error_message, completed_at
            FROM work_step_attempts
            WHERE step_id = 'step-a'
              AND plan_version = '1'
              AND invocation_id = 'invocation-a';
            """;
        await using var reader = await attempt.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual("interrupted", reader.GetString(0));
        Assert.AreEqual("parent run cancelled", reader.GetString(1));
        Assert.IsFalse(reader.IsDBNull(2));
    }

    [TestMethod]
    public async Task ResetStepForRetryAsync_WithFailedStep_ReopensPendingStepForNewAttempt()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var sessionRepo = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Work,
            "work-retry-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            "work-retry-run",
            KeyRetention,
            TestContext.CancellationToken);
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [new WorkPlanStepDraft("step-a", "Prepare notes", "worker")]);
        await workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            TestContext.CancellationToken);
        await workRepo.SavePlanStepsAsync(sessionId, runId, plan, TestContext.CancellationToken);
        var stepInputHash = await ReadStepInputHashAsync(
            connection,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        await sessionRepo.TryStartWorkStepAsync(
            "step-a",
            plan.PlanVersion,
            runId,
            sessionId,
            "worker",
            stepInputHash,
            "invocation-a",
            TestContext.CancellationToken);
        await sessionRepo.FailWorkStepAsync(
            "step-a",
            plan.PlanVersion,
            "temporary failure",
            TestContext.CancellationToken);

        await workRepo.ResetStepForRetryAsync(
            "step-a",
            plan.PlanVersion,
            "retry scheduled",
            TestContext.CancellationToken);
        var restarted = await sessionRepo.TryStartWorkStepAsync(
            "step-a",
            plan.PlanVersion,
            runId,
            sessionId,
            "worker",
            stepInputHash,
            "invocation-b",
            TestContext.CancellationToken);

        Assert.IsTrue(restarted);
        var step = Assert.ContainsSingle(
            await workRepo.ListStepsAsync(sessionId, plan.PlanVersion, TestContext.CancellationToken));
        Assert.AreEqual(WorkStepLifecycleStatus.Running, step.Status);
        Assert.AreEqual(2, step.AttemptCount);
        Assert.AreEqual("invocation-b", step.InvocationId);
        Assert.AreEqual("retry scheduled", step.ErrorMessage);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task EnsureCreatedAsync_FromEveryHistoricalVersion_PreservesExistingRun(
        int historicalVersion)
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(
            connection,
            historicalVersion,
            TestContext.CancellationToken);
        var sessionId = $"session-v{historicalVersion}";
        var runId = $"run-v{historicalVersion}";
        await InsertHistoricalRunAsync(
            connection,
            sessionId,
            runId,
            TestContext.CancellationToken);

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(sessionId, snapshot.SessionId);
        Assert.AreEqual(RunStatus.Running, snapshot.Status);
        Assert.IsNull(snapshot.TerminalText);
        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            await GetSchemaVersionAsync(connection, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_WhenMigrationFails_RollsBackVersionAndPreservesData()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(connection, 4, TestContext.CancellationToken);
        await InsertHistoricalRunAsync(
            connection,
            "session-failed-migration",
            "run-failed-migration",
            TestContext.CancellationToken);
        await using (var conflict = connection.CreateCommand())
        {
            conflict.CommandText =
                "ALTER TABLE session_idempotency ADD COLUMN request_hash TEXT;";
            await conflict.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(
            async () => await SqliteSchema.EnsureCreatedAsync(
                connection,
                TestContext.CancellationToken));

        Assert.AreEqual(
            4,
            await GetSchemaVersionAsync(connection, TestContext.CancellationToken));
        await using var preservedData = connection.CreateCommand();
        preservedData.CommandText = """
            SELECT COUNT(*)
            FROM runs
            WHERE run_id = 'run-failed-migration'
              AND session_id = 'session-failed-migration'
              AND status = 'Running';
            """;
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await preservedData.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_WithRunningRun_MarksRunInterrupted()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);

        await repository.MarkInterruptedAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.Interrupted,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_WithWaitingForApprovalRun_PreservesWaitingState()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.WaitingForApproval,
            TestContext.CancellationToken);

        await repository.MarkInterruptedAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.WaitingForApproval,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_WithWaitingForCredentialsWork_MarksAllActiveStateInterrupted()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Work,
            "work-credential-recovery-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await repository.CreateRunAsync(
            sessionId,
            "work-credential-recovery-run",
            KeyRetention,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);

        var plan = new WorkPlanDraft(
            "1",
            "Refresh provider credentials",
            [new WorkPlanStepDraft("step-credential", "Call provider", "worker")]);
        await workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            TestContext.CancellationToken);
        await workRepo.SavePlanStepsAsync(
            sessionId,
            runId,
            plan,
            TestContext.CancellationToken);
        Assert.IsTrue(await repository.TryStartWorkStepAsync(
            "step-credential",
            plan.PlanVersion,
            runId,
            sessionId,
            "worker",
            "input-hash",
            "invocation-credential",
            TestContext.CancellationToken));
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.WaitingForCredentials,
            TestContext.CancellationToken);
        await workRepo.MarkCredentialsWaitAsync(
            sessionId,
            "step-credential",
            plan.PlanVersion,
            TestContext.CancellationToken);

        var waiting = await workRepo.GetResumeStateAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(waiting);
        Assert.AreEqual(WorkSessionStatus.WaitingForCredentials, waiting.Status);
        Assert.IsNotNull(waiting.CurrentStep);
        Assert.AreEqual(WorkStepLifecycleStatus.WaitingForCredentials, waiting.CurrentStep.Status);

        await repository.MarkInterruptedAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.Interrupted,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
        var recovered = await workRepo.GetResumeStateAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(WorkSessionStatus.Interrupted, recovered.Status);
        Assert.AreEqual(
            WorkStepLifecycleStatus.Interrupted,
            Assert.ContainsSingle(recovered.Steps).Status);
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_WithRunningBackgroundJob_MarksJobInterrupted()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var sessionRepo = new SqliteSessionRepository(connection);
        var workRepo = new SqliteWorkRepository(connection);
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Work,
            "work-background-recovery-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            "work-background-recovery-run",
            KeyRetention,
            TestContext.CancellationToken);
        var plan = new WorkPlanDraft(
            "1",
            "Recover background job",
            [new WorkPlanStepDraft("step-a", "Run background work", "worker", IsBackground: true)]);
        await workRepo.UpsertSessionAsync(
            sessionId,
            runId,
            WorkSessionStatus.Executing,
            "manager",
            WorkflowPolicy.Default,
            WorkContextPolicy.Default,
            plan.PlanVersion,
            planMessageId: null,
            plan,
            TestContext.CancellationToken);
        await workRepo.SavePlanStepsAsync(sessionId, runId, plan, TestContext.CancellationToken);
        await workRepo.CreateBackgroundJobAsync(
            "job-recovery",
            sessionId,
            runId,
            "step-a",
            plan.PlanVersion,
            TestContext.CancellationToken);
        Assert.IsTrue(
            await workRepo.TryStartBackgroundJobAsync("job-recovery", TestContext.CancellationToken));

        await sessionRepo.MarkInterruptedAsync(TestContext.CancellationToken);

        var job = await workRepo.GetBackgroundJobAsync("job-recovery", TestContext.CancellationToken);
        Assert.IsNotNull(job);
        Assert.AreEqual(WorkBackgroundJobStatus.Interrupted, job.Status);
        Assert.IsNotNull(job.CompletedAt);
    }

    [TestMethod]
    public async Task ListSessionsAsync_100kSessions_UsesKeysetIndexWithinBaseline()
    {
        const int sessionCount = 100_000;
        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-pagination",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync(TestContext.CancellationToken);
            await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

            using (var transaction = connection.BeginTransaction())
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                    VALUES($sessionId, $mode, $status, $createdAt, $updatedAt);
                    """;
                var sessionId = insert.Parameters.Add("$sessionId", SqliteType.Text);
                var mode = insert.Parameters.Add("$mode", SqliteType.Text);
                var status = insert.Parameters.Add("$status", SqliteType.Text);
                var createdAt = insert.Parameters.Add("$createdAt", SqliteType.Text);
                var updatedAt = insert.Parameters.Add("$updatedAt", SqliteType.Text);
                mode.Value = RuntimeMode.Expert.ToString();
                status.Value = SessionStatus.Active.ToString();
                for (var index = 0; index < sessionCount; index++)
                {
                    var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index)
                        .ToString("O", CultureInfo.InvariantCulture);
                    sessionId.Value = $"session-{index:D6}";
                    createdAt.Value = timestamp;
                    updatedAt.Value = timestamp;
                    await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
                }

                transaction.Commit();
            }

            await using (var plan = connection.CreateCommand())
            {
                plan.CommandText = """
                    EXPLAIN QUERY PLAN
                    SELECT session_id, mode, status, updated_at
                    FROM sessions
                    WHERE updated_at < $updatedAt
                       OR (updated_at = $updatedAt AND session_id < $sessionId)
                    ORDER BY updated_at DESC, session_id DESC
                    LIMIT $pageSize;
                    """;
                plan.Parameters.AddWithValue(
                    "$updatedAt",
                    DateTimeOffset.UnixEpoch.AddSeconds(sessionCount / 2)
                        .ToString("O", CultureInfo.InvariantCulture));
                plan.Parameters.AddWithValue("$sessionId", "session-050000");
                plan.Parameters.AddWithValue("$pageSize", 100);
                await using var planReader = await plan.ExecuteReaderAsync(
                    TestContext.CancellationToken);
                var details = new List<string>();
                while (await planReader.ReadAsync(TestContext.CancellationToken))
                {
                    details.Add(planReader.GetString(3));
                }

                StringAssert.Contains(string.Join("\n", details), "idx_sessions_updated");
            }

            var repository = new SqliteSessionRepository(connection);
            var cursor = (string?)null;
            var total = 0;
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var page = await repository.ListSessionsAsync(
                    cursor,
                    pageSize: 500,
                    TestContext.CancellationToken);
                if (page.Count == 0)
                {
                    break;
                }

                total += page.Count;
                var last = page[^1];
                cursor = last.UpdatedAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture) + "|" + last.SessionId;
            }

            stopwatch.Stop();
            Assert.AreEqual(sessionCount, total);
            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"100k Session keyset pagination took {stopwatch.Elapsed}.");
            TestContext.WriteLine(
                $"session-pagination count={sessionCount} pages={sessionCount / 500} elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            if (connection is not null)
            {
                connection.Close();
                await connection.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task TrySaveSessionSelectionAsync_WithVersionConflict_PreservesCurrentSelection()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "selection-session",
            KeyRetention,
            TestContext.CancellationToken);
        var versionOneAgents = new[]
        {
            new AgentSnapshot("agent-1", "v1", "hash-v1", "provider-a", "model-a")
        };

        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: null,
            selectionVersion: 1,
            "{\"selectionVersion\":1}",
            versionOneAgents,
            TestContext.CancellationToken));
        Assert.IsFalse(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: 0,
            selectionVersion: 2,
            "{\"selectionVersion\":2}",
            versionOneAgents,
            TestContext.CancellationToken));
        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: 1,
            selectionVersion: 2,
            "{\"selectionVersion\":2}",
            [new AgentSnapshot("agent-1", "v2", "hash-v2", "provider-b", "model-b")],
            TestContext.CancellationToken));
        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: 1,
            selectionVersion: 2,
            "{\"selectionVersion\":2}",
            [new AgentSnapshot("agent-1", "v2", "hash-v2", "provider-b", "model-b")],
            TestContext.CancellationToken));

        var snapshot = await repository.GetSessionSnapshotAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(2, snapshot.SelectionVersion);
        Assert.AreEqual("{\"selectionVersion\":2}", snapshot.SelectionJson);
        Assert.HasCount(1, snapshot.AgentSnapshots);
        Assert.AreEqual("hash-v2", snapshot.AgentSnapshots[0].PromptHash);
    }

    [TestMethod]
    public async Task TrySaveSessionSelectionWithMeetingAsync_UpdatesAllTablesAndSoftDeletesRemovedParticipants()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var meetingRepository = new SqliteMeetingRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            "meeting-selection-session",
            KeyRetention,
            TestContext.CancellationToken);
        var initialParticipants = new[]
        {
            new MeetingParticipantInput("p1", "a1", null, "A", 0, "active"),
            new MeetingParticipantInput("p2", "a2", null, "B", 1, "active"),
            new MeetingParticipantInput("p3", "a3", null, "C", 2, "standby")
        };
        await meetingRepository.CreateMeetingSessionAsync(
            sessionId,
            "meeting-run-1",
            "policy-v1",
            "hash-policy-v1",
            initialParticipants,
            1,
            TestContext.CancellationToken);
        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            null,
            1,
            "selection-v1",
            [new AgentSnapshot("agent-1", "v1", "hash-v1", "provider-a", "model-a")],
            TestContext.CancellationToken));

        var saved = await repository.TrySaveSessionSelectionWithMeetingAsync(
            sessionId,
            1,
            2,
            "selection-v2",
            [new AgentSnapshot("agent-2", "v2", "hash-v2", "provider-b", "model-b")],
            [
                new MeetingParticipantInput("p1", "a1", null, "A updated", 2, "active"),
                new MeetingParticipantInput("p3", "a3", null, "C", 0, "standby")
            ],
            "policy-v2",
            "hash-policy-v2",
            TestContext.CancellationToken);

        Assert.IsTrue(saved);
        var session = await repository.GetSessionSnapshotAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(session);
        Assert.AreEqual(2, session.SelectionVersion);
        Assert.AreEqual("selection-v2", session.SelectionJson);
        Assert.HasCount(1, session.AgentSnapshots);
        Assert.AreEqual("agent-2", session.AgentSnapshots[0].AgentId);

        var meeting = await meetingRepository.GetMeetingSnapshotAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(meeting);
        Assert.AreEqual(2, meeting.Session.SelectionVersion);
        var removed = meeting.Participants.Single(p => p.ParticipantId == "p2");
        Assert.AreEqual("removed", removed.Status);
        Assert.AreEqual(2, removed.RemovedSelectionVersion);
        var updated = meeting.Participants.Single(p => p.ParticipantId == "p1");
        Assert.AreEqual("active", updated.Status);
        Assert.AreEqual(2, updated.JoinOrder);
        var standby = meeting.Participants.Single(p => p.ParticipantId == "p3");
        Assert.AreEqual("standby", standby.Status);
        Assert.AreEqual(0, standby.JoinOrder);
    }

    [TestMethod]
    public async Task TrySaveSessionSelectionWithMeetingAsync_MeetingVersionConflict_RollsBackSessionSelectionAndAgents()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var meetingRepository = new SqliteMeetingRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            "meeting-selection-conflict-session",
            KeyRetention,
            TestContext.CancellationToken);
        await meetingRepository.CreateMeetingSessionAsync(
            sessionId,
            "meeting-run-1",
            "policy-v1",
            "hash-policy-v1",
            [
                new MeetingParticipantInput("p1", "a1", null, "A", 0, "active"),
                new MeetingParticipantInput("p2", "a2", null, "B", 1, "active")
            ],
            1,
            TestContext.CancellationToken);
        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            null,
            1,
            "selection-v1",
            [new AgentSnapshot("agent-1", "v1", "hash-v1", "provider-a", "model-a")],
            TestContext.CancellationToken));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE meeting_sessions SET selection_version = 99 WHERE session_id = $sessionId;";
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var saved = await repository.TrySaveSessionSelectionWithMeetingAsync(
            sessionId,
            1,
            2,
            "selection-v2",
            [new AgentSnapshot("agent-2", "v2", "hash-v2", "provider-b", "model-b")],
            [new MeetingParticipantInput("p1", "a1", null, "A", 0, "active")],
            "policy-v2",
            "hash-policy-v2",
            TestContext.CancellationToken);

        Assert.IsFalse(saved);
        var session = await repository.GetSessionSnapshotAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(session);
        Assert.AreEqual(1, session.SelectionVersion);
        Assert.AreEqual("selection-v1", session.SelectionJson);
        Assert.HasCount(1, session.AgentSnapshots);
        Assert.AreEqual("agent-1", session.AgentSnapshots[0].AgentId);
        var meeting = await meetingRepository.GetMeetingSnapshotAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(meeting);
        Assert.AreEqual(99, meeting.Session.SelectionVersion);
        Assert.AreEqual("policy-v1", meeting.Session.PolicyJson);
    }

    [TestMethod]
    public async Task SaveInvocationSnapshotAsync_WithSameSnapshot_IsIdempotent()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            "snapshot-idempotent-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await repository.CreateRunAsync(
            sessionId,
            "snapshot-idempotent-run",
            KeyRetention,
            TestContext.CancellationToken);
        var snapshot = new InvocationSnapshot(
            "invocation-1",
            "agent-1",
            "provider-1",
            "model-1",
            "prompt-hash-1",
            "global-memory-hash-1",
            "project-memory-hash-1",
            "tools-v1",
            "projection-v1",
            new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero));
        const string snapshotJson = """{"invocationId":"invocation-1","version":1}""";

        await repository.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            TestContext.CancellationToken);
        await repository.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            TestContext.CancellationToken);

        Assert.AreEqual(
            snapshotJson,
            await repository.GetInvocationSnapshotJsonAsync(
                snapshot.InvocationId,
                TestContext.CancellationToken));
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText =
            "SELECT COUNT(*) FROM invocation_snapshots WHERE invocation_id = $invocationId;";
        countCommand.Parameters.AddWithValue("$invocationId", snapshot.InvocationId);
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task SaveInvocationSnapshotAsync_WithChangedImmutableField_RejectsAndPreservesOriginal()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            "snapshot-immutable-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await repository.CreateRunAsync(
            sessionId,
            "snapshot-immutable-run",
            KeyRetention,
            TestContext.CancellationToken);
        var otherRunId = await repository.CreateRunAsync(
            sessionId,
            "snapshot-other-run",
            KeyRetention,
            TestContext.CancellationToken);
        var otherSessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            "snapshot-other-session",
            KeyRetention,
            TestContext.CancellationToken);
        var snapshot = new InvocationSnapshot(
            "invocation-immutable",
            "agent-1",
            "provider-1",
            "model-1",
            "prompt-hash-1",
            null,
            null,
            "tools-v1",
            "projection-v1",
            new DateTimeOffset(2026, 7, 23, 4, 5, 6, TimeSpan.Zero));
        const string snapshotJson = """{"invocationId":"invocation-immutable","version":1}""";
        const string changedSnapshotJson = """{"invocationId":"invocation-immutable","version":2}""";
        await repository.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                runId,
                sessionId,
                snapshot,
                changedSnapshotJson,
                TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                otherRunId,
                sessionId,
                snapshot,
                snapshotJson,
                TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                runId,
                otherSessionId,
                snapshot,
                snapshotJson,
                TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                runId,
                sessionId,
                snapshot with { StartedAt = snapshot.StartedAt.AddSeconds(1) },
                snapshotJson,
                TestContext.CancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, session_id, snapshot_json, started_at
            FROM invocation_snapshots
            WHERE invocation_id = $invocationId;
            """;
        command.Parameters.AddWithValue("$invocationId", snapshot.InvocationId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual(runId, reader.GetString(0));
        Assert.AreEqual(sessionId, reader.GetString(1));
        Assert.AreEqual(snapshotJson, reader.GetString(2));
        Assert.AreEqual(
            snapshot.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            reader.GetString(3));
        Assert.IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ListMessageIndexAsync_WithCursor_ReturnsOrderedMetadataPage()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "message-session",
            KeyRetention,
            TestContext.CancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO message_index(
                    message_id,
                    session_id,
                    sequence,
                    invocation_id,
                    agent_id,
                    role,
                    file_offset,
                    record_length,
                    created_at)
                VALUES
                    ('message-1', $sessionId, 1, 'invocation-1', 'agent-1', 'user', 100, 20, '2026-07-22T00:00:00.0000000+00:00'),
                    ('message-2', $sessionId, 2, 'invocation-1', 'agent-1', 'assistant', 120, 30, '2026-07-22T00:00:01.0000000+00:00'),
                    ('message-3', $sessionId, 3, 'invocation-2', 'agent-1', 'user', 150, 25, '2026-07-22T00:00:02.0000000+00:00');
                """;
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var firstPage = await repository.ListMessageIndexAsync(
            sessionId,
            cursor: null,
            pageSize: 2,
            TestContext.CancellationToken);
        var secondPage = await repository.ListMessageIndexAsync(
            sessionId,
            cursor: firstPage[^1].Sequence,
            pageSize: 2,
            TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            firstPage.Select(static entry => entry.Sequence).ToArray());
        CollectionAssert.AreEqual(
            new long[] { 3 },
            secondPage.Select(static entry => entry.Sequence).ToArray());
        Assert.AreEqual("assistant", firstPage[1].Role);
    }

    private static async Task<(SqliteSessionRepository Repository, string RunId)> CreateRunAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting,
            Guid.NewGuid().ToString("N"),
            KeyRetention,
            ct);
        var runId = await repository.CreateRunAsync(
            sessionId,
            Guid.NewGuid().ToString("N"),
            KeyRetention,
            ct);
        return (repository, runId);
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }

    private static async Task DowngradeSchemaAsync(
        SqliteConnection connection,
        int targetVersion,
        CancellationToken ct)
    {
        var commands = new List<string>();
        if (targetVersion < 13)
        {
            commands.Add("DROP INDEX IF EXISTS idx_work_plan_revisions_session;");
            commands.Add("DROP TABLE IF EXISTS work_plan_revisions;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN step_message_id;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN reuses_step_id;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN replaces_step_id;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN checkpoint_json;");
        }

        if (targetVersion < 12)
        {
            commands.Add("ALTER TABLE tool_audit DROP COLUMN session_id;");
            commands.Add("ALTER TABLE tool_audit DROP COLUMN invocation_id;");
            commands.Add("ALTER TABLE tool_audit DROP COLUMN parent_invocation_id;");
            commands.Add("ALTER TABLE tool_audit DROP COLUMN work_step_id;");
            commands.Add("ALTER TABLE tool_audit DROP COLUMN plan_version;");
            commands.Add("ALTER TABLE tool_audit DROP COLUMN root_grant_id;");
        }

        if (targetVersion < 11)
        {
            commands.Add("DROP INDEX IF EXISTS idx_work_sessions_status;");
            commands.Add("DROP INDEX IF EXISTS idx_work_steps_session;");
            commands.Add("DROP INDEX IF EXISTS idx_work_steps_run;");
            commands.Add("DROP INDEX IF EXISTS idx_work_steps_status;");
            commands.Add("DROP INDEX IF EXISTS idx_work_steps_agent;");
            commands.Add("DROP INDEX IF EXISTS idx_work_step_dependencies_step;");
            commands.Add("DROP INDEX IF EXISTS idx_work_step_dependencies_depends;");
            commands.Add("DROP INDEX IF EXISTS idx_work_step_attempts_step;");
            commands.Add("DROP INDEX IF EXISTS idx_work_background_jobs_session;");
            commands.Add("DROP INDEX IF EXISTS idx_work_background_jobs_step;");
            commands.Add("DROP INDEX IF EXISTS idx_tool_intents_work_step;");
            commands.Add("DROP TABLE IF EXISTS work_sessions;");
            commands.Add("DROP TABLE IF EXISTS work_step_dependencies;");
            commands.Add("DROP TABLE IF EXISTS work_step_attempts;");
            commands.Add("DROP TABLE IF EXISTS work_background_jobs;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN parent_step_id;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN invocation_id;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN depth;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN step_index;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN attempt_count;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN error_message;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN started_at;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN title;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN goal;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN is_background;");
            commands.Add("ALTER TABLE work_steps DROP COLUMN depends_json;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN work_step_id;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN plan_version;");
        }

        if (targetVersion < 8)
        {
            commands.Add("DROP TABLE tool_audit;");
            commands.Add("DROP TABLE tool_approvals;");
            commands.Add("DROP TABLE tool_grants;");
            commands.Add("DROP INDEX idx_tool_intents_status;");
            commands.Add("DROP INDEX idx_tool_intents_run;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN agent_id;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN parent_agent_id;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN catalog_version;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN sent_at;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN grant_id;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN approval_request_id;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_hash;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_blob_id;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_blob_length;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_blob_sha256;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_blob_content_type;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_blob_access_scope;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_blob_expires_at;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN error_code;");
            commands.Add("ALTER TABLE tool_intents DROP COLUMN result_visible;");
        }

        if (targetVersion < 7)
        {
            commands.Add("DROP TABLE agent_snapshots;");
            commands.Add("DROP TABLE session_selections;");
        }

        if (targetVersion < 6)
        {
            commands.Add("ALTER TABLE runs DROP COLUMN terminal_text;");
        }
        if (targetVersion < 5)
        {
            commands.Add("ALTER TABLE session_idempotency DROP COLUMN request_hash;");
            commands.Add("ALTER TABLE run_idempotency DROP COLUMN request_hash;");
        }

        if (targetVersion < 4)
        {
            commands.Add("DROP TABLE work_steps;");
        }

        if (targetVersion < 3)
        {
            commands.Add("DROP TABLE message_index;");
        }

        if (targetVersion < 2)
        {
            commands.Add("DROP TABLE tool_intents;");
        }

        commands.Add("DELETE FROM schema_versions WHERE version > $targetVersion;");
        await using var command = connection.CreateCommand();
        command.CommandText = string.Join(Environment.NewLine, commands);
        command.Parameters.AddWithValue("$targetVersion", targetVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertHistoricalRunAsync(
        SqliteConnection connection,
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
            VALUES($sessionId, 'Expert', 'Active', '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
            INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
            VALUES($runId, $sessionId, 'Running', 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_versions;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadStepInputHashAsync(
        SqliteConnection connection,
        string stepId,
        string planVersion,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT step_input_hash
            FROM work_steps
            WHERE step_id = $stepId AND plan_version = $planVersion;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        return (string?)await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException($"Work step '{stepId}' did not have an input hash.");
    }

    private static async Task InsertSentToolIntentAsync(
        SqliteConnection connection,
        string sessionId,
        string runId,
        string stepId,
        string planVersion,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_intents(
                call_id, invocation_id, run_id, session_id, tool_id,
                arguments_hash, status, created_at, agent_id, parent_agent_id,
                catalog_version, sent_at, work_step_id, plan_version)
            VALUES(
                'call-step-a', 'invocation-a', $runId, $sessionId, 'builtin.test',
                'arguments-hash', 'sent', '2026-07-23T00:00:00.000Z',
                'worker', 'manager', 'tools-v1', '2026-07-23T00:00:01.000Z',
                $stepId, $planVersion);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task BackdateBackgroundJobAsync(
        SqliteConnection connection,
        string jobId,
        string timestamp,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET created_at = $timestamp,
                started_at = $timestamp
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string[]> ListTableNamesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            ORDER BY name;
            """;
        return await ReadStringArrayAsync(command, ct);
    }

    private static async Task<string[]> ListColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(1));
        }

        return [.. names];
    }

    private static async Task<string[]> ListIndexNamesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index'
            ORDER BY name;
            """;
        return await ReadStringArrayAsync(command, ct);
    }

    private static async Task<string[]> ReadStringArrayAsync(
        SqliteCommand command,
        CancellationToken ct)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed record TerminalAttempt(RunStatus Status, string Text);
}
