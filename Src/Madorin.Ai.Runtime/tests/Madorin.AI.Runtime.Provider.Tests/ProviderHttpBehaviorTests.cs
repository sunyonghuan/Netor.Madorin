using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderHttpBehaviorTests
{
    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI, 401, RuntimeErrorCodes.AuthenticationFailed, "authentication")]
    [DataRow(ProviderAdapterKind.Anthropic, 401, RuntimeErrorCodes.AuthenticationFailed, "authentication")]
    [DataRow(ProviderAdapterKind.OpenAICompatible, 401, RuntimeErrorCodes.AuthenticationFailed, "authentication")]
    [DataRow(ProviderAdapterKind.OpenAI, 429, RuntimeErrorCodes.ProviderRateLimited, "rate_limit")]
    [DataRow(ProviderAdapterKind.Anthropic, 429, RuntimeErrorCodes.ProviderRateLimited, "rate_limit")]
    [DataRow(ProviderAdapterKind.OpenAICompatible, 429, RuntimeErrorCodes.ProviderRateLimited, "rate_limit")]
    [DataRow(ProviderAdapterKind.OpenAI, 503, RuntimeErrorCodes.ProviderRequestFailed, "provider")]
    [DataRow(ProviderAdapterKind.Anthropic, 503, RuntimeErrorCodes.ProviderRequestFailed, "provider")]
    [DataRow(ProviderAdapterKind.OpenAICompatible, 503, RuntimeErrorCodes.ProviderRequestFailed, "provider")]
    public async Task CompleteStreamingAsync_HttpFailure_MapsRedactedError(
        ProviderAdapterKind kind,
        int statusCode,
        string expectedCode,
        string expectedCategory)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["retry-after"] = "7",
            ["x-request-id"] = "request-123"
        };
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(
                statusCode,
                "{\"error\":{\"message\":\"sensitive response body provider-test-key sensitive prompt\"}}",
                headers));
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest());

        var failed = Assert.IsInstanceOfType<InvocationFailedProviderEvent>(
            Assert.ContainsSingle(events));
        Assert.AreEqual(expectedCode, failed.Error.Code);
        Assert.AreEqual(expectedCategory, failed.Error.Category);
        Assert.IsNotNull(failed.Error.ProviderDetails);
        Assert.AreEqual(statusCode, failed.Error.ProviderDetails.Value.GetProperty("statusCode").GetInt32());
        Assert.AreEqual("7", failed.Error.ProviderDetails.Value.GetProperty("retryAfter").GetString());
        Assert.AreEqual("request-123", failed.Error.ProviderDetails.Value.GetProperty("requestId").GetString());

        var serialized = JsonSerializer.Serialize(
            failed.Error,
            RuntimeJsonContext.Default.RuntimeError);
        Assert.DoesNotContain(ProviderAdapterTestFactory.ApiKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive prompt", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive response body", serialized, StringComparison.Ordinal);
        Assert.HasCount(1, server.Requests);
    }

    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI, "tools")]
    [DataRow(ProviderAdapterKind.Anthropic, "tools")]
    [DataRow(ProviderAdapterKind.OpenAICompatible, "tools")]
    [DataRow(ProviderAdapterKind.OpenAI, "structured_output")]
    [DataRow(ProviderAdapterKind.Anthropic, "structured_output")]
    [DataRow(ProviderAdapterKind.OpenAICompatible, "structured_output")]
    public async Task CompleteStreamingAsync_UnsupportedRequestCapability_FailsBeforeHttp(
        ProviderAdapterKind kind,
        string capability)
    {
        await using var server = await FakeProviderServer.StartAsync(_ =>
            FakeProviderResponse.Error(500, "{\"error\":\"HTTP should not be reached\"}"));
        var modelCapabilities = ProviderAdapterTestFactory.CreateCapabilities(
            toolCalling: capability != "tools",
            structuredOutput: capability != "structured_output");
        using var fixture = ProviderAdapterTestFactory.Create(
            kind,
            server.BaseAddress,
            modelCapabilities);

        ToolDescriptor[]? tools = capability == "tools"
            ? [new("weather", "host", "get_weather", "Gets weather.", "{\"type\":\"object\"}")]
            : null;
        RuntimeStructuredOutput? structuredOutput = null;
        if (capability == "structured_output")
        {
            using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
            structuredOutput = new RuntimeStructuredOutput("answer", schema.RootElement.Clone());
        }

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            ProviderAdapterTestFactory.CreateRequest(tools, structuredOutput));

        var failed = Assert.IsInstanceOfType<InvocationFailedProviderEvent>(
            Assert.ContainsSingle(events));
        Assert.AreEqual(RuntimeErrorCodes.CapabilityNotSupported, failed.Error.Code);
        Assert.AreEqual("capability", failed.Error.Category);
        Assert.IsEmpty(server.Requests);
    }
}
