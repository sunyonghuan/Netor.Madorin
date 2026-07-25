using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolGatewayTests
{
    private string _workspaceRoot = null!;

    public TestContext TestContext { get; set; }

    [TestInitialize]
    public void Initialize()
    {
        _workspaceRoot = Path.Join(
            Path.GetTempPath(),
            $"madorin-tool-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_PathContainsParentSegment_RejectsAccess()
    {
        var executor = new BuiltinFsToolExecutor();
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "../outside.txt")));

        var result = await executor.ExecuteAsync(invocation, TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "Parent path segments");
    }

    [TestMethod]
    public async Task ExecuteAsync_PathTargetsMadorinDirectory_RejectsAccess()
    {
        var reservedDirectory = Path.Join(_workspaceRoot, ".madorin");
        Directory.CreateDirectory(reservedDirectory);
        await File.WriteAllTextAsync(
            Path.Join(reservedDirectory, "secret.txt"),
            "secret",
            TestContext.CancellationToken);
        var executor = new BuiltinFsToolExecutor();
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", ".madorin/secret.txt")));

        var result = await executor.ExecuteAsync(invocation, TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, ".madorin");
    }

    [TestMethod]
    public async Task ExecuteAsync_PathUsesMadorinCaseVariant_RejectsAccess()
    {
        var executor = new BuiltinFsToolExecutor();
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", ".MADORIN/memory.md")));

        var result = await executor.ExecuteAsync(invocation, TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, ".madorin");
    }

    [TestMethod]
    public async Task ExecuteAsync_LinkTargetsMadorinDirectory_RejectsAccess()
    {
        var reservedDirectory = Path.Join(_workspaceRoot, ".madorin");
        Directory.CreateDirectory(reservedDirectory);
        await File.WriteAllTextAsync(
            Path.Join(reservedDirectory, "memory.md"),
            "secret",
            TestContext.CancellationToken);
        var linkPath = Path.Join(_workspaceRoot, "linked-memory");
        try
        {
            Directory.CreateSymbolicLink(linkPath, reservedDirectory);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"Symbolic links are unavailable in this environment: {ex.Message}");
        }

        var executor = new BuiltinFsToolExecutor();
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "linked-memory/memory.md")));

        var result = await executor.ExecuteAsync(invocation, TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error, "Symbolic links");
    }

    [TestMethod]
    public async Task ExecuteAsync_ReadsUtf8FileWithinWorkspace()
    {
        var filePath = Path.Join(_workspaceRoot, "notes.txt");
        await File.WriteAllTextAsync(
            filePath,
            "hello tools",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.CancellationToken);
        var executor = new BuiltinFsToolExecutor();
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")));

        var result = await executor.ExecuteAsync(invocation, TestContext.CancellationToken);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(
            "hello tools",
            output.RootElement.GetProperty("content").GetString());
        Assert.AreEqual(
            "utf-8",
            output.RootElement.GetProperty("encoding").GetString());
    }

    [TestMethod]
    public async Task ExecuteAsync_SameCallId_ReturnsCachedResultWithoutExecutingAgain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var executor = new CountingToolExecutor(BuiltinToolRegistry.FileReadToolId);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var permission = CreatePermission();
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")),
            callId: "call-idempotent");

        var first = await gateway.ExecuteAsync(
            invocation,
            permission,
            TestContext.CancellationToken);
        var second = await gateway.ExecuteAsync(
            invocation,
            permission,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, first.Kind);
        Assert.AreEqual(ToolGatewayResultKind.Success, second.Kind);
        Assert.AreEqual(1, executor.ExecutionCount);
        Assert.IsNotNull(first.Result);
        Assert.IsNotNull(second.Result);
        Assert.AreEqual(first.Result.OutputJson, second.Result.OutputJson);
        var intent = await repository.GetByCallIdAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual("succeeded", intent.Status);
    }

    [TestMethod]
    public async Task ExecuteAsync_InvocationPermissionContext_PersistsWorkStepLineage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var executor = new CountingToolExecutor(BuiltinToolRegistry.FileReadToolId);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var workPermission = CreatePermission() with
        {
            StepId = "step-1",
            ParentInvocationId = "manager-invocation-1",
            PlanVersion = "3"
        };
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")),
            callId: "call-work-lineage") with
        {
            PermissionContext = workPermission
        };

        var result = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(workPermission.StepId, intent.WorkStepId);
        Assert.AreEqual(workPermission.PlanVersion, intent.PlanVersion);
    }


    [TestMethod]
    public async Task ExecuteAsync_InvocationWorkStepFields_PersistsWorkStepLineage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var executor = new CountingToolExecutor(BuiltinToolRegistry.FileReadToolId);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")),
            callId: "call-work-fields") with
        {
            WorkStepId = "step-fields-1",
            PlanVersion = "7"
        };

        var result = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual("step-fields-1", intent.WorkStepId);
        Assert.AreEqual("7", intent.PlanVersion);
    }

    [TestMethod]
    public async Task ExecuteAsync_WorkStepRetryForCompletedSideEffectTool_ReusesResultWithoutExecutingAgain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
        var descriptor = CreateSideEffectDescriptor();
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var executor = new CountingToolExecutor(descriptor.ToolId);
        using var gateway = new ToolGateway(
            catalogStore,
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var firstInvocation = CreateWorkInvocation(
            descriptor.ToolId,
            "call-side-effect-first");
        var retryInvocation = firstInvocation with
        {
            CallId = "call-side-effect-retry",
            InvocationId = "invocation-side-effect-retry"
        };

        var first = await gateway.ExecuteAsync(
            firstInvocation,
            CreatePermission(),
            catalog,
            TestContext.CancellationToken);
        var retry = await gateway.ExecuteAsync(
            retryInvocation,
            CreatePermission(),
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, first.Kind);
        Assert.AreEqual(ToolGatewayResultKind.Success, retry.Kind);
        Assert.AreEqual(1, executor.ExecutionCount);
        Assert.IsNotNull(first.Result);
        Assert.IsNotNull(retry.Result);
        Assert.AreEqual(retryInvocation.CallId, retry.Result.CallId);
        Assert.AreEqual(first.Result.OutputJson, retry.Result.OutputJson);
        Assert.IsNull(await repository.GetIntentAsync(
            retryInvocation.CallId,
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_WorkStepRetryForUnknownSideEffectTool_ReturnsManualInterventionWithoutExecuting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
        var descriptor = CreateSideEffectDescriptor();
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var executor = new CountingToolExecutor(descriptor.ToolId);
        using var gateway = new ToolGateway(
            catalogStore,
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var originalInvocation = CreateWorkInvocation(
            descriptor.ToolId,
            "call-side-effect-unknown");
        var retryInvocation = originalInvocation with
        {
            CallId = "call-side-effect-unknown-retry",
            InvocationId = "invocation-side-effect-unknown-retry"
        };
        await SeedUnknownWorkStepIntentAsync(
            repository,
            originalInvocation,
            catalog.EffectiveVersion,
            TestContext.CancellationToken);

        var retry = await gateway.ExecuteAsync(
            retryInvocation,
            CreatePermission(),
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, retry.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolResultUnknown, retry.Error?.Code);
        Assert.AreEqual(0, executor.ExecutionCount);
        Assert.IsNull(await repository.GetIntentAsync(
            retryInvocation.CallId,
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_OversizedResult_PersistsBlobAndReusesIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var executor = new LargeResultToolExecutor(BuiltinToolRegistry.FileReadToolId);
        var blobStore = new RecordingBlobStore();
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox,
            resultBlobStore: blobStore,
            runtimeLimits: new RuntimeLimits(MaxInlineContentBytes: 32));
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "large.txt")),
            callId: "call-large-result");

        var first = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(),
            TestContext.CancellationToken);
        var second = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, first.Kind);
        Assert.IsNotNull(first.Result);
        Assert.IsNull(first.Result.OutputJson);
        Assert.IsNotNull(first.Result.ResultBlob);
        Assert.AreEqual(first.Result.ResultBlob, second.Result?.ResultBlob);
        Assert.AreEqual(1, executor.ExecutionCount);
        Assert.AreEqual(1, blobStore.StoreCount);
        var persisted = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(persisted);
        Assert.IsNull(persisted.ResultJson);
        Assert.IsNotNull(persisted.ResultBlob);
        Assert.AreEqual(first.Result.ResultBlob.BlobId, persisted.ResultBlob.BlobId);
        Assert.AreEqual(first.Result.ResultHash, persisted.ResultHash);
    }

    [TestMethod]
    public async Task ExecuteAsync_PrepareFails_CompletesPendingIntentAsFailed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        var executor = new FailingPrepareToolExecutor(BuiltinToolRegistry.FileReadToolId);
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")),
            callId: "call-prepare-failed");

        var result = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolExecutionFailed, result.Error?.Code);
        Assert.AreEqual(1, executor.PreparationCount);
        Assert.AreEqual(0, executor.ExecutionCount);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Failed, intent.Status);
        Assert.AreEqual(RuntimeErrorCodes.ToolExecutionFailed, intent.ErrorCode);
        Assert.IsNotNull(intent.CompletedAt);
        Assert.IsEmpty(await repository.ListRecoverableIntentsAsync(
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_HighRiskProcess_PersistsNormalizedAuditBeforeExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        var runtimePermission = CreateProcessPermission("runtime-process");
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                new RuntimeToolPolicy(runtimePermission)),
            [new BuiltinProcessToolExecutor()],
            repository,
            outbox);
        var invocation = CreateProcessInvocation("call-process-audit");

        var result = await gateway.ExecuteAsync(
            invocation,
            CreateProcessPermission("grant-process"),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_summary, arguments_hash, status, diagnostic_id
            FROM tool_audit
            WHERE call_id = $callId;
            """;
        command.Parameters.AddWithValue("$callId", invocation.CallId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual(
            OperatingSystem.IsWindows() ? "process:dotnet.exe" : "process:dotnet",
            reader.GetString(0));
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation.ArgumentsJson))),
            reader.GetString(1));
        Assert.AreEqual("succeeded", reader.GetString(2));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reader.GetString(3)));
        Assert.IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_ChildHighRiskTool_PersistsAuditLineageAndRootGrant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var descriptor = CreateDestructiveDescriptor("host.destructive.audit");
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var authorization = new ToolAuthorizationService(
            repository,
            RuntimeToolPolicy.CreateRestricted(_workspaceRoot));
        var executor = new PreparedCountingToolExecutor(descriptor.ToolId);
        using var gateway = new ToolGateway(
            new ToolCatalogStore(new BuiltinToolRegistry(), validator),
            validator,
            authorization,
            [executor],
            repository,
            outbox);
        var invocation = CreateChildInvocation(
            descriptor.ToolId,
            "call-child-audit",
            "{}");
        await SaveDelegatedGrantChainAsync(
            authorization,
            invocation,
            descriptor.ToolId,
            ToolRiskLevel.Destructive,
            allowDelete: true);

        var result = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        Assert.AreEqual(1, executor.ExecutionCount);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT call_id,
                   session_id,
                   invocation_id,
                   parent_invocation_id,
                   work_step_id,
                   plan_version,
                   grant_id,
                   root_grant_id,
                   delegation_chain,
                   status,
                   result_hash
            FROM tool_audit
            WHERE call_id = $callId;
            """;
        command.Parameters.AddWithValue("$callId", invocation.CallId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual(invocation.CallId, reader.GetString(0));
        Assert.AreEqual(invocation.SessionId, reader.GetString(1));
        Assert.AreEqual(invocation.InvocationId, reader.GetString(2));
        Assert.AreEqual(invocation.ParentInvocationId, reader.GetString(3));
        Assert.AreEqual(invocation.WorkStepId, reader.GetString(4));
        Assert.AreEqual(invocation.PlanVersion, reader.GetString(5));
        Assert.AreEqual("grant-child", reader.GetString(6));
        Assert.AreEqual("grant-root", reader.GetString(7));
        using var delegationChain = JsonDocument.Parse(reader.GetString(8));
        var rootGrant = Assert.ContainsSingle(delegationChain.RootElement.EnumerateArray());
        Assert.AreEqual("grant-root", rootGrant.GetString());
        Assert.AreEqual("succeeded", reader.GetString(9));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reader.GetString(10)));
        Assert.IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_RevokedRootGrant_BlocksUnsentDescendantCall()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        var authorization = new ToolAuthorizationService(
            repository,
            RuntimeToolPolicy.CreateRestricted(_workspaceRoot));
        var executor = new CountingToolExecutor(BuiltinToolRegistry.FileReadToolId);
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            authorization,
            [executor],
            repository,
            outbox);
        var invocation = CreateChildInvocation(
            BuiltinToolRegistry.FileReadToolId,
            "call-revoked-before-send",
            CreateArguments(("path", "notes.txt")));
        await SaveDelegatedGrantChainAsync(
            authorization,
            invocation,
            BuiltinToolRegistry.FileReadToolId,
            ToolRiskLevel.SensitiveRead,
            allowDelete: false);
        Assert.AreEqual(
            2,
            await authorization.RevokeGrantAsync(
                "grant-root",
                reason: "user revoked",
                ct: TestContext.CancellationToken));

        var result = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.NeedsPermission, result.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolPermissionRequired, result.Error?.Code);
        Assert.AreEqual(0, executor.ExecutionCount);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Pending, intent.Status);
        Assert.IsNull(intent.SentAt);
    }

    [TestMethod]
    public async Task ExecuteAsync_RootGrantRevokedAfterSend_PersistsInvisibleResultAndRejectsReuse()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var descriptor = new ToolDescriptor(
            "host.blocking.read",
            "host",
            "Blocking read",
            "Returns after the test releases the prepared execution.",
            "{}",
            "{}",
            Risk: ToolRiskLevel.SensitiveRead,
            ExecutionTarget: ToolExecutionTarget.Host);
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var authorization = new ToolAuthorizationService(
            repository,
            RuntimeToolPolicy.CreateRestricted(_workspaceRoot));
        var executor = new BlockingPreparedToolExecutor(descriptor.ToolId);
        using var gateway = new ToolGateway(
            new ToolCatalogStore(new BuiltinToolRegistry(), validator),
            validator,
            authorization,
            [executor],
            repository,
            outbox);
        var invocation = CreateChildInvocation(
            descriptor.ToolId,
            "call-revoked-after-send",
            "{}");
        await SaveDelegatedGrantChainAsync(
            authorization,
            invocation,
            descriptor.ToolId,
            ToolRiskLevel.SensitiveRead,
            allowDelete: false);

        var running = gateway.ExecuteAsync(
            invocation,
            permission: null,
            catalog,
            TestContext.CancellationToken);
        await executor.Started.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.CancellationToken);
        Assert.AreEqual(
            2,
            await authorization.RevokeGrantAsync(
                "grant-root",
                reason: "user revoked",
                ct: TestContext.CancellationToken));
        executor.Release();

        var first = await running.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.CancellationToken);
        var second = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, first.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolGrantRevoked, first.Error?.Code);
        Assert.AreEqual(ToolGatewayResultKind.Failed, second.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolGrantRevoked, second.Error?.Code);
        Assert.AreEqual(1, executor.ExecutionCount);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
        Assert.IsFalse(intent.IsResultVisible);
    }

    [TestMethod]
    public async Task ExecuteAsync_HighRiskAuditWriteFails_DoesNotExecutePreparedCall()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_tool_audit
                BEFORE INSERT ON tool_audit
                BEGIN
                    SELECT RAISE(ABORT, 'audit unavailable');
                END;
                """;
            await trigger.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var registry = new BuiltinToolRegistry();
        var executor = new PreparedCountingToolExecutor(BuiltinToolRegistry.ProcessRunToolId);
        var runtimePermission = CreateProcessPermission("runtime-process");
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                new RuntimeToolPolicy(runtimePermission)),
            [executor],
            repository,
            outbox);
        var invocation = CreateProcessInvocation("call-audit-failed");

        var result = await gateway.ExecuteAsync(
            invocation,
            CreateProcessPermission("grant-process"),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolAuditWriteFailed, result.Error?.Code);
        Assert.AreEqual(1, executor.PreparationCount);
        Assert.AreEqual(0, executor.ExecutionCount);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Failed, intent.Status);
        Assert.AreEqual(RuntimeErrorCodes.ToolAuditWriteFailed, intent.ErrorCode);
        Assert.IsNull(intent.SentAt);
    }

    [TestMethod]
    public async Task ExecuteAsync_SentCall_QueriesAndPersistsRecoveredResult()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
        var catalog = catalogStore.CaptureSnapshot();
        var executor = new RecoverableToolExecutor(
            BuiltinToolRegistry.FileReadToolId,
            ToolCallStatus.Succeeded);
        using var gateway = new ToolGateway(
            catalogStore,
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")),
            "call-recovered");
        await SeedSentIntentAsync(repository, invocation, catalog.EffectiveVersion);

        var result = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(),
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        Assert.AreEqual(1, executor.QueryCount);
        Assert.AreEqual(0, executor.ExecutionCount);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
    }

    [TestMethod]
    public async Task ExecuteAsync_NonIdempotentSentCallUnknown_RequiresManualIntervention()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
        var descriptor = new ToolDescriptor(
            "host.payment.capture",
            "host",
            "Capture payment",
            "Captures a payment once.",
            "{}",
            "{}",
            ExecutionTarget: ToolExecutionTarget.Host,
            IsIdempotent: false);
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        var executor = new RecoverableToolExecutor(
            descriptor.ToolId,
            ToolCallStatus.Unknown);
        using var gateway = new ToolGateway(
            catalogStore,
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var invocation = new ToolInvocation(
            "call-unknown",
            descriptor.ToolId,
            "agent-1",
            ParentAgentId: null,
            "{}",
            "run-1",
            "session-1",
            "invocation-1");
        await SeedSentIntentAsync(repository, invocation, catalog.EffectiveVersion);

        var result = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(),
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        Assert.AreEqual(RuntimeErrorCodes.ToolResultUnknown, result.Error?.Code);
        Assert.AreEqual(1, executor.QueryCount);
        Assert.AreEqual(0, executor.ExecutionCount);
        var intent = await repository.GetIntentAsync(
            invocation.CallId,
            TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Unknown, intent.Status);
    }

    [TestMethod]
    public async Task ExecuteAsync_IdempotentSentCallUnknown_ResendsSameCallIdOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
        var catalog = catalogStore.CaptureSnapshot();
        var executor = new RecoverableToolExecutor(
            BuiltinToolRegistry.FileReadToolId,
            ToolCallStatus.Unknown);
        using var gateway = new ToolGateway(
            catalogStore,
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(_workspaceRoot)),
            [executor],
            repository,
            outbox);
        var invocation = CreateFileInvocation(
            BuiltinToolRegistry.FileReadToolId,
            CreateArguments(("path", "notes.txt")),
            "call-resend");
        await SeedSentIntentAsync(repository, invocation, catalog.EffectiveVersion);

        var result = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(),
            catalog,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Success, result.Kind);
        Assert.AreEqual(1, executor.QueryCount);
        Assert.AreEqual(1, executor.ExecutionCount);
        Assert.AreEqual(invocation.CallId, executor.LastExecutedCallId);
    }

    private static async Task SeedSentIntentAsync(
        SqliteToolIntentRepository repository,
        ToolInvocation invocation,
        string catalogVersion)
    {
        var argumentsHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(invocation.ArgumentsJson)));
        var now = DateTimeOffset.UtcNow;
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
                CreatedAt: now,
                SentAt: null,
                CompletedAt: null)));
        Assert.IsTrue(await repository.TryMarkSentAsync(
            invocation.CallId,
            "grant-1",
            null,
            now));
    }

    private static async Task SeedUnknownWorkStepIntentAsync(
        SqliteToolIntentRepository repository,
        ToolInvocation invocation,
        string catalogVersion,
        CancellationToken ct)
    {
        var argumentsHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(invocation.ArgumentsJson)));
        var now = DateTimeOffset.UtcNow;
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
                CreatedAt: now,
                SentAt: null,
                CompletedAt: null,
                WorkStepId: invocation.WorkStepId,
                PlanVersion: invocation.PlanVersion),
            ct));
        Assert.IsTrue(await repository.TryMarkSentAsync(
            invocation.CallId,
            "grant-1",
            null,
            now,
            ct));
        Assert.IsTrue(await repository.TryCompleteIntentAsync(
            new ToolIntentCompletion(
                invocation.CallId,
                ToolIntentStatus.Sent,
                ToolIntentStatus.Unknown,
                ResultJson: null,
                ResultHash: null,
                ResultBlob: null,
                ErrorCode: RuntimeErrorCodes.ToolResultUnknown,
                ErrorMessage: "The previous side-effect result is unknown.",
                IsResultVisible: true,
                CompletedAt: now),
            ct));
    }

    private ToolInvocation CreateFileInvocation(
        string toolId,
        string argumentsJson,
        string? callId = null)
    {
        return new ToolInvocation(
            callId ?? Guid.NewGuid().ToString("N"),
            toolId,
            "agent-1",
            ParentAgentId: null,
            argumentsJson,
            "run-1",
            "session-1",
            InvocationId: "invocation-1",
            PermissionContext: CreatePermission());
    }

    private static ToolInvocation CreateChildInvocation(
        string toolId,
        string callId,
        string argumentsJson) =>
        new(
            callId,
            toolId,
            "agent-child",
            "agent-root",
            argumentsJson,
            "run-1",
            "session-1",
            InvocationId: $"invocation-{callId}",
            WorkStepId: "step-child-1",
            PlanVersion: "plan-v1",
            ParentInvocationId: "invocation-root");

    private static ToolInvocation CreateWorkInvocation(
        string toolId,
        string callId) =>
        new(
            callId,
            toolId,
            "agent-1",
            ParentAgentId: null,
            "{}",
            "run-1",
            "session-1",
            InvocationId: "invocation-side-effect",
            WorkStepId: "work-step-side-effect",
            PlanVersion: "plan-version-1");

    private ToolPermissionContext CreatePermission() =>
        new(
            "grant-1",
            "run-1",
            _workspaceRoot,
            [_workspaceRoot],
            [_workspaceRoot]);

    private static ToolDescriptor CreateSideEffectDescriptor() =>
        new(
            "host.payment.capture",
            "host",
            "Capture payment",
            "Captures a payment once.",
            "{}",
            "{}",
            Risk: ToolRiskLevel.Write,
            ExecutionTarget: ToolExecutionTarget.Host,
            IsIdempotent: false);

    private static ToolDescriptor CreateDestructiveDescriptor(string toolId) =>
        new(
            toolId,
            "host",
            "Destructive host operation",
            "Exercises audit lineage for a high-risk host tool.",
            "{}",
            "{}",
            Risk: ToolRiskLevel.Destructive,
            ExecutionTarget: ToolExecutionTarget.Host,
            IsIdempotent: false);

    private async Task SaveDelegatedGrantChainAsync(
        ToolAuthorizationService authorization,
        ToolInvocation invocation,
        string toolId,
        ToolRiskLevel maximumRisk,
        bool allowDelete)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await authorization.SaveGrantAsync(
            CreateGrant(
                "grant-root",
                "agent-root",
                parentGrantId: null,
                rootGrantId: null,
                toolId,
                allowedCallIds: null,
                maximumRisk,
                expiresAt,
                allowDelegation: true,
                delegatedAgentIds: [invocation.AgentId],
                delegationChain: null,
                allowDelete),
            ct: TestContext.CancellationToken);
        await authorization.SaveGrantAsync(
            CreateGrant(
                "grant-child",
                invocation.AgentId,
                "grant-root",
                "grant-root",
                toolId,
                [invocation.CallId],
                maximumRisk,
                expiresAt,
                allowDelegation: false,
                delegatedAgentIds: [],
                delegationChain: ["grant-root"],
                allowDelete),
            ct: TestContext.CancellationToken);
    }

    private ToolGrant CreateGrant(
        string grantId,
        string agentId,
        string? parentGrantId,
        string? rootGrantId,
        string toolId,
        string[]? allowedCallIds,
        ToolRiskLevel maximumRisk,
        DateTimeOffset expiresAt,
        bool allowDelegation,
        string[] delegatedAgentIds,
        string[]? delegationChain,
        bool allowDelete) =>
        new(
            grantId,
            "run-1",
            _workspaceRoot,
            [_workspaceRoot],
            [_workspaceRoot],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: allowDelete,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            expiresAt,
            allowDelegation,
            delegatedAgentIds,
            AllowedToolIds: [toolId],
            AllowedCallIds: allowedCallIds,
            MaximumRisk: maximumRisk,
            AgentId: agentId,
            ParentGrantId: parentGrantId,
            RootGrantId: rootGrantId,
            DelegationChain: delegationChain);

    private ToolPermissionContext CreateProcessPermission(string grantId) =>
        new(
            grantId,
            "run-1",
            _workspaceRoot,
            [_workspaceRoot],
            [_workspaceRoot],
            AllowedExecutables: ["dotnet"],
            AllowedToolIds: [BuiltinToolRegistry.ProcessRunToolId],
            MaximumRisk: ToolRiskLevel.Process,
            AllowedEnvironmentVariables: []);

    private static ToolInvocation CreateProcessInvocation(string callId) =>
        new(
            callId,
            BuiltinToolRegistry.ProcessRunToolId,
            "agent-1",
            ParentAgentId: null,
            """{"fileName":"dotnet","arguments":["--version"]}""",
            "run-1",
            "session-1",
            InvocationId: "invocation-1");

    private static string CreateArguments(params (string Name, string Value)[] values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class CountingToolExecutor(string toolId) : IToolExecutor
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref _executionCount);
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                $"{{\"content\":\"\",\"encoding\":\"utf-8\",\"byteLength\":0,\"executionCount\":{count}}}"));
        }
    }

    private sealed class FailingPrepareToolExecutor(string toolId) : IToolExecutor
    {
        private int _executionCount;
        private int _preparationCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PreparationCount => Volatile.Read(ref _preparationCount);

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public ValueTask<IPreparedToolExecution> PrepareAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _preparationCount);
            return ValueTask.FromException<IPreparedToolExecution>(
                new UnauthorizedAccessException("The prepared target was rejected."));
        }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executionCount);
            throw new InvalidOperationException("A failed preparation must not execute.");
        }
    }

    private sealed class PreparedCountingToolExecutor(string toolId) : IToolExecutor
    {
        private int _executionCount;
        private int _preparationCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PreparationCount => Volatile.Read(ref _preparationCount);

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public ValueTask<IPreparedToolExecution> PrepareAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _preparationCount);
            return ValueTask.FromResult<IPreparedToolExecution>(
                new PreparedCountingExecution(this, invocation));
        }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default) =>
            ExecutePreparedAsync(invocation, ct);

        private Task<ToolResult> ExecutePreparedAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                """{"exitCode":0,"stdout":"","stderr":""}"""));
        }

        private sealed class PreparedCountingExecution(
            PreparedCountingToolExecutor owner,
            ToolInvocation invocation) : IPreparedToolExecution
        {
            public string TargetSummary => "process:prepared";

            public Task<ToolResult> ExecuteAsync(CancellationToken ct = default) =>
                owner.ExecutePreparedAsync(invocation, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingPreparedToolExecutor(string toolId) : IToolExecutor
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public Task Started => _started.Task;

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public ValueTask<IPreparedToolExecution> PrepareAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IPreparedToolExecution>(
                new BlockingPreparedExecution(this, invocation));
        }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default) =>
            ExecutePreparedAsync(invocation, ct);

        public void Release() => _release.TrySetResult();

        private async Task<ToolResult> ExecutePreparedAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                "{}");
        }

        private sealed class BlockingPreparedExecution(
            BlockingPreparedToolExecutor owner,
            ToolInvocation invocation) : IPreparedToolExecution
        {
            public string TargetSummary => "host:blocking";

            public Task<ToolResult> ExecuteAsync(CancellationToken ct = default) =>
                owner.ExecutePreparedAsync(invocation, ct);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class LargeResultToolExecutor(string toolId) : IToolExecutor
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                $"{{\"content\":\"{new string('x', 128)}\",\"encoding\":\"utf-8\",\"byteLength\":128}}"));
        }
    }

    private sealed class RecordingBlobStore : IToolResultBlobStore
    {
        private int _storeCount;

        public int StoreCount => Volatile.Read(ref _storeCount);

        public Task<BlobReference> StoreAsync(
            string runId,
            ReadOnlyMemory<byte> content,
            string contentType,
            string accessScope,
            DateTimeOffset expiresAt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _storeCount);
            var hash = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
            return Task.FromResult(new BlobReference(
                hash,
                content.Length,
                hash,
                contentType,
                accessScope,
                expiresAt));
        }

        public Task ValidateReferenceAsync(
            BlobReference reference,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecoverableToolExecutor(
        string toolId,
        ToolCallStatus recoveryStatus) : IRecoverableToolExecutor
    {
        private int _executionCount;
        private int _queryCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int QueryCount => Volatile.Read(ref _queryCount);

        public string? LastExecutedCallId { get; private set; }

        public bool CanExecute(string candidateToolId) =>
            string.Equals(toolId, candidateToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executionCount);
            LastExecutedCallId = invocation.CallId;
            return Task.FromResult(CreateResult(invocation));
        }

        public Task<ToolRecoveryResult> QueryResultAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _queryCount);
            var result = recoveryStatus is ToolCallStatus.Succeeded
                or ToolCallStatus.Failed
                or ToolCallStatus.Cancelled
                    ? CreateResult(invocation)
                    : null;
            return Task.FromResult(new ToolRecoveryResult(recoveryStatus, result));
        }

        private static ToolResult CreateResult(ToolInvocation invocation) =>
            new(
                invocation.CallId,
                invocation.ToolId,
                true,
                "{\"content\":\"\",\"encoding\":\"utf-8\",\"byteLength\":0}");
    }
}
