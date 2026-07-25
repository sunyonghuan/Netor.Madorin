using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolAuthorizationTests
{
    private string _workspace = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Path.Join(
            Path.GetTempPath(),
            $"madorin-authorization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(_workspace, "src"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [TestMethod]
    public void Intersect_NarrowsEveryPermissionDimension()
    {
        var sourceRoot = Path.Join(_workspace, "src");
        var runtime = new ToolPermissionContext(
            "runtime",
            "run-1",
            _workspace,
            [_workspace],
            [_workspace],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            DenyNetwork: true,
            MaximumRisk: ToolRiskLevel.Write,
            AllowedEnvironmentVariables: []);
        var host = new ToolPermissionContext(
            "host",
            "run-1",
            _workspace,
            [sourceRoot],
            [sourceRoot],
            AllowOverwrite: true,
            AllowMove: true,
            AllowDelete: true,
            AllowedExecutables: ["dotnet"],
            AllowPowerShell: true,
            DenyNetwork: false,
            AllowedToolIds: ["host.read"],
            MaximumRisk: ToolRiskLevel.SensitiveRead,
            AllowedEnvironmentVariables: ["PATH"],
            NetworkPolicy: new NetworkPolicy(false, ["example.test"]),
            StepId: "step-1",
            ParentInvocationId: "manager-invocation-1",
            PlanVersion: "3");

        var effective = ToolGrantCalculator.Intersect([runtime, host]);

        CollectionAssert.AreEqual(new[] { sourceRoot }, effective.AllowedReadRoots);
        CollectionAssert.AreEqual(new[] { sourceRoot }, effective.AllowedWriteRoots);
        CollectionAssert.AreEqual(host.AllowedToolIds, effective.AllowedToolIds);
        Assert.AreEqual(ToolRiskLevel.SensitiveRead, effective.MaximumRisk);
        Assert.IsFalse(effective.AllowOverwrite);
        Assert.IsFalse(effective.AllowPowerShell);
        Assert.IsTrue(effective.DenyNetwork);
        Assert.IsEmpty(effective.AllowedExecutables);
        Assert.IsNotNull(effective.AllowedEnvironmentVariables);
        Assert.IsEmpty(effective.AllowedEnvironmentVariables);
        Assert.AreEqual(host.StepId, effective.StepId);
        Assert.AreEqual(host.ParentInvocationId, effective.ParentInvocationId);
        Assert.AreEqual(host.PlanVersion, effective.PlanVersion);
    }

    [TestMethod]
    public async Task AuthorizeAsync_ChildAgentRequiresNarrowDelegatedGrant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var service = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        var invocation = new ToolInvocation(
            "call-1",
            "host.read",
            "agent-child",
            "agent-root",
            "{}",
            "run-1",
            "session-1",
            "invocation-1");
        var descriptor = new ToolDescriptor(
            invocation.ToolId,
            "host",
            "Read",
            "Reads one host resource.",
            "{\"type\":\"object\"}",
            "{\"type\":\"object\"}",
            Risk: ToolRiskLevel.SensitiveRead,
            ExecutionTarget: ToolExecutionTarget.Host,
            RequiresApproval: true);

        var withoutGrant = await service.AuthorizeAsync(
            invocation,
            descriptor,
            ct: TestContext.CancellationToken);
        Assert.AreEqual(ToolAuthorizationDecision.NeedsApproval, withoutGrant.Decision);

        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await service.SaveGrantAsync(
            CreateGrant(
                "grant-root",
                "agent-root",
                parentGrantId: null,
                rootGrantId: null,
                allowedCallIds: null,
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: true),
            ct: TestContext.CancellationToken);

        var withRootGrantOnly = await service.AuthorizeAsync(
            invocation,
            descriptor,
            ct: TestContext.CancellationToken);
        Assert.AreEqual(ToolAuthorizationDecision.NeedsApproval, withRootGrantOnly.Decision);

        await service.SaveGrantAsync(
            CreateGrant(
                "grant-child",
                "agent-child",
                "grant-root",
                "grant-root",
                [invocation.CallId],
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: false,
                delegationChain: ["grant-root"]),
            ct: TestContext.CancellationToken);

        var granted = await service.AuthorizeAsync(
            invocation,
            descriptor,
            ct: TestContext.CancellationToken);
        Assert.AreEqual(ToolAuthorizationDecision.Granted, granted.Decision);
        Assert.IsNotNull(granted.EffectivePermission);
        Assert.AreEqual("grant-child", granted.EffectivePermission.GrantId);
        CollectionAssert.AreEqual(
            new[] { invocation.CallId },
            granted.EffectivePermission.AllowedCallIds);
    }

    [TestMethod]
    public async Task ResolveAndExecuteAsync_NeedsPermissionRequest_IncludesWorkLineage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var authorization = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        using var gateway = new ToolGateway(
            new ToolCatalogStore(new BuiltinToolRegistry(), validator),
            validator,
            authorization,
            [],
            store,
            outbox);
        var descriptor = new ToolDescriptor(
            "host.read",
            "host",
            "Read",
            "Reads one host resource.",
            "{\"type\":\"object\"}",
            "{\"type\":\"object\"}",
            Risk: ToolRiskLevel.SensitiveRead,
            ExecutionTarget: ToolExecutionTarget.Host);
        var catalog = new ToolCatalogSnapshot("host-v1", "custom-v1", [descriptor]);
        ToolPermissionRequest? captured = null;
        var coordinator = new ToolConsentCoordinator(
            gateway,
            authorization,
            catalog,
            toolPermissionHandler: (permissionRequest, _) =>
            {
                captured = permissionRequest;
                return Task.FromResult(new ToolPermissionResponse(
                    permissionRequest.CorrelationId ?? string.Empty,
                    permissionRequest.CallId,
                    ToolAuthorizationDecision.Denied,
                    Reason: "denied"));
            });
        var invocation = new ToolInvocation(
            "call-1",
            descriptor.ToolId,
            "agent-child",
            "agent-root",
            "{}",
            "run-1",
            "session-1",
            "invocation-child",
            WorkStepId: "step-1",
            PlanVersion: "plan-v1",
            ParentInvocationId: "invocation-root");
        var pending = new ToolGatewayResult(
            ToolGatewayResultKind.NeedsPermission,
            Error: new RuntimeError(
                RuntimeErrorCodes.ToolPermissionRequired,
                "tool",
                "No effective Grant permits tool 'host.read'.",
                false,
                ProviderDetails: null,
                "diag-permission"));

        var result = await coordinator.ResolveAndExecuteAsync(
            invocation,
            pending,
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.Failed, result.Kind);
        Assert.IsNotNull(captured);
        Assert.AreEqual(invocation.AgentId, captured.AgentId);
        Assert.AreEqual(invocation.ParentAgentId, captured.ParentAgentId);
        Assert.AreEqual(invocation.SessionId, captured.SessionId);
        Assert.AreEqual(invocation.InvocationId, captured.InvocationId);
        Assert.AreEqual(invocation.ParentInvocationId, captured.ParentInvocationId);
        Assert.AreEqual(invocation.WorkStepId, captured.WorkStepId);
        Assert.AreEqual(invocation.PlanVersion, captured.PlanVersion);
    }

    [TestMethod]
    public async Task SaveGrantAsync_ChildWidensRisk_RejectsDelegation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var service = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await service.SaveGrantAsync(
            CreateGrant(
                "grant-root",
                "agent-root",
                parentGrantId: null,
                rootGrantId: null,
                allowedCallIds: null,
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: true),
            ct: TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await service.SaveGrantAsync(
                CreateGrant(
                    "grant-child",
                    "agent-child",
                    "grant-root",
                    "grant-root",
                    ["call-1"],
                    ToolRiskLevel.Write,
                    expiresAt,
                    allowDelegation: false,
                    delegationChain: ["grant-root"]),
                ct: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AuthorizeAsync_ChildGrantIntersectsRuntimeParentAndLeafScopes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var runtimeMaximum = new ToolPermissionContext(
            "runtime-maximum",
            "*",
            _workspace,
            [Path.Join(_workspace, "src")],
            [],
            AllowedToolIds: ["host.read"],
            MaximumRisk: ToolRiskLevel.SensitiveRead);
        var service = new ToolAuthorizationService(
            store,
            new RuntimeToolPolicy(
                RuntimeToolPolicy.CreateRestricted(_workspace).DefaultPermission,
                runtimeMaximum));
        var invocation = new ToolInvocation(
            "call-intersection",
            "host.read",
            "agent-child",
            "agent-root",
            "{}",
            "run-1",
            "session-1",
            "invocation-child");
        var descriptor = new ToolDescriptor(
            invocation.ToolId,
            "host",
            "Read",
            "Reads one host resource.",
            "{\"type\":\"object\"}",
            "{\"type\":\"object\"}",
            Risk: ToolRiskLevel.SensitiveRead,
            ExecutionTarget: ToolExecutionTarget.Host);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await service.SaveGrantAsync(
            CreateGrant(
                "grant-root",
                "agent-root",
                parentGrantId: null,
                rootGrantId: null,
                allowedCallIds: null,
                ToolRiskLevel.Write,
                expiresAt,
                allowDelegation: true),
            ct: TestContext.CancellationToken);
        await service.SaveGrantAsync(
            CreateGrant(
                "grant-child",
                invocation.AgentId,
                "grant-root",
                "grant-root",
                [invocation.CallId],
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: false,
                delegationChain: ["grant-root"]),
            ct: TestContext.CancellationToken);

        var granted = await service.AuthorizeAsync(
            invocation,
            descriptor,
            ct: TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, granted.Decision);
        Assert.IsNotNull(granted.EffectivePermission);
        var effective = granted.EffectivePermission;
        Assert.AreEqual("grant-child", effective.GrantId);
        Assert.AreEqual("grant-root", effective.RootGrantId);
        Assert.IsNotNull(effective.DelegationChain);
        Assert.HasCount(1, effective.DelegationChain);
        Assert.AreEqual("grant-root", effective.DelegationChain[0]);
        Assert.HasCount(1, effective.AllowedReadRoots);
        Assert.AreEqual(
            Path.GetFullPath(Path.Join(_workspace, "src")),
            effective.AllowedReadRoots[0]);
        Assert.IsNotNull(effective.AllowedToolIds);
        Assert.HasCount(1, effective.AllowedToolIds);
        Assert.AreEqual(invocation.ToolId, effective.AllowedToolIds[0]);
        Assert.IsNotNull(effective.AllowedCallIds);
        Assert.HasCount(1, effective.AllowedCallIds);
        Assert.AreEqual(invocation.CallId, effective.AllowedCallIds[0]);
        Assert.AreEqual(
            ToolRiskLevel.SensitiveRead,
            effective.MaximumRisk);
    }

    [TestMethod]
    public async Task SaveGrantAsync_GrandchildRequiresImmediateParentDelegationAndAgentScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var service = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await service.SaveGrantAsync(
            CreateGrant(
                "grant-root",
                "agent-root",
                parentGrantId: null,
                rootGrantId: null,
                allowedCallIds: null,
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: true,
                delegatedAgentIds: ["agent-middle", "agent-middle-no-agent"]),
            ct: TestContext.CancellationToken);
        await service.SaveGrantAsync(
            CreateGrant(
                "grant-middle-blocked",
                "agent-middle",
                "grant-root",
                "grant-root",
                allowedCallIds: null,
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: false,
                delegationChain: ["grant-root"]),
            ct: TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await service.SaveGrantAsync(
                CreateGrant(
                    "grant-leaf-blocked",
                    "agent-child",
                    "grant-middle-blocked",
                    "grant-root",
                    ["call-1"],
                    ToolRiskLevel.SensitiveRead,
                    expiresAt,
                    allowDelegation: false,
                    delegationChain: ["grant-root", "grant-middle-blocked"]),
                ct: TestContext.CancellationToken));

        await service.SaveGrantAsync(
            CreateGrant(
                "grant-middle-no-agent",
                "agent-middle-no-agent",
                "grant-root",
                "grant-root",
                allowedCallIds: null,
                ToolRiskLevel.SensitiveRead,
                expiresAt,
                allowDelegation: true,
                delegationChain: ["grant-root"],
                delegatedAgentIds: ["agent-other"]),
            ct: TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await service.SaveGrantAsync(
                CreateGrant(
                    "grant-leaf-wrong-agent",
                    "agent-child",
                    "grant-middle-no-agent",
                    "grant-root",
                    ["call-1"],
                    ToolRiskLevel.SensitiveRead,
                    expiresAt,
                    allowDelegation: false,
                    delegationChain: ["grant-root", "grant-middle-no-agent"]),
                ct: TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AuthorizeAsync_ExplicitGrantCanElevateRestrictedProcessDefault()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var store = new SqliteToolIntentRepository(connection);
        var service = new ToolAuthorizationService(
            store,
            RuntimeToolPolicy.CreateRestricted(_workspace));
        var invocation = new ToolInvocation(
            "call-process",
            "builtin.process.run",
            "agent-root",
            ParentAgentId: null,
            "{}",
            "run-1",
            "session-1",
            "invocation-process");
        var descriptor = new ToolDescriptor(
            invocation.ToolId,
            "builtin",
            "Run process",
            "Runs an allowlisted process.",
            "{\"type\":\"object\"}",
            "{\"type\":\"object\"}",
            Risk: ToolRiskLevel.Process,
            Capabilities: ["process.execute"]);

        var denied = await service.AuthorizeAsync(
            invocation,
            descriptor,
            ct: TestContext.CancellationToken);
        Assert.AreEqual(ToolAuthorizationDecision.Denied, denied.Decision);

        await service.SaveGrantAsync(
            new ToolGrant(
                "grant-process",
                invocation.RunId,
                _workspace,
                [],
                [],
                AllowOverwrite: false,
                AllowMove: false,
                AllowDelete: false,
                AllowedExecutables: ["dotnet"],
                AllowPowerShell: false,
                new NetworkPolicy(),
                DateTimeOffset.UtcNow.AddHours(1),
                AllowDelegation: false,
                DelegatedAgentIds: [],
                AllowedToolIds: [invocation.ToolId],
                MaximumRisk: ToolRiskLevel.Process,
                AgentId: invocation.AgentId),
            ct: TestContext.CancellationToken);

        var granted = await service.AuthorizeAsync(
            invocation,
            descriptor,
            ct: TestContext.CancellationToken);

        Assert.AreEqual(ToolAuthorizationDecision.Granted, granted.Decision);
        Assert.IsNotNull(granted.EffectivePermission);
        Assert.HasCount(1, granted.EffectivePermission.AllowedExecutables);
        Assert.AreEqual("dotnet", granted.EffectivePermission.AllowedExecutables[0]);
        Assert.AreEqual(ToolRiskLevel.Process, granted.EffectivePermission.MaximumRisk);
    }

    private ToolGrant CreateGrant(
        string grantId,
        string agentId,
        string? parentGrantId,
        string? rootGrantId,
        string[]? allowedCallIds,
        ToolRiskLevel maximumRisk,
        DateTimeOffset expiresAt,
        bool allowDelegation,
        string[]? delegationChain = null,
        string[]? delegatedAgentIds = null) =>
        new(
            grantId,
            "run-1",
            _workspace,
            [Path.Join(_workspace, "src")],
            [],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            expiresAt,
            allowDelegation,
            delegatedAgentIds ?? ["agent-child"],
            AllowedToolIds: ["host.read"],
            AllowedCallIds: allowedCallIds,
            MaximumRisk: maximumRisk,
            AgentId: agentId,
            ParentGrantId: parentGrantId,
            RootGrantId: rootGrantId,
            DelegationChain: delegationChain);
}
