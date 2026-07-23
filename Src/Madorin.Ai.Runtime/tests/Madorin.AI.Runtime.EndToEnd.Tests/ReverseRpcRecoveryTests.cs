using System.Collections.Concurrent;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Transport.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReverseRpcRecoveryTests
{
    private const string ToolId = "host.order.create";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task RuntimeRestart_SentHostCallQueriesPersistedResultWithoutReexecution()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "madorin-reverse-recovery",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Join(root, "state.db");
            var host = new RecoveringHost();
            var invocation = new ToolInvocation(
                "call-restart",
                ToolId,
                "agent-1",
                ParentAgentId: null,
                """{"orderId":42}""",
                "run-1",
                "session-1",
                InvocationId: "invocation-1");
            var permission = new ToolPermissionContext(
                "grant-call-restart",
                invocation.RunId,
                root,
                [root],
                [root],
                AllowedToolIds: [invocation.ToolId],
                AllowedCallIds: [invocation.CallId],
                MaximumRisk: ToolRiskLevel.Low);

            var first = await ExecuteAsync(
                databasePath,
                root,
                new RecoveringPeer(host, loseToolCallResponse: true),
                invocation,
                permission,
                TestContext.CancellationToken);

            Assert.AreEqual(ToolGatewayResultKind.Failed, first.Result.Kind);
            Assert.AreEqual(RuntimeErrorCodes.ToolResultUnknown, first.Result.Error?.Code);
            Assert.AreEqual(ToolIntentStatus.Sent, first.Intent.Status);
            Assert.AreEqual(1, host.ExecutionCount);
            Assert.AreEqual(0, host.QueryCount);

            var second = await ExecuteAsync(
                databasePath,
                root,
                new RecoveringPeer(host, loseToolCallResponse: false),
                invocation,
                permission,
                TestContext.CancellationToken);

            Assert.AreEqual(ToolGatewayResultKind.Success, second.Result.Kind);
            Assert.AreEqual(ToolIntentStatus.Succeeded, second.Intent.Status);
            Assert.AreEqual(1, host.ExecutionCount);
            Assert.AreEqual(1, host.QueryCount);
            Assert.AreEqual(0, host.SecondConnectionExecutionCount);
            Assert.AreEqual(
                """{"created":true,"orderId":42}""",
                second.Result.Result?.OutputJson);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<GatewayExecution> ExecuteAsync(
        string databasePath,
        string workspaceRoot,
        IDuplexRpcPeer peer,
        ToolInvocation invocation,
        ToolPermissionContext permission,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        using var stateStore = new SqliteToolIntentRepository(connection);
        using var outbox = new SqliteEventOutbox(connection);
        var validator = new JsonSchemaToolValidator();
        var catalogStore = new ToolCatalogStore(new BuiltinToolRegistry(), validator);
        var descriptor = new ToolDescriptor(
            ToolId,
            "host",
            "Create order",
            "Creates one order on the host.",
            """{"type":"object","properties":{"orderId":{"type":"integer"}},"required":["orderId"],"additionalProperties":false}""",
            """{"type":"object","properties":{"created":{"type":"boolean"},"orderId":{"type":"integer"}},"required":["created","orderId"],"additionalProperties":false}""",
            Risk: ToolRiskLevel.Low,
            ExecutionTarget: ToolExecutionTarget.Host,
            IsIdempotent: false);
        var catalog = new ToolCatalogSnapshot("host-v1", "effective-host-v1", [descriptor]);
        using var gateway = new ToolGateway(
            catalogStore,
            validator,
            new ToolAuthorizationService(
                stateStore,
                RuntimeToolPolicy.CreateRestricted(workspaceRoot)),
            [new ReverseRpcToolExecutor(peer)],
            stateStore,
            outbox);

        var result = await gateway.ExecuteAsync(invocation, permission, catalog, ct);
        var intent = await stateStore.GetIntentAsync(invocation.CallId, ct)
            ?? throw new InvalidDataException("The tool intent was not persisted.");
        return new GatewayExecution(result, intent);
    }

    private sealed record GatewayExecution(
        ToolGatewayResult Result,
        ToolIntentState Intent);

    private sealed class RecoveringHost
    {
        private readonly ConcurrentDictionary<string, ToolCallResponse> _results =
            new(StringComparer.Ordinal);
        private int _executionCount;
        private int _queryCount;
        private int _secondConnectionExecutionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int QueryCount => Volatile.Read(ref _queryCount);

        public int SecondConnectionExecutionCount =>
            Volatile.Read(ref _secondConnectionExecutionCount);

        public ToolCallResponse Execute(ToolCallRequest call, bool isSecondConnection)
        {
            Interlocked.Increment(ref _executionCount);
            if (isSecondConnection)
            {
                Interlocked.Increment(ref _secondConnectionExecutionCount);
            }

            var response = new ToolCallResponse(
                call.CorrelationId,
                call.CallId,
                ToolCallStatus.Succeeded,
                ParseElement("""{"created":true,"orderId":42}"""));
            _results[call.CallId] = response;
            return response;
        }

        public ToolResultQueryResponse Query(ToolResultQueryRequest query)
        {
            Interlocked.Increment(ref _queryCount);
            if (_results.TryGetValue(query.CallId, out var result))
            {
                return new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    result.Status,
                    result.Result,
                    result.ResultHash,
                    result.ResultBlob,
                    result.ErrorCode,
                    result.ErrorMessage);
            }

            return new ToolResultQueryResponse(
                query.CorrelationId,
                query.CallId,
                ToolCallStatus.Unknown,
                ErrorCode: RuntimeErrorCodes.ToolResultUnknown,
                ErrorMessage: "The host has no durable result for this call.");
        }

        private static JsonElement ParseElement(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }

    private sealed class RecoveringPeer(
        RecoveringHost host,
        bool loseToolCallResponse) : IDuplexRpcPeer
    {
        private readonly RecoveringHost _host =
            host ?? throw new ArgumentNullException(nameof(host));
        private readonly bool _loseToolCallResponse = loseToolCallResponse;
        private Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>>?
            _requestHandler;

        public Task Completion => Task.CompletedTask;

        public void SetRequestHandler(
            Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler) =>
            _requestHandler = handler ?? throw new ArgumentNullException(nameof(handler));

        public ValueTask<JsonRpcResponse> SendRequestAsync(
            string method,
            JsonElement? parameters = null,
            CancellationToken ct = default) =>
            SendRequestAsync(method, parameters, Timeout.InfiniteTimeSpan, ct);

        public ValueTask<JsonRpcResponse> SendRequestAsync(
            string method,
            JsonElement? parameters,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = timeout;
            var payload = parameters
                ?? throw new InvalidDataException($"Reverse RPC method '{method}' has no parameters.");

            return method switch
            {
                MessageTypes.ToolCallRequest => HandleToolCallAsync(payload),
                MessageTypes.ToolResultQuery => ValueTask.FromResult(HandleResultQuery(payload)),
                _ => throw new NotSupportedException($"Reverse RPC method '{method}' is not supported.")
            };
        }

        public ValueTask<ReadOnlyMemory<byte>> SendAsync(
            ReadOnlyMemory<byte> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _requestHandler = null;
            return ValueTask.CompletedTask;
        }

        private ValueTask<JsonRpcResponse> HandleToolCallAsync(JsonElement parameters)
        {
            var call = JsonSerializer.Deserialize(
                parameters,
                RuntimeJsonContext.Default.ToolCallRequest)
                ?? throw new InvalidDataException("The tool call request is empty.");
            var response = _host.Execute(call, isSecondConnection: !_loseToolCallResponse);
            if (_loseToolCallResponse)
            {
                throw new EndOfStreamException("The host response was lost after execution.");
            }

            return ValueTask.FromResult(CreateResponse(
                response,
                RuntimeJsonContext.Default.ToolCallResponse));
        }

        private JsonRpcResponse HandleResultQuery(JsonElement parameters)
        {
            var query = JsonSerializer.Deserialize(
                parameters,
                RuntimeJsonContext.Default.ToolResultQueryRequest)
                ?? throw new InvalidDataException("The tool result query is empty.");
            return CreateResponse(
                _host.Query(query),
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }

        private static JsonRpcResponse CreateResponse<T>(
            T response,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(
                "2.0",
                1,
                JsonSerializer.SerializeToElement(response, typeInfo));
    }
}
