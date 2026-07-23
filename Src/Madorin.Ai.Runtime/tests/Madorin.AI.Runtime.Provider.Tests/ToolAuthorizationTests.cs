using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
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
        string[]? delegationChain = null) =>
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
            ["agent-child"],
            AllowedToolIds: ["host.read"],
            AllowedCallIds: allowedCallIds,
            MaximumRisk: maximumRisk,
            AgentId: agentId,
            ParentGrantId: parentGrantId,
            RootGrantId: rootGrantId,
            DelegationChain: delegationChain);
}
