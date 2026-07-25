using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class Stage5ProtocolTests
{
    [TestMethod]
    public void ToolProtocolMessages_RoundTripWithSourceGeneratedMetadata()
    {
        var schema = ParseElement("""{"type":"object"}""");
        var item = new ToolCatalogItem(
            "host.search",
            "Search",
            "Searches the host index.",
            schema,
            schema,
            ToolRiskLevel.SensitiveRead,
            30,
            ["search"],
            ["host-index"],
            ToolExecutionTarget.Host,
            RequiresApproval: false,
            IsIdempotent: true);
        var grant = CreateGrant();

        AssertRoundTrip(
            new ToolCatalogReplaceRequest("host-v1", [item]),
            RuntimeJsonContext.Default.ToolCatalogReplaceRequest);
        AssertRoundTrip(
            new ToolCatalogPatchRequest("host-v1", "host-v2", [item], ["host.old"]),
            RuntimeJsonContext.Default.ToolCatalogPatchRequest);
        AssertRoundTrip(
            new ToolCatalogUpdateResponse("host-v2", "sha256:abc", 3),
            RuntimeJsonContext.Default.ToolCatalogUpdateResponse);
        AssertRoundTrip(
            new ToolPermissionResponse(
                "correlation-1",
                "call-1",
                ToolAuthorizationDecision.Granted,
                grant),
            RuntimeJsonContext.Default.ToolPermissionResponse);
        AssertRoundTrip(
            new ApprovalRequest(
                "correlation-2",
                "approval-1",
                "call-1",
                "run-1",
                "agent-1",
                "parent-1",
                "host.search",
                "arguments-hash",
                ToolRiskLevel.SensitiveRead,
                "host index",
                "Search the configured index",
                SessionId: "session-1",
                InvocationId: "invocation-1",
                ParentInvocationId: "parent-invocation-1",
                WorkStepId: "step-1",
                PlanVersion: "plan-v1"),
            RuntimeJsonContext.Default.ApprovalRequest);
        AssertRoundTrip(
            new ApprovalResponse(
                "correlation-2",
                "approval-1",
                ToolAuthorizationDecision.Granted,
                grant),
            RuntimeJsonContext.Default.ApprovalResponse);
        AssertRoundTrip(
            new ToolCallRequest(
                "correlation-3",
                "call-1",
                "run-1",
                "session-1",
                "invocation-1",
                "agent-1",
                "host.search",
                "sha256:catalog",
                ParseElement("""{"query":"中文"}"""),
                30_000,
                "grant-1"),
            RuntimeJsonContext.Default.ToolCallRequest);
        AssertRoundTrip(
            new ToolCallResponse(
                "correlation-3",
                "call-1",
                ToolCallStatus.Succeeded,
                ParseElement("""{"items":[]}"""),
                "result-hash"),
            RuntimeJsonContext.Default.ToolCallResponse);
        AssertRoundTrip(
            new ToolCallCancelRequest("correlation-4", "call-1", "run cancelled"),
            RuntimeJsonContext.Default.ToolCallCancelRequest);
        AssertRoundTrip(
            new ToolResultQueryRequest("correlation-5", "call-1"),
            RuntimeJsonContext.Default.ToolResultQueryRequest);
        AssertRoundTrip(
            new ToolResultQueryResponse(
                "correlation-5",
                "call-1",
                ToolCallStatus.Unknown),
            RuntimeJsonContext.Default.ToolResultQueryResponse);
        AssertRoundTrip(
            new GrantRevokeRequest("grant-1"),
            RuntimeJsonContext.Default.GrantRevokeRequest);
        AssertRoundTrip(
            new GrantRevokeResponse("grant-1", 2),
            RuntimeJsonContext.Default.GrantRevokeResponse);
    }

    [TestMethod]
    public void ToolProtocolMessages_IgnoreUnknownOptionalProperties()
    {
        const string json =
            """
            {
              "correlationId": "correlation-1",
              "callId": "call-1",
              "status": "Unknown",
              "futureRecoveryHint": { "manual": true }
            }
            """;

        var response = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.ToolResultQueryResponse);

        Assert.IsNotNull(response);
        Assert.AreEqual(ToolCallStatus.Unknown, response.Status);
    }

    private static ToolGrant CreateGrant() =>
        new(
            "grant-1",
            "run-1",
            "C:\\workspace",
            ["C:\\workspace"],
            ["C:\\workspace"],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero),
            AllowDelegation: false,
            DelegatedAgentIds: []);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertRoundTrip<T>(T expected, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(expected, typeInfo);
        var actual = JsonSerializer.Deserialize(json, typeInfo);

        Assert.IsNotNull(actual);
        Assert.IsTrue(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected, typeInfo),
            JsonSerializer.SerializeToElement(actual, typeInfo)));
    }
}
