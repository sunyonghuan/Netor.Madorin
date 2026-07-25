using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class RecoveryServiceTests
{
    private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(1);

    private readonly TestContext _testContext;

    public RecoveryServiceTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task RecoverOnStartupAsync_WithRunningRun_MarksRunInterrupted()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, _, runId) = await CreateRunAsync(connection, RuntimeMode.Expert, ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            ct);
        var service = new RecoveryService(repository);

        await service.RecoverOnStartupAsync(ct);

        Assert.AreEqual(
            RunStatus.Interrupted,
            await repository.GetRunStatusAsync(runId, ct));
    }

    [TestMethod]
    public async Task RecoverOnStartupAsync_WithRunningWorkStep_MarksStepAndAttemptInterrupted()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, sessionId, runId) = await CreateRunAsync(
            connection,
            RuntimeMode.Work,
            ct);
        var workRepository = new SqliteWorkRepository(connection);
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [new WorkPlanStepDraft("step-a", "Prepare notes", "worker")]);
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
            ct);
        await workRepository.SavePlanStepsAsync(sessionId, runId, plan, ct);
        var started = await repository.TryStartWorkStepAsync(
            "step-a",
            "1",
            runId,
            sessionId,
            "worker",
            "hash-step-a",
            "invocation-a",
            ct);
        Assert.IsTrue(started);
        var service = new RecoveryService(repository);

        await service.RecoverOnStartupAsync(ct);

        var step = Assert.ContainsSingle(
            await workRepository.ListStepsAsync(sessionId, "1", ct));
        Assert.AreEqual(WorkStepLifecycleStatus.Interrupted, step.Status);
        Assert.AreEqual(
            "interrupted",
            await GetWorkStepAttemptStatusAsync(connection, "step-a", "1", 1, ct));
    }

    [TestMethod]
    public async Task RecoverOnStartupAsync_WithSentRecoverableTool_PersistsRecoveredResultWithoutExecuting()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, sessionId, runId) = await CreateRunAsync(
            connection,
            RuntimeMode.Work,
            ct);
        var workRepository = await CreateRunningWorkStepAsync(
            connection,
            repository,
            sessionId,
            runId,
            ct);
        using var toolRepository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var descriptor = CreateHostToolDescriptor(isIdempotent: false);
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var executor = new RecoverableOnlyExecutor(
            descriptor.ToolId,
            ToolCallStatus.Succeeded,
            new ToolResult("call-capture", descriptor.ToolId, true));
        using var gateway = CreateGateway(toolRepository, outbox, catalog, executor);
        var invocation = CreateInvocation(
            descriptor.ToolId,
            sessionId,
            runId,
            "call-capture");
        await SeedSentIntentAsync(toolRepository, invocation, catalog.EffectiveVersion, ct);
        var service = new RecoveryService(
            repository,
            toolRepository,
            gateway,
            catalog,
            workRepository);

        await service.RecoverOnStartupAsync(ct);

        var intent = await toolRepository.GetIntentAsync(invocation.CallId, ct);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
        Assert.AreEqual(1, executor.QueryCount);
        Assert.AreEqual(0, executor.ExecutionCount);
        var resume = await workRepository.GetResumeStateAsync(sessionId, ct);
        Assert.IsNotNull(resume);
        Assert.IsFalse(resume.RequiresManualIntervention);
    }

    [TestMethod]
    public async Task RecoverOnStartupAsync_WithNonIdempotentUnknownSentTool_MarksManualIntervention()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, sessionId, runId) = await CreateRunAsync(
            connection,
            RuntimeMode.Work,
            ct);
        var workRepository = await CreateRunningWorkStepAsync(
            connection,
            repository,
            sessionId,
            runId,
            ct);
        using var toolRepository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var descriptor = CreateHostToolDescriptor(isIdempotent: false);
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var executor = new RecoverableOnlyExecutor(
            descriptor.ToolId,
            ToolCallStatus.Unknown,
            errorMessage: "Host lost the payment capture result.");
        using var gateway = CreateGateway(toolRepository, outbox, catalog, executor);
        var invocation = CreateInvocation(
            descriptor.ToolId,
            sessionId,
            runId,
            "call-unknown");
        await SeedSentIntentAsync(toolRepository, invocation, catalog.EffectiveVersion, ct);
        var service = new RecoveryService(
            repository,
            toolRepository,
            gateway,
            catalog,
            workRepository);

        await service.RecoverOnStartupAsync(ct);

        var intent = await toolRepository.GetIntentAsync(invocation.CallId, ct);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Unknown, intent.Status);
        Assert.AreEqual(RuntimeErrorCodes.ToolResultUnknown, intent.ErrorCode);
        Assert.AreEqual(1, executor.QueryCount);
        Assert.AreEqual(0, executor.ExecutionCount);
        var resume = await workRepository.GetResumeStateAsync(sessionId, ct);
        Assert.IsNotNull(resume);
        Assert.IsTrue(resume.RequiresManualIntervention);
        Assert.AreEqual(WorkSessionStatus.Interrupted, resume.Status);
        var step = Assert.ContainsSingle(resume.Steps);
        Assert.AreEqual(WorkStepLifecycleStatus.Interrupted, step.Status);
        StringAssert.Contains(step.ErrorMessage, "Host lost the payment capture result.");
    }

    [TestMethod]
    public async Task ResumeSessionAsync_WithExistingSession_ReturnsLatestRunState()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, sessionId, runId) = await CreateRunAsync(
            connection,
            RuntimeMode.Work,
            ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct);
        var service = new RecoveryService(repository);

        var resumeInfo = await service.ResumeSessionAsync(sessionId, ct);

        Assert.IsNotNull(resumeInfo);
        Assert.AreEqual(sessionId, resumeInfo.SessionId);
        Assert.AreEqual(RuntimeMode.Work, resumeInfo.Mode);
        Assert.AreEqual(runId, resumeInfo.LastRunId);
        Assert.AreEqual(RunStatus.Preparing.ToString(), resumeInfo.LastStatus);
    }

    private static async Task<SqliteWorkRepository> CreateRunningWorkStepAsync(
        SqliteConnection connection,
        SqliteSessionRepository repository,
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        var workRepository = new SqliteWorkRepository(connection);
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [new WorkPlanStepDraft("step-a", "Capture payment", "worker")]);
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
            ct);
        await workRepository.SavePlanStepsAsync(sessionId, runId, plan, ct);
        Assert.IsTrue(await repository.TryStartWorkStepAsync(
            "step-a",
            "1",
            runId,
            sessionId,
            "worker",
            "hash-step-a",
            "invocation-a",
            ct));
        return workRepository;
    }

    private static async Task<string?> GetWorkStepAttemptStatusAsync(
        SqliteConnection connection,
        string stepId,
        string planVersion,
        int attemptNumber,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM work_step_attempts
            WHERE step_id = $stepId
              AND plan_version = $planVersion
              AND attempt_number = $attemptNumber;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        command.Parameters.AddWithValue("$attemptNumber", attemptNumber);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static ToolDescriptor CreateHostToolDescriptor(bool isIdempotent) =>
        new(
            "host.payment.capture",
            "host",
            "Capture payment",
            "Captures a payment once.",
            "{}",
            "{}",
            Risk: ToolRiskLevel.Write,
            ExecutionTarget: ToolExecutionTarget.Host,
            IsIdempotent: isIdempotent);

    private static ToolInvocation CreateInvocation(
        string toolId,
        string sessionId,
        string runId,
        string callId) =>
        new(
            callId,
            toolId,
            "worker",
            ParentAgentId: null,
            "{}",
            runId,
            sessionId,
            "invocation-a",
            WorkStepId: "step-a",
            PlanVersion: "1");

    private static ToolGateway CreateGateway(
        IToolStateStore toolRepository,
        IEventOutbox outbox,
        ToolCatalogSnapshot catalog,
        IToolExecutor executor)
    {
        var validator = new JsonSchemaToolValidator();
        return new ToolGateway(
            new FixedToolCatalogStore(catalog),
            validator,
            new GrantingAuthorizationService(),
            [executor],
            toolRepository,
            outbox);
    }

    private static async Task SeedSentIntentAsync(
        SqliteToolIntentRepository repository,
        ToolInvocation invocation,
        string catalogVersion,
        CancellationToken ct)
    {
        var argumentsHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(invocation.ArgumentsJson)));
        Assert.IsTrue(await repository.TryCreateIntentAsync(
            new ToolIntentState(
                invocation.CallId,
                invocation.InvocationId ?? invocation.CallId,
                invocation.RunId,
                invocation.SessionId,
                invocation.AgentId,
                invocation.ParentAgentId,
                invocation.ToolId,
                catalogVersion,
                argumentsHash,
                ToolIntentStatus.Pending,
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
                WorkStepId: invocation.WorkStepId,
                PlanVersion: invocation.PlanVersion),
            ct));
        Assert.IsTrue(await repository.TryMarkSentAsync(
            invocation.CallId,
            "grant-1",
            approvalRequestId: null,
            DateTimeOffset.UtcNow,
            ct));
    }

    private static async Task<(SqliteSessionRepository Repository, string SessionId, string RunId)>
        CreateRunAsync(
            SqliteConnection connection,
            RuntimeMode mode,
            CancellationToken ct)
    {
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            mode,
            Guid.NewGuid().ToString("N"),
            KeyRetention,
            ct);
        var runId = await repository.CreateRunAsync(
            sessionId,
            Guid.NewGuid().ToString("N"),
            KeyRetention,
            ct);
        return (repository, sessionId, runId);
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }

    private sealed class FixedToolCatalogStore(ToolCatalogSnapshot snapshot) : IToolCatalogStore
    {
        public ToolCatalogSnapshot CaptureSnapshot() => snapshot;

        public ToolCatalogSnapshot ReplaceHostCatalog(ToolCatalogReplaceRequest request) => snapshot;

        public ToolCatalogSnapshot PatchHostCatalog(ToolCatalogPatchRequest request) => snapshot;
    }

    private sealed class GrantingAuthorizationService : IToolAuthorizationService
    {
        public Task<ToolAuthorizationResult> AuthorizeAsync(
            ToolInvocation invocation,
            ToolDescriptor descriptor,
            ToolPermissionContext? suppliedPermission = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ToolAuthorizationResult(
                ToolAuthorizationDecision.Granted,
                suppliedPermission
                    ?? new ToolPermissionContext(
                        "grant-1",
                        invocation.RunId,
                        Environment.CurrentDirectory,
                        [],
                        [])));

        public Task SaveGrantAsync(
            ToolGrant grant,
            string? agentId = null,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> RevokeGrantAsync(
            string grantId,
            bool revokeDescendants = true,
            string? reason = null,
            CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class RecoverableOnlyExecutor(
        string toolId,
        ToolCallStatus recoveryStatus,
        ToolResult? result = null,
        string? errorMessage = null) : IRecoverableToolExecutor
    {
        public int ExecutionCount { get; private set; }

        public int QueryCount { get; private set; }

        public bool CanExecute(string candidateToolId) =>
            string.Equals(candidateToolId, toolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(
                result
                ?? new ToolResult(invocation.CallId, invocation.ToolId, true));
        }

        public Task<ToolRecoveryResult> QueryResultAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            QueryCount++;
            return Task.FromResult(new ToolRecoveryResult(
                recoveryStatus,
                result,
                ErrorCode: recoveryStatus is ToolCallStatus.Unknown
                    ? RuntimeErrorCodes.ToolResultUnknown
                    : null,
                ErrorMessage: errorMessage));
        }
    }
}
