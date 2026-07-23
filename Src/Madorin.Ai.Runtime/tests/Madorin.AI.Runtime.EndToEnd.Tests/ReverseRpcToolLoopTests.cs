using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Transport.NamedPipes;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReverseRpcToolLoopTests
{
    private const string HostToolId = "host.lookup";
    private const string McpToolId = "mcp.orders.create";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Server_HostAndMcpTools_RequestsConsentPersistsBlobAndContinuesProvider()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ReverseToolProvider();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.reverse-tools.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(timeout.Token);

            var host = new FakeToolHost(client, workspace);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            await PublishCatalogAsync(client, timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest(),
                timeout.Token);
            var completed = await ReadTerminalRunEventAsync(
                client,
                runId,
                timeout.Token);
            if (completed.MessageType == MessageTypes.RunFailed)
            {
                var failed = JsonSerializer.Deserialize(
                    completed.Payload,
                    RuntimeJsonContext.Default.RunFailedEvent);
                Assert.Fail($"Run failed with {failed?.Error.Code}: {failed?.Error.Message}");
            }

            Assert.AreEqual(MessageTypes.RunCompleted, completed.MessageType);
            var completedPayload = JsonSerializer.Deserialize(
                completed.Payload,
                RuntimeJsonContext.Default.RunCompletedEvent);
            var run = await client.QueryRunAsync(runId, timeout.Token);

            Assert.IsNotNull(completedPayload);
            Assert.AreEqual("Host and MCP results were applied.", run.TerminalText);
            Assert.AreEqual(3, provider.CallCount);
            Assert.AreEqual(1, host.PermissionRequestCount);
            Assert.AreEqual(1, host.ApprovalRequestCount);
            Assert.AreEqual(1, host.GetExecutionCount("host-call"));
            Assert.AreEqual(1, host.GetExecutionCount("mcp-call"));
            Assert.IsTrue(provider.ObservedBlobResult);
            await AssertApprovalChainAsync(workspace, timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static async Task PublishCatalogAsync(
        RuntimeClient client,
        CancellationToken ct)
    {
        var request = new ToolCatalogReplaceRequest(
            "host-v1",
            [
                CreateCatalogItem(HostToolId, ToolExecutionTarget.Host, requiresApproval: false),
                CreateCatalogItem(McpToolId, ToolExecutionTarget.Mcp, requiresApproval: true)
            ]);
        var response = await client.ControlPeer.SendRequestAsync(
            MessageTypes.ToolCatalogReplace,
            JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.ToolCatalogReplaceRequest),
            TimeSpan.FromSeconds(5),
            ct);
        Assert.IsNull(response.Error);
        Assert.IsNotNull(response.Result);
        var update = JsonSerializer.Deserialize(
            response.Result.Value,
            RuntimeJsonContext.Default.ToolCatalogUpdateResponse);
        Assert.IsNotNull(update);
        Assert.IsGreaterThanOrEqualTo(2, update.ToolCount);
    }

    private static async Task AssertApprovalChainAsync(
        string workspace,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Join(workspace, ".madorin", "state.db"),
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.approval_request_id,
                   a.decision,
                   a.grant_id,
                   i.approval_request_id,
                   g.approval_request_id
            FROM tool_approvals a
            JOIN tool_intents i ON i.call_id = a.call_id
            JOIN tool_grants g ON g.grant_id = a.grant_id
            WHERE a.call_id = 'mcp-call';
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.IsTrue(await reader.ReadAsync(ct));
        Assert.AreEqual("granted", reader.GetString(1));
        Assert.AreEqual("grant-mcp-call", reader.GetString(2));
        Assert.AreEqual(reader.GetString(0), reader.GetString(3));
        Assert.AreEqual(reader.GetString(0), reader.GetString(4));
        Assert.IsFalse(await reader.ReadAsync(ct));
    }

    private static ToolCatalogItem CreateCatalogItem(
        string toolId,
        ToolExecutionTarget target,
        bool requiresApproval) =>
        new(
            toolId,
            toolId,
            "Test reverse RPC tool.",
            ParseElement("""{"type":"object","additionalProperties":true}"""),
            ParseElement("""{"type":"object","additionalProperties":true}"""),
            requiresApproval ? ToolRiskLevel.Write : ToolRiskLevel.Low,
            10,
            ["test"],
            [],
            target,
            requiresApproval,
            IsIdempotent: false);

    private static NewSessionRunRequest CreateRequest() =>
        new(
            "reverse-session",
            "reverse-run",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("reverse-provider", "reverse-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "Use the host tools."))),
            [new TextContentBlock("Run both tools.")]);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-reverse-tool-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<RuntimeEventEnvelope> ReadTerminalRunEventAsync(
        RuntimeClient client,
        string runId,
        CancellationToken ct)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, ct))
        {
            if (envelope.RunId == runId
                && envelope.MessageType is MessageTypes.RunCompleted
                    or MessageTypes.RunFailed
                    or MessageTypes.RunCancelled)
            {
                return envelope;
            }
        }

        throw new InvalidOperationException($"Run '{runId}' did not emit a terminal event.");
    }

    private sealed class ReverseToolProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "reverse-provider";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true,
            Files: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("reverse-model", "Reverse Model", ContextWindow: 16_384)];

        public int CallCount { get; private set; }

        public bool ObservedBlobResult { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool => tool.ToolId == HostToolId));
                Assert.IsTrue(request.Tools.Any(static tool => tool.ToolId == McpToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "host-call",
                    HostToolId,
                    "lookup",
                    "{\"query\":\"blob\"}");
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "mcp-call",
                    McpToolId,
                    "create",
                    "{\"orderId\":42}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var toolResults = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .ToArray();
            ObservedBlobResult |= toolResults.Any(result =>
                result.CallId == "host-call"
                && result.Content.OfType<BlobRefContentBlock>().Any());
            if (CallCount == 2)
            {
                Assert.HasCount(2, toolResults);
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "host-call",
                    HostToolId,
                    "lookup",
                    "{\"query\":\"blob\"}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            Assert.HasCount(3, toolResults);
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Host and MCP results were applied.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeToolHost(RuntimeClient client, string workspace)
    {
        private readonly RuntimeClient _client = client;
        private readonly ConcurrentDictionary<string, int> _executions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ToolCallResponse> _results = new(StringComparer.Ordinal);
        private readonly string _workspace = workspace;
        private int _approvalRequestCount;
        private int _permissionRequestCount;

        public int ApprovalRequestCount => Volatile.Read(ref _approvalRequestCount);

        public int PermissionRequestCount => Volatile.Read(ref _permissionRequestCount);

        public int GetExecutionCount(string callId) => _executions.GetValueOrDefault(callId);

        public async ValueTask<JsonRpcResponse> HandleAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            return request.Method switch
            {
                MessageTypes.ToolPermissionRequest => HandlePermission(request),
                MessageTypes.ApprovalRequest => HandleApproval(request),
                MessageTypes.ToolCallRequest => await HandleToolCallAsync(request, ct),
                MessageTypes.ToolResultQuery => HandleResultQuery(request),
                MessageTypes.ToolCallCancel => CreateResponse(
                    request,
                    true,
                    RuntimeJsonContext.Default.Boolean),
                _ => new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'."))
            };
        }

        private JsonRpcResponse HandlePermission(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _permissionRequestCount);
            var permission = Deserialize(
                request,
                RuntimeJsonContext.Default.ToolPermissionRequest);
            var response = new ToolPermissionResponse(
                permission.CorrelationId ?? permission.ApprovalRequestId,
                permission.CallId,
                ToolAuthorizationDecision.Granted,
                CreateGrant(
                    permission.RunId!,
                    permission.AgentId,
                    permission.CallId,
                    permission.ToolId,
                    ToolRiskLevel.Low));
            return CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolPermissionResponse);
        }

        private JsonRpcResponse HandleApproval(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _approvalRequestCount);
            var approval = Deserialize(request, RuntimeJsonContext.Default.ApprovalRequest);
            var response = new ApprovalResponse(
                approval.CorrelationId,
                approval.ApprovalRequestId,
                ToolAuthorizationDecision.Granted,
                CreateGrant(
                    approval.RunId,
                    approval.AgentId,
                    approval.CallId,
                    approval.ToolId,
                    approval.Risk));
            return CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ApprovalResponse);
        }

        private async ValueTask<JsonRpcResponse> HandleToolCallAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            var call = Deserialize(request, RuntimeJsonContext.Default.ToolCallRequest);
            _executions.AddOrUpdate(call.CallId, 1, static (_, count) => count + 1);
            ToolCallResponse response;
            if (string.Equals(call.ToolId, HostToolId, StringComparison.Ordinal))
            {
                var payload = Encoding.UTF8.GetBytes(
                    $"{{\"payload\":\"{new string('x', 70 * 1024)}\"}}");
                await using var stream = new MemoryStream(payload, writable: false);
                var blobChannel = (FramedBlobChannel)_client.BlobChannel;
                var blob = await blobChannel.WriteReferenceAsync(
                    stream,
                    "application/json",
                    call.RunId,
                    "run",
                    TimeSpan.FromMinutes(10),
                    ct);
                response = new ToolCallResponse(
                    call.CorrelationId,
                    call.CallId,
                    ToolCallStatus.Succeeded,
                    ResultHash: blob.Sha256,
                    ResultBlob: blob);
            }
            else
            {
                response = new ToolCallResponse(
                    call.CorrelationId,
                    call.CallId,
                    ToolCallStatus.Succeeded,
                    ParseElement("""{"created":true,"orderId":42}"""));
            }

            _results[call.CallId] = response;
            return CreateResponse(request, response, RuntimeJsonContext.Default.ToolCallResponse);
        }

        private JsonRpcResponse HandleResultQuery(JsonRpcRequest request)
        {
            var query = Deserialize(request, RuntimeJsonContext.Default.ToolResultQueryRequest);
            var response = _results.TryGetValue(query.CallId, out var result)
                ? new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    result.Status,
                    result.Result,
                    result.ResultHash,
                    result.ResultBlob,
                    result.ErrorCode,
                    result.ErrorMessage)
                : new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    ToolCallStatus.Unknown);
            return CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }

        private ToolGrant CreateGrant(
            string runId,
            string agentId,
            string callId,
            string toolId,
            ToolRiskLevel risk)
        {
            var grantId = $"grant-{callId}";
            return new ToolGrant(
                grantId,
                runId,
                _workspace,
                [_workspace],
                [_workspace],
                AllowOverwrite: false,
                AllowMove: false,
                AllowDelete: false,
                AllowedExecutables: [],
                AllowPowerShell: false,
                new NetworkPolicy(),
                DateTimeOffset.UtcNow.AddMinutes(10),
                AllowDelegation: false,
                DelegatedAgentIds: [],
                AllowedToolIds: [toolId],
                AllowedCallIds: [callId],
                MaximumRisk: risk,
                AllowedEnvironmentVariables: [],
                AgentId: agentId,
                RootGrantId: grantId);
        }

        private static T Deserialize<T>(
            JsonRpcRequest request,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            request.Params is { } parameters
                ? JsonSerializer.Deserialize(parameters, typeInfo)
                    ?? throw new InvalidDataException(
                        $"Request '{request.Method}' had an empty payload.")
                : throw new InvalidDataException(
                    $"Request '{request.Method}' did not contain parameters.");

        private static JsonRpcResponse CreateResponse<T>(
            JsonRpcRequest request,
            T response,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(
                "2.0",
                request.Id,
                JsonSerializer.SerializeToElement(response, typeInfo));
    }
}
