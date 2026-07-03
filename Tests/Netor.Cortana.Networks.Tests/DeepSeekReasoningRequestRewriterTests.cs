using System.Text;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Networks.Proxy;

namespace Netor.Cortana.Networks.Tests.Proxy;

[TestClass]
public sealed class DeepSeekReasoningRequestRewriterTests
{
    [TestMethod]
    public void IsDeepSeekProvider_ReturnsFalseForOpenAiProvider()
    {
        var provider = new AiProviderEntity
        {
            Name = "OpenAI",
            ProviderType = "OpenAI",
            Url = "https://api.openai.com/v1"
        };

        Assert.IsFalse(DeepSeekReasoningRequestRewriter.IsDeepSeekProvider(provider));
    }

    [TestMethod]
    public void RewriteRequest_AddsEmptyReasoningContentForDeepSeekAssistant()
    {
        var root = JsonNode.Parse("""
            {
              "model": "deepseek-reasoner",
              "messages": [
                { "role": "user", "content": "hi" },
                { "role": "assistant", "content": "hello" }
              ]
            }
            """)!.AsObject();

        DeepSeekReasoningRequestRewriter.RewriteRequest(root, string.Empty);

        var assistant = root["messages"]!.AsArray()[1]!.AsObject();
        Assert.AreEqual(string.Empty, assistant["reasoning_content"]!.GetValue<string>());
    }

    [TestMethod]
    public void RewriteRequest_CopiesReasoningToReasoningContent()
    {
        var root = JsonNode.Parse("""
            {
              "model": "deepseek-reasoner",
              "messages": [
                { "role": "assistant", "content": "answer", "reasoning": "think" }
              ]
            }
            """)!.AsObject();

        DeepSeekReasoningRequestRewriter.RewriteRequest(root, string.Empty);

        var assistant = root["messages"]!.AsArray()[0]!.AsObject();
        Assert.AreEqual("think", assistant["reasoning_content"]!.GetValue<string>());
    }

    [TestMethod]
    public void RewriteRequest_ReplaysCachedReasoningToLastAssistantWithToolCalls()
    {
        var root = JsonNode.Parse("""
            {
              "model": "deepseek-reasoner",
              "messages": [
                { "role": "assistant", "content": "first" },
                { "role": "assistant", "content": null, "tool_calls": [{ "id": "call_1", "type": "function", "function": { "name": "test", "arguments": "{}" } }] }
              ]
            }
            """)!.AsObject();

        DeepSeekReasoningRequestRewriter.RewriteRequest(root, "cached reasoning");

        var messages = root["messages"]!.AsArray();
        Assert.AreEqual(string.Empty, messages[0]!["reasoning_content"]!.GetValue<string>());
        Assert.AreEqual("cached reasoning", messages[1]!["reasoning_content"]!.GetValue<string>());
    }

    [TestMethod]
    public void ExtractReasoningFromResponse_ReadsNonStreamingReasoningContent()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "choices": [
                { "message": { "role": "assistant", "reasoning_content": "step one" } }
              ]
            }
            """);

        var reasoning = DeepSeekReasoningRequestRewriter.ExtractReasoningFromResponse(
            "application/json; charset=utf-8",
            body);

        Assert.AreEqual("step one", reasoning);
    }

    [TestMethod]
    public void ExtractReasoningFromResponse_ReadsStreamingReasoningContent()
    {
        var body = Encoding.UTF8.GetBytes("""
            data: {"choices":[{"delta":{"reasoning_content":"step "}}]}

            data: {"choices":[{"delta":{"reasoning_content":"two"}}]}

            data: [DONE]

            """);

        var reasoning = DeepSeekReasoningRequestRewriter.ExtractReasoningFromResponse(
            "text/event-stream; charset=utf-8",
            body);

        Assert.AreEqual("step two", reasoning);
    }

    [TestMethod]
    public void ReplayCache_IsolatesByProviderModelAndClient()
    {
        var cache = new DeepSeekReasoningReplayCache();
        var provider1 = new AiProviderEntity { Id = "p1" };
        var provider2 = new AiProviderEntity { Id = "p2" };

        var key1 = cache.BuildKey(provider1, "model", "client");
        var key2 = cache.BuildKey(provider2, "model", "client");

        cache.Append(key1, "reasoning 1");
        cache.Append(key2, "reasoning 2");

        Assert.AreEqual("reasoning 1", cache.GetReplayText(key1));
        Assert.AreEqual("reasoning 2", cache.GetReplayText(key2));
    }
}
