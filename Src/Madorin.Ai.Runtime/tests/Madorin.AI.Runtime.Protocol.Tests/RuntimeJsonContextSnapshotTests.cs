using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class RuntimeJsonContextSnapshotTests
{
    private static readonly DateTimeOffset SnapshotTimestamp =
        new(2026, 7, 22, 10, 30, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("initialize-request.v1.json", "initialize.request")]
    [DataRow("initialize-response.v1.json", "initialize.response")]
    [DataRow("new-session-run-expert.v1.json", "run.new_session.expert")]
    [DataRow("new-session-run-meeting.v1.json", "run.new_session.meeting")]
    [DataRow("new-session-run-work.v1.json", "run.new_session.work")]
    [DataRow("existing-session-run.v1.json", "run.existing_session")]
    [DataRow("runtime-event-envelope.v1.json", "event.envelope")]
    [DataRow("json-rpc-request.v1.json", "jsonrpc.request")]
    [DataRow("json-rpc-error-response.v1.json", "jsonrpc.error_response")]
    [DataRow("handshake-server-hello.v1.json", "handshake.server_hello")]
    [DataRow("tool-permission-request.v1.json", "tool.permission.request")]
    [DataRow("tool-grant.v1.json", "tool.grant")]
    [DataRow("run-query-request.v1.json", "run.query")]
    [DataRow("run-query-result.v1.json", "run.query.result")]
    [DataRow("context-projection-adjusted.v1.json", "context.projection.adjusted")]
    public void RuntimeJsonContext_Message_MatchesVersionedSnapshot(
        string snapshotFileName,
        string messageType)
    {
        var snapshotPath = Path.Combine(
            AppContext.BaseDirectory,
            "Snapshots",
            snapshotFileName);
        using var snapshot = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        var snapshotRoot = snapshot.RootElement;
        var actualPayload = CreatePayload(snapshotFileName);
        var roundTrippedPayload = RoundTripPayload(snapshotFileName, actualPayload);

        Assert.AreEqual(
            "madorin.ai.runtime.protocol-snapshot/v1",
            snapshotRoot.GetProperty("$schema").GetString());
        Assert.AreEqual(
            ProtocolVersions.Legacy,
            snapshotRoot.GetProperty("protocolVersion").GetString());
        Assert.AreEqual(
            messageType,
            snapshotRoot.GetProperty("messageType").GetString());
        Assert.IsTrue(
            JsonElement.DeepEquals(
                snapshotRoot.GetProperty("payload"),
                actualPayload),
            $"Protocol snapshot '{snapshotFileName}' changed. Actual payload:{Environment.NewLine}{actualPayload}");
        Assert.IsTrue(
            JsonElement.DeepEquals(actualPayload, roundTrippedPayload),
            $"Protocol snapshot '{snapshotFileName}' did not round-trip without loss.");
    }

    private static JsonElement CreatePayload(string snapshotFileName)
    {
        return snapshotFileName switch
        {
            "initialize-request.v1.json" => CreateInitializeRequest(),
            "initialize-response.v1.json" => CreateInitializeResponse(),
            "new-session-run-expert.v1.json" => CreateNewSessionRun(RuntimeMode.Expert),
            "new-session-run-meeting.v1.json" => CreateNewSessionRun(RuntimeMode.Meeting),
            "new-session-run-work.v1.json" => CreateNewSessionRun(RuntimeMode.Work),
            "existing-session-run.v1.json" => CreateExistingSessionRun(),
            "runtime-event-envelope.v1.json" => CreateRuntimeEventEnvelope(),
            "json-rpc-request.v1.json" => CreateJsonRpcRequest(),
            "json-rpc-error-response.v1.json" => CreateJsonRpcErrorResponse(),
            "handshake-server-hello.v1.json" => Serialize(
                new HandshakeServerHello("runtime-1", "nonce-s", "proof-s"),
                RuntimeJsonContext.Default.HandshakeServerHello),
            "tool-permission-request.v1.json" => CreateToolPermissionRequest(),
            "tool-grant.v1.json" => CreateToolGrant(),
            "run-query-request.v1.json" => Serialize(
                new RunQueryParameters("run-1"),
                RuntimeJsonContext.Default.RunQueryParameters),
            "run-query-result.v1.json" => Serialize(
                new RunQueryResult(
                    "run-1",
                    "session-1",
                    RunStatus.Completed,
                    "Final answer\nline 2"),
                RuntimeJsonContext.Default.RunQueryResult),
            "context-projection-adjusted.v1.json" => Serialize(
                new ContextProjectionAdjustedEvent(
                    "invocation-1",
                    DroppedMessageCount: 7,
                    IncludedMessageCount: 3,
                    EstimatedTokens: 4096,
                    EstimateSource: "provider.estimated",
                    Strategy: "TailWindow"),
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent),
            _ => throw new InvalidOperationException(
                $"Unknown protocol snapshot '{snapshotFileName}'.")
        };
    }

    private static JsonElement RoundTripPayload(
        string snapshotFileName,
        JsonElement payload)
    {
        return snapshotFileName switch
        {
            "initialize-request.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.InitializeRequest),
            "initialize-response.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.InitializeResponse),
            "new-session-run-expert.v1.json" or
            "new-session-run-meeting.v1.json" or
            "new-session-run-work.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.NewSessionRunRequest),
            "existing-session-run.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.ExistingSessionRunRequest),
            "runtime-event-envelope.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.RuntimeEventEnvelope),
            "json-rpc-request.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.JsonRpcRequest),
            "json-rpc-error-response.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.JsonRpcResponse),
            "handshake-server-hello.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.HandshakeServerHello),
            "tool-permission-request.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.ToolPermissionRequest),
            "tool-grant.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.ToolGrant),
            "run-query-request.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.RunQueryParameters),
            "run-query-result.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.RunQueryResult),
            "context-projection-adjusted.v1.json" => RoundTrip(
                payload,
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent),
            _ => throw new InvalidOperationException(
                $"Unknown protocol snapshot '{snapshotFileName}'.")
        };
    }

    private static JsonElement CreateInitializeRequest()
    {
        return Serialize(
            new InitializeRequest(
                "host-1",
                "1.0.0",
                [ProtocolVersions.Legacy, "0.9"],
                new RuntimeCapabilities(
                    Streaming: true,
                    ToolCalling: true,
                    BlobTransfer: false,
                    MultiRun: true,
                    SessionResume: true),
                42),
            RuntimeJsonContext.Default.InitializeRequest);
    }

    private static JsonElement CreateInitializeResponse()
    {
        return Serialize(
            new InitializeResponse(
                "runtime-1",
                "1.0.0",
                ProtocolVersions.Legacy,
                new RuntimeCapabilities(),
                new RuntimeLimits(),
                "memory://runtime-1/events",
                15),
            RuntimeJsonContext.Default.InitializeResponse);
    }

    private static JsonElement CreateNewSessionRun(RuntimeMode mode)
    {
        var primaryAgent = new AgentRef(
            $"{mode.ToString().ToLowerInvariant()}-agent",
            "v1",
            $"{mode} 系统提示",
            "provider-1",
            "model-1");
        ModeOptions modeOptions = mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(primaryAgent),
            RuntimeMode.Meeting => new MeetingModeOptions(
                [primaryAgent, new AgentRef("reviewer", "v2", "Review")],
                primaryAgent.AgentId),
            RuntimeMode.Work => new WorkModeOptions(
                primaryAgent,
                [new AgentRef("worker", "v3", "Execute")]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        var content = mode == RuntimeMode.Expert
            ? CreateAllContentBlocks()
            : [new TextContentBlock($"Start {mode}")];
        var request = new NewSessionRunRequest(
            $"session-key-{mode.ToString().ToLowerInvariant()}",
            $"run-key-{mode.ToString().ToLowerInvariant()}",
            mode,
            new NextTurnSelection(
                3,
                mode,
                new DefaultSelection("provider-1", "model-1"),
                modeOptions,
                "  保留首尾空白  ",
                "catalog-v1"),
            content,
            "workspace-1");

        return Serialize(request, RuntimeJsonContext.Default.NewSessionRunRequest);
    }

    private static ContentBlock[] CreateAllContentBlocks()
    {
        var arguments = ParseElement(
            """{"path":"C:\\临时\\a b.txt","url":"https://example.test/a%20b?x=1%2F2"}""");
        var blob = new BlobReference(
            "blob-1",
            128,
            "0123456789abcdef",
            "text/plain",
            "run",
            SnapshotTimestamp.AddHours(1));

        return
        [
            new TextContentBlock("  中文 \"quoted\"\r\nline2  "),
            new ReasoningContentBlock("reasoning"),
            new ToolCallContentBlock("call-1", "builtin.fs", "read", arguments),
            new ToolResultContentBlock(
                "call-1",
                "builtin.fs",
                true,
                [new TextContentBlock("result")]),
            new BlobRefContentBlock(blob)
        ];
    }

    private static JsonElement CreateExistingSessionRun()
    {
        var request = new ExistingSessionRunRequest(
            "session-1",
            "run-key-next",
            new NextTurnSelection(
                4,
                RuntimeMode.Expert,
                new DefaultSelection("provider-2", "model-2"),
                new ExpertModeOptions(
                    new AgentRef("agent-2", "v2", "Override prompt")),
                ToolCatalogVersion: "catalog-v2"),
            [new TextContentBlock("next turn")]);

        return Serialize(request, RuntimeJsonContext.Default.ExistingSessionRunRequest);
    }

    private static JsonElement CreateRuntimeEventEnvelope()
    {
        var payload = Serialize(
            new RunCompletedEvent("run-1", "session-1"),
            RuntimeJsonContext.Default.RunCompletedEvent);
        var runtimeEvent = new RuntimeEventEnvelope(
            "runtime-1",
            7,
            "run-1",
            3,
            MessageTypes.RunCompleted,
            SnapshotTimestamp,
            payload);

        return Serialize(runtimeEvent, RuntimeJsonContext.Default.RuntimeEventEnvelope);
    }

    private static JsonElement CreateJsonRpcRequest()
    {
        var parameters = Serialize(
            new EventAcknowledgeParameters(42),
            RuntimeJsonContext.Default.EventAcknowledgeParameters);

        return Serialize(
            new JsonRpcRequest("2.0", 7, "events.acknowledge", parameters),
            RuntimeJsonContext.Default.JsonRpcRequest);
    }

    private static JsonElement CreateJsonRpcErrorResponse()
    {
        var data = ParseElement("""{"runtimeInstanceId":"runtime-1"}""");
        return Serialize(
            new JsonRpcResponse(
                "2.0",
                7,
                Error: new JsonRpcError(-32602, "Instance mismatch", data)),
            RuntimeJsonContext.Default.JsonRpcResponse);
    }

    private static JsonElement CreateToolPermissionRequest()
    {
        return Serialize(
            new ToolPermissionRequest(
                "approval-1",
                "agent-1",
                "parent-1",
                "builtin.fs",
                "call-1",
                "arguments-hash",
                "high",
                "workspace",
                "Read project file",
                SessionId: "session-1",
                InvocationId: "invocation-1",
                ParentInvocationId: "parent-invocation-1",
                WorkStepId: "step-1",
                PlanVersion: "plan-v1"),
            RuntimeJsonContext.Default.ToolPermissionRequest);
    }

    private static JsonElement CreateToolGrant()
    {
        return Serialize(
            new ToolGrant(
                "grant-1",
                "run-1",
                "C:\\workspace",
                ["C:\\workspace\\src"],
                ["C:\\workspace\\out"],
                AllowOverwrite: true,
                AllowMove: false,
                AllowDelete: false,
                AllowedExecutables: ["dotnet"],
                AllowPowerShell: false,
                new NetworkPolicy(false, ["api.example.test"]),
                SnapshotTimestamp.AddMinutes(30),
                AllowDelegation: true,
                DelegatedAgentIds: ["agent-child"]),
            RuntimeJsonContext.Default.ToolGrant);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Serialize<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        return JsonSerializer.SerializeToElement(value, jsonTypeInfo);
    }

    private static JsonElement RoundTrip<T>(
        JsonElement payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var value = JsonSerializer.Deserialize(payload.GetRawText(), jsonTypeInfo) ??
                    throw new InvalidOperationException(
                        $"Could not deserialize protocol snapshot type {typeof(T).Name}.");
        return Serialize(value, jsonTypeInfo);
    }
}
