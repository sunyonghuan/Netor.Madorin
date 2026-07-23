using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class MemoryToolTests
{
    private static readonly string?[] AppendScopes = ["global", "project"];
    private static readonly string?[] ReadScopes = ["global", "project", "effective"];

    private string _root = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Join(
            Path.GetTempPath(),
            $"madorin-memory-tool-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Registry_MemoryTools_FreezeIdsSchemasAndApprovalRequirement()
    {
        var registry = new BuiltinToolRegistry();

        var read = Assert.ContainsSingle(registry.GetTools(
            [BuiltinToolRegistry.MemoryReadToolId]));
        var append = Assert.ContainsSingle(registry.GetTools(
            [BuiltinToolRegistry.MemoryAppendToolId]));

        Assert.IsFalse(read.RequiresApproval);
        Assert.IsTrue(append.RequiresApproval);
        using var readInput = JsonDocument.Parse(read.InputSchemaJson);
        using var readOutput = JsonDocument.Parse(read.OutputSchemaJson);
        using var appendInput = JsonDocument.Parse(append.InputSchemaJson);
        using var appendOutput = JsonDocument.Parse(append.OutputSchemaJson);
        CollectionAssert.AreEquivalent(
            ReadScopes,
            readInput.RootElement
                .GetProperty("properties")
                .GetProperty("scope")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.IsTrue(readOutput.RootElement
            .GetProperty("properties")
            .TryGetProperty("hash", out _));
        CollectionAssert.AreEquivalent(
            AppendScopes,
            appendInput.RootElement
                .GetProperty("properties")
                .GetProperty("scope")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.AreEqual(
            "nextInvocation",
            appendOutput.RootElement
                .GetProperty("properties")
                .GetProperty("appliesFrom")
                .GetProperty("const")
                .GetString());
    }

    [TestMethod]
    public async Task ExecuteAsync_Read_ReturnsContentAndHashFromFixedScope()
    {
        var service = CreateMemoryService();
        const string content = "# Memory\n\n- Read this rule.\n";
        await service.ReplaceAsync(
            MemoryScope.Project,
            content,
            TestContext.CancellationToken);
        var snapshots = new MemoryInvocationSnapshotStore();
        var context = await new AgentContextComposer(service).ComposeAsync(
            "System prompt.",
            TestContext.CancellationToken);
        snapshots.Capture("invocation-1", context);
        await service.ReplaceAsync(
            MemoryScope.Project,
            "# Memory\n\n- A newer rule.\n",
            TestContext.CancellationToken);
        var executor = new BuiltinMemoryToolExecutor(service, snapshots);
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryReadToolId,
            "{\"scope\":\"project\"}");

        var result = await executor.ExecuteAsync(
            invocation,
            TestContext.CancellationToken);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(content, output.RootElement.GetProperty("content").GetString());
        Assert.AreEqual(
            context.ProjectMemoryHash,
            output.RootElement.GetProperty("hash").GetString());
        Assert.IsTrue(snapshots.Release("invocation-1"));
    }

    [TestMethod]
    public async Task ExecuteAsync_AppendWithoutApproval_DoesNotWrite()
    {
        var service = CreateMemoryService();
        var executor = new BuiltinMemoryToolExecutor(
            service,
            new MemoryInvocationSnapshotStore());
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryAppendToolId,
            "{\"scope\":\"global\",\"item\":\"Do not write\"}");

        var result = await executor.ExecuteAsync(
            invocation,
            TestContext.CancellationToken);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "approval");
        Assert.IsFalse(File.Exists(service.GlobalPath));
    }

    [TestMethod]
    public async Task Gateway_ApprovedAppend_RoutesLocallyAndReturnsNextInvocationHash()
    {
        var service = CreateMemoryService();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        using var repository = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var registry = new FakeMemoryToolRegistry();
        var validator = new JsonSchemaToolValidator();
        using var gateway = new ToolGateway(
            new ToolCatalogStore(registry, validator),
            validator,
            new ToolAuthorizationService(
                repository,
                RuntimeToolPolicy.CreateRestricted(Path.Join(_root, "workspace"))),
            [new BuiltinMemoryToolExecutor(service, new MemoryInvocationSnapshotStore())],
            repository,
            outbox);
        var invocation = CreateInvocation(
            BuiltinToolRegistry.MemoryAppendToolId,
            "{\"scope\":\"global\",\"item\":\"Approved rule\"}");

        var unapproved = await gateway.ExecuteAsync(
            invocation,
            permission: null,
            TestContext.CancellationToken);
        var approved = await gateway.ExecuteAsync(
            invocation,
            CreatePermission(invocation.CallId),
            TestContext.CancellationToken);

        Assert.AreEqual(ToolGatewayResultKind.NeedsApproval, unapproved.Kind);
        Assert.AreEqual(ToolGatewayResultKind.Success, approved.Kind);
        Assert.IsNotNull(approved.Result);
        using var output = JsonDocument.Parse(approved.Result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("appended").GetBoolean());
        Assert.AreEqual(
            "nextInvocation",
            output.RootElement.GetProperty("appliesFrom").GetString());
        Assert.AreEqual(
            await service.GetHashAsync(
                MemoryScope.Global,
                TestContext.CancellationToken),
            output.RootElement.GetProperty("hash").GetString());
        StringAssert.Contains(
            await service.ReadAsync(
                MemoryScope.Global,
                TestContext.CancellationToken),
            "- Approved rule\n");
    }

    private MemoryFileService CreateMemoryService() => new(
        Path.Join(_root, "home"),
        Path.Join(_root, "workspace"));

    private ToolPermissionContext CreatePermission(string callId) => new(
        "approval-1",
        "run-1",
        Path.Join(_root, "workspace"),
        [],
        [],
        AllowedCallIds: [callId],
        ApprovalRequestId: "approval-request-1");

    private static ToolInvocation CreateInvocation(string toolId, string argumentsJson) => new(
        Guid.NewGuid().ToString("N"),
        toolId,
        "agent-1",
        ParentAgentId: null,
        argumentsJson,
        "run-1",
        "session-1",
        InvocationId: "invocation-1");

    private sealed class FakeMemoryToolRegistry : IBuiltinToolRegistry
    {
        private static readonly string[] MemoryToolIds =
        [
            BuiltinToolRegistry.MemoryReadToolId,
            BuiltinToolRegistry.MemoryAppendToolId
        ];

        private readonly IReadOnlyList<ToolDescriptor> _tools =
            new BuiltinToolRegistry().GetTools(MemoryToolIds);

        public string CatalogVersion => "memory-test-v1";

        public IReadOnlyList<ToolDescriptor> GetTools() => _tools;

        public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
        {
            ArgumentNullException.ThrowIfNull(toolIds);
            var requested = toolIds.ToHashSet(StringComparer.Ordinal);
            return _tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
        }
    }
}
