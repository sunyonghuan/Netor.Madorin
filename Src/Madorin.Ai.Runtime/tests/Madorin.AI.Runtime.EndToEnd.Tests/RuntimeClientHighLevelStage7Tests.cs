using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class RuntimeClientHighLevelStage7Tests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task HostCallbackDispatcher_RoutesAllTypedCallbacks()
    {
        var callbacks = new RuntimeHostCallbacks
        {
            ToolPermissionRequestedAsync = static (request, _) => ValueTask.FromResult(
                new ToolPermissionResponse(
                    request.CorrelationId ?? request.ApprovalRequestId,
                    request.CallId,
                    ToolAuthorizationDecision.Denied)),
            ApprovalRequestedAsync = static (request, _) => ValueTask.FromResult(
                new ApprovalResponse(
                    request.CorrelationId,
                    request.ApprovalRequestId,
                    ToolAuthorizationDecision.Denied)),
            ToolCallRequestedAsync = static (request, _) => ValueTask.FromResult(
                new ToolCallResponse(
                    request.CorrelationId,
                    request.CallId,
                    ToolCallStatus.Succeeded,
                    ParseElement("""{"ok":true}"""))),
            ToolResultQueriedAsync = static (request, _) => ValueTask.FromResult(
                new ToolResultQueryResponse(
                    request.CorrelationId,
                    request.CallId,
                    ToolCallStatus.Unknown)),
            ToolCallCancelledAsync = static (_, _) => ValueTask.FromResult(true)
        };
        using var dispatcher = new RuntimeHostCallbackDispatcher(callbacks);

        var permission = CreatePermissionRequest("permission-call");
        var permissionResponse = await dispatcher.HandleAsync(
            CreateRequest(
                1,
                MessageTypes.ToolPermissionRequest,
                permission,
                RuntimeJsonContext.Default.ToolPermissionRequest),
            TestContext.CancellationToken);
        var permissionResult = DeserializeResult(
            permissionResponse,
            RuntimeJsonContext.Default.ToolPermissionResponse);
        Assert.AreEqual(permission.CallId, permissionResult.CallId);

        var approval = CreateApprovalRequest("approval-call");
        var approvalResponse = await dispatcher.HandleAsync(
            CreateRequest(
                2,
                MessageTypes.ApprovalRequest,
                approval,
                RuntimeJsonContext.Default.ApprovalRequest),
            TestContext.CancellationToken);
        Assert.AreEqual(
            approval.ApprovalRequestId,
            DeserializeResult(
                approvalResponse,
                RuntimeJsonContext.Default.ApprovalResponse).ApprovalRequestId);

        var call = CreateToolCallRequest("tool-call");
        var callResponse = await dispatcher.HandleAsync(
            CreateRequest(
                3,
                MessageTypes.ToolCallRequest,
                call,
                RuntimeJsonContext.Default.ToolCallRequest),
            TestContext.CancellationToken);
        Assert.AreEqual(
            ToolCallStatus.Succeeded,
            DeserializeResult(
                callResponse,
                RuntimeJsonContext.Default.ToolCallResponse).Status);

        var query = new ToolResultQueryRequest("query-correlation", "query-call");
        var queryResponse = await dispatcher.HandleAsync(
            CreateRequest(
                4,
                MessageTypes.ToolResultQuery,
                query,
                RuntimeJsonContext.Default.ToolResultQueryRequest),
            TestContext.CancellationToken);
        Assert.AreEqual(
            ToolCallStatus.Unknown,
            DeserializeResult(
                queryResponse,
                RuntimeJsonContext.Default.ToolResultQueryResponse).Status);

        var cancel = new ToolCallCancelRequest("cancel-correlation", "cancel-call");
        var cancelResponse = await dispatcher.HandleAsync(
            CreateRequest(
                5,
                MessageTypes.ToolCallCancel,
                cancel,
                RuntimeJsonContext.Default.ToolCallCancelRequest),
            TestContext.CancellationToken);
        Assert.IsTrue(DeserializeResult(cancelResponse, RuntimeJsonContext.Default.Boolean));
    }

    [TestMethod]
    public async Task HostCallbackDispatcher_ExceptionAndTimeout_DoNotBreakLaterCallbacks()
    {
        var callCount = 0;
        var callbacks = new RuntimeHostCallbacks
        {
            CallbackTimeout = TimeSpan.FromMilliseconds(100),
            ToolCallRequestedAsync = (request, ct) => Interlocked.Increment(ref callCount) switch
            {
                1 => ValueTask.FromException<ToolCallResponse>(
                    new InvalidOperationException("callback failure")),
                2 => DelayUntilCancelledAsync(ct),
                _ => ValueTask.FromResult(
                    new ToolCallResponse(
                        request.CorrelationId,
                        request.CallId,
                        ToolCallStatus.Succeeded))
            }
        };
        using var dispatcher = new RuntimeHostCallbackDispatcher(callbacks);

        var failed = await DispatchToolCallAsync(dispatcher, 1, "failed");
        Assert.IsNotNull(failed.Error);
        Assert.AreEqual(-32022, failed.Error.Code);

        var timedOut = await DispatchToolCallAsync(dispatcher, 2, "timeout");
        Assert.IsNotNull(timedOut.Error);
        Assert.AreEqual(-32020, timedOut.Error.Code);

        var succeeded = await DispatchToolCallAsync(dispatcher, 3, "succeeded");
        Assert.IsNull(succeeded.Error);
        Assert.AreEqual(
            ToolCallStatus.Succeeded,
            DeserializeResult(
                succeeded,
                RuntimeJsonContext.Default.ToolCallResponse).Status);
    }

    [TestMethod]
    public async Task HostCallbackDispatcher_EnforcesConcurrencyLimit()
    {
        var active = 0;
        var maximumActive = 0;
        var started = 0;
        var twoStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new RuntimeHostCallbacks
        {
            MaxConcurrency = 2,
            CallbackTimeout = TimeSpan.FromSeconds(5),
            ToolCallRequestedAsync = async (request, ct) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                if (Interlocked.Increment(ref started) == 2)
                {
                    twoStarted.TrySetResult();
                }

                try
                {
                    await release.Task.WaitAsync(ct);
                    return new ToolCallResponse(
                        request.CorrelationId,
                        request.CallId,
                        ToolCallStatus.Succeeded);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        };
        using var dispatcher = new RuntimeHostCallbackDispatcher(callbacks);

        var calls = Enumerable.Range(1, 3)
            .Select(index => DispatchToolCallAsync(
                dispatcher,
                index,
                $"concurrent-{index}").AsTask())
            .ToArray();
        await twoStarted.Task.WaitAsync(TestContext.CancellationToken);
        await Task.Delay(50, TestContext.CancellationToken);
        Assert.AreEqual(2, Volatile.Read(ref started));

        release.TrySetResult();
        var responses = await Task.WhenAll(calls);
        Assert.AreEqual(2, maximumActive);
        Assert.IsTrue(responses.All(static response => response.Error is null));
    }

    private static async ValueTask<ToolCallResponse> DelayUntilCancelledAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The timeout callback unexpectedly completed.");
    }

    private ValueTask<JsonRpcResponse> DispatchToolCallAsync(
        RuntimeHostCallbackDispatcher dispatcher,
        long id,
        string callId) => dispatcher.HandleAsync(
            CreateRequest(
                id,
                MessageTypes.ToolCallRequest,
                CreateToolCallRequest(callId),
                RuntimeJsonContext.Default.ToolCallRequest),
            TestContext.CancellationToken);

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (current >= value)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HighLevelToolApiAndConfiguredCallbacks_CompleteToolRunAndRevokeGrant()
    {
        var provider = new SingleToolProvider();
        var toolCallCount = 0;
        var permissionCount = 0;
        await using var fixture = await RuntimeFixture.StartAsync(
            provider,
            workspace => new RuntimeHostCallbacks
            {
                ToolPermissionRequestedAsync = (request, _) =>
                {
                    Interlocked.Increment(ref permissionCount);
                    var grant = CreateGrant(request, workspace);
                    return ValueTask.FromResult(
                        new ToolPermissionResponse(
                            request.CorrelationId ?? request.ApprovalRequestId,
                            request.CallId,
                            ToolAuthorizationDecision.Granted,
                            grant));
                },
                ToolCallRequestedAsync = (request, _) =>
                {
                    Interlocked.Increment(ref toolCallCount);
                    return ValueTask.FromResult(
                        new ToolCallResponse(
                            request.CorrelationId,
                            request.CallId,
                            ToolCallStatus.Succeeded,
                            ParseElement("""{"value":"from-host"}""")));
                },
                ToolResultQueriedAsync = static (request, _) => ValueTask.FromResult(
                    new ToolResultQueryResponse(
                        request.CorrelationId,
                        request.CallId,
                        ToolCallStatus.Unknown)),
                ToolCallCancelledAsync = static (_, _) => ValueTask.FromResult(true)
            });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var replaced = await fixture.Client.ReplaceToolCatalogAsync(
            new ToolCatalogReplaceRequest("host-v1", [CreateCatalogItem()]),
            timeout.Token);
        Assert.AreEqual("host-v1", replaced.CatalogVersion);
        Assert.IsGreaterThanOrEqualTo(1, replaced.ToolCount);

        var patched = await fixture.Client.PatchToolCatalogAsync(
            new ToolCatalogPatchRequest(
                "host-v1",
                "host-v2",
                [CreateCatalogItem()],
                []),
            timeout.Token);
        Assert.AreEqual("host-v2", patched.CatalogVersion);

        var runId = await fixture.Client.StartNewSessionRunAsync(
            CreateRunRequest("host-v2"),
            timeout.Token);
        var terminal = await ReadTerminalEventAsync(fixture.Client, runId, timeout.Token);
        await fixture.Client.AcknowledgeEventsAsync(terminal.Gsn, timeout.Token);

        Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
        Assert.AreEqual(1, permissionCount);
        Assert.AreEqual(1, toolCallCount);
        Assert.AreEqual(2, provider.CallCount);
        var run = await fixture.Client.QueryRunAsync(runId, timeout.Token);
        Assert.AreEqual("Tool result applied.", run.TerminalText);

        var revoked = await fixture.Client.RevokeGrantAsync(
            new GrantRevokeRequest("grant-stage7"),
            timeout.Token);
        Assert.AreEqual("grant-stage7", revoked.GrantId);
        Assert.AreEqual(1, revoked.RevokedCount);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ReadEventsAsync_WithoutExplicitAcknowledgement_ReplaysAfterReconnect()
    {
        var provider = new BlockingProvider();
        await using var fixture = await RuntimeFixture.StartAsync(provider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var events = fixture.Client
            .ReadEventsAsync(0, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        var runId = await fixture.Client.StartNewSessionRunAsync(
            CreateRunRequest("0", provider.ProviderId),
            timeout.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        var first = events.Current;
        Assert.AreEqual(runId, first.RunId);

        await fixture.Server.DisconnectEventChannelsAsync();
        RuntimeEventEnvelope replayed;
        do
        {
            Assert.IsTrue(await events.MoveNextAsync());
            replayed = events.Current;
            Assert.AreEqual(first.RuntimeInstanceId, replayed.RuntimeInstanceId);
        }
        while (replayed.Gsn != first.Gsn);

        Assert.AreEqual(first.Gsn, replayed.Gsn);
        Assert.AreEqual(first.RunSequence, replayed.RunSequence);

        await fixture.Client.AcknowledgeEventsAsync(replayed.Gsn, timeout.Token);
        provider.Release();
        RuntimeEventEnvelope terminal;
        do
        {
            Assert.IsTrue(await events.MoveNextAsync());
            terminal = events.Current;
        }
        while (terminal.RunId != runId
            || terminal.MessageType is not (MessageTypes.RunCompleted
                or MessageTypes.RunFailed
                or MessageTypes.RunCancelled));

        Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
        await fixture.Client.AcknowledgeEventsAsync(terminal.Gsn, timeout.Token);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StartNewSessionRunAsync_SpecialCharacters_PreservesPromptAcrossTransport()
    {
        const string expected = "  路径=C:\\临时 目录\\a \"quoted\".txt\n"
            + "url=https://example.test/a%20b/01?q=a+b&next=%2Ffolder%2Ffile#part-1\n"
            + "提示词=\"你好\"\n"
            + "末尾空白  ";
        var provider = new CapturingProvider();
        await using var fixture = await RuntimeFixture.StartAsync(provider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var runId = await fixture.Client.StartNewSessionRunAsync(
            CreateRunRequest(
                "0",
                provider.ProviderId,
                [new TextContentBlock(expected)]),
            timeout.Token);
        var terminal = await ReadTerminalEventAsync(fixture.Client, runId, timeout.Token);
        await fixture.Client.AcknowledgeEventsAsync(terminal.Gsn, timeout.Token);

        Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
        Assert.AreEqual(expected, provider.CapturedUserText);
    }

    private static ToolPermissionRequest CreatePermissionRequest(string callId) => new(
        "approval-id",
        "agent",
        ParentAgentId: null,
        ToolId,
        callId,
        "arguments-hash",
        "low",
        "workspace",
        "test",
        CorrelationId: "permission-correlation",
        RunId: "run");

    private static ApprovalRequest CreateApprovalRequest(string callId) => new(
        "approval-correlation",
        "approval-id",
        callId,
        "run",
        "agent",
        ParentAgentId: null,
        ToolId,
        "arguments-hash",
        ToolRiskLevel.Low,
        "target",
        "test");

    private static ToolCallRequest CreateToolCallRequest(string callId) => new(
        $"correlation-{callId}",
        callId,
        "run",
        "session",
        "invocation",
        "agent",
        ToolId,
        "host-v1",
        ParseElement("{}"),
        1_000,
        "grant");

    private static ToolGrant CreateGrant(ToolPermissionRequest request, string workspace) => new(
        "grant-stage7",
        request.RunId!,
        workspace,
        [workspace],
        [workspace],
        AllowOverwrite: false,
        AllowMove: false,
        AllowDelete: false,
        AllowedExecutables: [],
        AllowPowerShell: false,
        new NetworkPolicy(),
        DateTimeOffset.UtcNow.AddMinutes(5),
        AllowDelegation: false,
        DelegatedAgentIds: [],
        AllowedToolIds: [request.ToolId],
        AllowedCallIds: [request.CallId],
        MaximumRisk: ToolRiskLevel.Low,
        AgentId: request.AgentId,
        RootGrantId: "grant-stage7");

    private static ToolCatalogItem CreateCatalogItem() => new(
        ToolId,
        "Stage 7 Tool",
        "Returns a deterministic test result.",
        ParseElement("""{"type":"object","additionalProperties":true}"""),
        ParseElement("""{"type":"object","additionalProperties":true}"""),
        ToolRiskLevel.Low,
        5,
        ["test"],
        [],
        ToolExecutionTarget.Host,
        RequiresApproval: false,
        IsIdempotent: true);

    private static NewSessionRunRequest CreateRunRequest(
        string toolCatalogVersion,
        string providerId = SingleToolProvider.Id,
        ContentBlock[]? input = null) => new(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection(providerId, "stage7-model"),
                new ExpertModeOptions(
                    new AgentRef(
                        "stage7-agent",
                        "v1",
                        "Use the configured tool.",
                        providerId,
                        "stage7-model")),
                ToolCatalogVersion: toolCatalogVersion),
            input ?? [new TextContentBlock("Run the test operation.")]);

    private static async Task<RuntimeEventEnvelope> ReadTerminalEventAsync(
        RuntimeClient client,
        string runId,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, cancellationToken))
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

    private static JsonRpcRequest CreateRequest<T>(
        long id,
        string method,
        T parameters,
        JsonTypeInfo<T> typeInfo) => new(
            "2.0",
            id,
            method,
            JsonSerializer.SerializeToElement(parameters, typeInfo));

    private static T DeserializeResult<T>(
        JsonRpcResponse response,
        JsonTypeInfo<T> typeInfo)
    {
        Assert.IsNull(response.Error);
        Assert.IsNotNull(response.Result);
        return JsonSerializer.Deserialize(response.Result.Value, typeInfo)
            ?? throw new InvalidDataException("The callback response result was empty.");
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class SingleToolProvider : IRuntimeProviderAdapter
    {
        public const string Id = "stage7-provider";

        public string ProviderId => Id;

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("stage7-model", "Stage 7 Model", ContextWindow: 8_192)];

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
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool => tool.ToolId == ToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "stage7-call",
                    ToolId,
                    "run",
                    "{}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            Assert.IsTrue(request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Any(static result => result.CallId == "stage7-call"));
            yield return new TextDeltaProviderEvent(request.InvocationId, "Tool result applied.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BlockingProvider : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "blocking-stage7-provider";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("stage7-model", "Stage 7 Model", ContextWindow: 8_192)];

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await _release.Task.WaitAsync(ct);
            yield return new TextDeltaProviderEvent(request.InvocationId, "Released.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "capturing-stage7-provider";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("stage7-model", "Stage 7 Model", ContextWindow: 8_192)];

        public string? CapturedUserText { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            CapturedUserText = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.User)
                .SelectMany(static message => message.Content)
                .OfType<TextContentBlock>()
                .Single()
                .Text;
            yield return new TextDeltaProviderEvent(request.InvocationId, "Captured.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _workspace;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;

        private RuntimeFixture(
            string workspace,
            RuntimeServer server,
            RuntimeClient client,
            CancellationTokenSource shutdown,
            Task serverTask)
        {
            _workspace = workspace;
            Server = server;
            Client = client;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        public RuntimeServer Server { get; }

        public RuntimeClient Client { get; }

        public static async Task<RuntimeFixture> StartAsync(
            IRuntimeProviderAdapter provider,
            Func<string, RuntimeHostCallbacks>? callbackFactory = null)
        {
            var workspace = Path.Combine(
                Path.GetTempPath(),
                "madorin-client-stage7-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.client-stage7.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                });
            var shutdown = new CancellationTokenSource();
            var serverTask = server.RunAsync(shutdown.Token);
            var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: server.InstanceId,
                    HandshakeSecret: secret,
                    EnableBackgroundHeartbeat: false,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true),
                    HostCallbacks: callbackFactory?.Invoke(workspace)));
            try
            {
                await client.ConnectAsync();
                return new RuntimeFixture(workspace, server, client, shutdown, serverTask);
            }
            catch
            {
                await client.DisposeAsync();
                shutdown.Cancel();
                await serverTask;
                await server.DisposeAsync();
                shutdown.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _shutdown.Cancel();
            await _serverTask;
            await Server.DisposeAsync();
            _shutdown.Dispose();
            try
            {
                Directory.Delete(_workspace, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private const string ToolId = "host.stage7";
}
