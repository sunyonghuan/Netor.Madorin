using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReverseRpcHotReconnectTests
{
    private const string ToolId = "host.order.create";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ActiveRun_LostToolResponseReconnectsAndQueriesWithoutReexecution()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ReconnectProvider();
        var host = new RecoveringHost(workspace);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.reverse-reconnect.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    hostInstanceId,
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            await PublishCatalogAsync(client, timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest(),
                timeout.Token);
            await host.ExecutionStarted.WaitAsync(timeout.Token);
            await server.DisconnectControlChannelsAsync();
            host.ReleaseLostResponse();
            await WaitForAsync(
                () => server.ConnectedSessionCount == 0,
                timeout.Token);

            await client.ReconnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            var terminal = await ReadTerminalRunEventAsync(client, runId, timeout.Token);

            Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, host.PermissionRequestCount);
            Assert.AreEqual(1, host.ExecutionCount);
            Assert.AreEqual(1, host.QueryCount);
            var run = await client.QueryRunAsync(runId, timeout.Token);
            Assert.AreEqual("Recovered host result applied.", run.TerminalText);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            host.ReleaseLostResponse();
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static async Task PublishCatalogAsync(RuntimeClient client, CancellationToken ct)
    {
        var request = new ToolCatalogReplaceRequest(
            "host-v1",
            [
                new ToolCatalogItem(
                    ToolId,
                    "Create order",
                    "Creates one order on the host.",
                    ParseElement("""{"type":"object","additionalProperties":true}"""),
                    ParseElement("""{"type":"object","additionalProperties":true}"""),
                    ToolRiskLevel.Low,
                    10,
                    ["test"],
                    [],
                    ToolExecutionTarget.Host,
                    RequiresApproval: false,
                    IsIdempotent: false)
            ]);
        var response = await client.ControlPeer.SendRequestAsync(
            MessageTypes.ToolCatalogReplace,
            JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.ToolCatalogReplaceRequest),
            TimeSpan.FromSeconds(5),
            ct);
        Assert.IsNull(response.Error);
    }

    private static NewSessionRunRequest CreateRequest() =>
        new(
            "reconnect-session",
            "reconnect-run",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("reconnect-provider", "reconnect-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "Create the order."))),
            [new TextContentBlock("Create one order.")]);

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

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(25, ct);
        }
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-reverse-reconnect-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ReconnectProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "reconnect-provider";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("reconnect-model", "Reconnect Model", ContextWindow: 4096)];

        public int CallCount { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "order-call",
                    ToolId,
                    "create",
                    "{\"orderId\":42}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var result = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "order-call");
            Assert.IsTrue(result.Success);
            StringAssert.Contains(result.Content.OfType<TextContentBlock>().Single().Text, "created");
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Recovered host result applied.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecoveringHost(string workspace)
    {
        private readonly TaskCompletionSource _executionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLostResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, ToolCallResponse> _results =
            new(StringComparer.Ordinal);
        private readonly string _workspace = workspace;
        private int _executionCount;
        private int _permissionRequestCount;
        private int _queryCount;

        public Task ExecutionStarted => _executionStarted.Task;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PermissionRequestCount => Volatile.Read(ref _permissionRequestCount);

        public int QueryCount => Volatile.Read(ref _queryCount);

        public void ReleaseLostResponse() => _releaseLostResponse.TrySetResult();

        public ValueTask<JsonRpcResponse> HandleAsync(
            JsonRpcRequest request,
            CancellationToken ct) =>
            request.Method switch
            {
                MessageTypes.ToolPermissionRequest => ValueTask.FromResult(
                    HandlePermission(request)),
                MessageTypes.ToolCallRequest => HandleToolCallAsync(request, ct),
                MessageTypes.ToolResultQuery => ValueTask.FromResult(
                    HandleResultQuery(request)),
                _ => ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'.")))
            };

        private JsonRpcResponse HandlePermission(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _permissionRequestCount);
            var permission = Deserialize(
                request,
                RuntimeJsonContext.Default.ToolPermissionRequest);
            var grantId = $"grant-{permission.CallId}";
            var response = new ToolPermissionResponse(
                permission.CorrelationId ?? permission.ApprovalRequestId,
                permission.CallId,
                ToolAuthorizationDecision.Granted,
                new ToolGrant(
                    grantId,
                    permission.RunId!,
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
                    AllowedToolIds: [permission.ToolId],
                    AllowedCallIds: [permission.CallId],
                    MaximumRisk: ToolRiskLevel.Low,
                    AgentId: permission.AgentId,
                    RootGrantId: grantId));
            return CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolPermissionResponse);
        }

        private async ValueTask<JsonRpcResponse> HandleToolCallAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            var call = Deserialize(request, RuntimeJsonContext.Default.ToolCallRequest);
            Interlocked.Increment(ref _executionCount);
            var response = new ToolCallResponse(
                call.CorrelationId,
                call.CallId,
                ToolCallStatus.Succeeded,
                ParseElement("""{"created":true,"orderId":42}"""));
            _results[call.CallId] = response;
            _executionStarted.TrySetResult();
            await _releaseLostResponse.Task.WaitAsync(ct);
            return CreateResponse(request, response, RuntimeJsonContext.Default.ToolCallResponse);
        }

        private JsonRpcResponse HandleResultQuery(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _queryCount);
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
