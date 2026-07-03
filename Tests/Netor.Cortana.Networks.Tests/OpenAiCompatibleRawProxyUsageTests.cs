using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys.Proxy;
using Netor.Cortana.Networks.Proxy;

namespace Netor.Cortana.Networks.Tests.Proxy;

[TestClass]
public sealed class OpenAiCompatibleRawProxyUsageTests
{
    [TestMethod]
    public void RecordUsageFromRawResponse_RecordsNonStreamingOpenAiUsage()
    {
        var tracker = new ProxyUsageTracker();
        var responseBody = Encoding.UTF8.GetBytes("""
            {
              "id": "chatcmpl-test",
              "object": "chat.completion",
              "created": 1710000000,
              "model": "test-model",
              "choices": [
                {
                  "index": 0,
                  "message": { "role": "assistant", "content": "ok" },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 123,
                "completion_tokens": 45,
                "total_tokens": 168
              }
            }
            """);

        OpenAiCompatibleRawProxy.RecordUsageFromRawResponseForTesting(
            tracker,
            statusCode: 200,
            contentType: "application/json; charset=utf-8",
            responseBody);

        var snapshot = tracker.GetSnapshot();
        Assert.AreEqual(123, snapshot.LastInputTokens);
        Assert.AreEqual(45, snapshot.TotalOutputTokens);
    }

    [TestMethod]
    public void RecordUsageFromRawResponse_RecordsStreamingOpenAiUsageFromFinalSseChunk()
    {
        var tracker = new ProxyUsageTracker();
        var responseBody = Encoding.UTF8.GetBytes("""
            data: {"id":"chunk-1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"你"},"finish_reason":null}]}

            data: {"id":"chunk-2","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":88,"completion_tokens":12,"total_tokens":100}}

            data: [DONE]

            """);

        OpenAiCompatibleRawProxy.RecordUsageFromRawResponseForTesting(
            tracker,
            statusCode: 200,
            contentType: "text/event-stream; charset=utf-8",
            responseBody);

        var snapshot = tracker.GetSnapshot();
        Assert.AreEqual(88, snapshot.LastInputTokens);
        Assert.AreEqual(12, snapshot.TotalOutputTokens);
    }
}
