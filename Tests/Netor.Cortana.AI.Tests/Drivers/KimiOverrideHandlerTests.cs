using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Drivers;

using System.Net;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.AI.Tests.Drivers;

[TestClass]
public sealed class KimiOverrideHandlerTests
{
    [TestMethod]
    public async Task SendAsync_WhenMessagesHaveNoToolCalls_InjectsThinkingOnly()
    {
        var body = """
            {
              "model": "kimi-k2",
              "messages": [
                { "role": "user", "content": "hello" },
                { "role": "assistant", "content": "hi" }
              ]
            }
            """;

        var rewrittenBody = await SendAndCaptureBodyAsync(body);

        using var document = JsonDocument.Parse(rewrittenBody);
        var root = document.RootElement;

        Assert.AreEqual("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        foreach (var message in root.GetProperty("messages").EnumerateArray())
        {
            Assert.IsFalse(message.TryGetProperty("partial", out _));
        }
    }

    [TestMethod]
    public async Task SendAsync_WhenAssistantMessagesHaveToolCalls_InjectsPartialAndThinking()
    {
        var body = """
            {
              "model": "kimi-k2",
              "messages": [
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{ "id": "call-1", "type": "function" }]
                },
                { "role": "tool", "tool_call_id": "call-1", "content": "done" },
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{ "id": "call-2", "type": "function" }]
                }
              ]
            }
            """;

        var rewrittenBody = await SendAndCaptureBodyAsync(body);

        using var document = JsonDocument.Parse(rewrittenBody);
        var root = document.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();

        Assert.AreEqual("enabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.IsTrue(messages[0].GetProperty("partial").GetBoolean());
        Assert.IsFalse(messages[1].TryGetProperty("partial", out _));
        Assert.IsTrue(messages[2].GetProperty("partial").GetBoolean());
    }

    [TestMethod]
    public async Task SendAsync_WhenContentArrayContainsEmptyTextPart_RemovesEmptyTextPart()
    {
        var body = """
            {
              "model": "kimi-k2",
              "thinking": { "type": "enabled" },
              "messages": [
                {
                  "role": "assistant",
                  "content": [
                    { "type": "text", "text": "" },
                    { "type": "text", "text": "我需要先确认当前工作区。" }
                  ],
                  "tool_calls": [{ "id": "call-1", "type": "function" }],
                  "partial": true
                }
              ]
            }
            """;

        var rewrittenBody = await SendAndCaptureBodyAsync(body);

        using var document = JsonDocument.Parse(rewrittenBody);
        var assistant = document.RootElement.GetProperty("messages")[0];
        var content = assistant.GetProperty("content").EnumerateArray().ToArray();

        Assert.HasCount(1, content);
        Assert.AreEqual("我需要先确认当前工作区。", content[0].GetProperty("text").GetString());
        Assert.IsTrue(assistant.GetProperty("partial").GetBoolean());
    }

    [TestMethod]
    public async Task SendAsync_WhenAssistantToolCallContentOnlyHasEmptyText_WritesNullContent()
    {
        var body = """
            {
              "model": "kimi-k2",
              "messages": [
                {
                  "role": "assistant",
                  "content": [{ "type": "text", "text": " " }],
                  "tool_calls": [{ "id": "call-1", "type": "function" }]
                }
              ]
            }
            """;

        var rewrittenBody = await SendAndCaptureBodyAsync(body);

        using var document = JsonDocument.Parse(rewrittenBody);
        var assistant = document.RootElement.GetProperty("messages")[0];

        Assert.AreEqual(JsonValueKind.Null, assistant.GetProperty("content").ValueKind);
        Assert.IsTrue(assistant.GetProperty("partial").GetBoolean());
    }

    [TestMethod]
    public async Task SendAsync_WhenThinkingAndPartialAlreadyExist_PreservesOriginalValues()
    {
        var body = """
            {
              "model": "kimi-k2",
              "thinking": { "type": "disabled" },
              "messages": [
                {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{ "id": "call-1", "type": "function" }],
                  "partial": false
                }
              ]
            }
            """;

        var rewrittenBody = await SendAndCaptureBodyAsync(body);

        using var document = JsonDocument.Parse(rewrittenBody);
        var root = document.RootElement;
        var assistant = root.GetProperty("messages")[0];

        Assert.AreEqual("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.IsFalse(assistant.GetProperty("partial").GetBoolean());
        Assert.AreEqual(1, CountProperties(root, "thinking"));
        Assert.AreEqual(1, CountProperties(assistant, "partial"));
    }

    [TestMethod]
    public async Task SendAsync_WhenContentTypeIsNotJson_PassesBodyThrough()
    {
        const string body = """{"messages":[{"role":"user","content":"hello"}]}""";

        var capturedBody = await SendAndCaptureBodyAsync(body, mediaType: "text/plain");

        Assert.AreEqual(body, capturedBody);
    }

    [TestMethod]
    [DataRow("""{"model":"kimi-k2"}""")]
    [DataRow("""{"messages":{"role":"user","content":"hello"}}""")]
    public async Task SendAsync_WhenMessagesAreMissingOrNotArray_PassesBodyThrough(string body)
    {
        var capturedBody = await SendAndCaptureBodyAsync(body);

        Assert.AreEqual(body, capturedBody);
    }

    private static async Task<string> SendAndCaptureBodyAsync(
        string body,
        string mediaType = "application/json")
    {
        var captureHandler = new CaptureHandler();
        using var handler = new KimiOverrideHandler(NullLogger<KimiOverrideHandler>.Instance)
        {
            InnerHandler = captureHandler,
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.moonshot.test/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(captureHandler.CapturedBody);
        return captureHandler.CapturedBody;
    }

    private static int CountProperties(JsonElement element, string propertyName)
    {
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(propertyName))
            {
                count++;
            }
        }

        return count;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
