namespace Madorin.AI.Runtime.Provider.Tests;

internal static class ProviderProtocolScripts
{
    public static FakeProviderResponse NormalText(ProviderAdapterKind kind, bool fragmented = false)
    {
        var payload = GetTextPayload(kind);

        if (!fragmented)
        {
            return FakeProviderResponse.EventStream(new FakeProviderChunk(payload));
        }

        var midpoint = payload.IndexOf("Hel", StringComparison.Ordinal) + 2;
        return FakeProviderResponse.EventStream(
            new FakeProviderChunk(payload[..midpoint]),
            new FakeProviderChunk(payload[midpoint..]));
    }

    public static FakeProviderResponse SlowText(ProviderAdapterKind kind, TimeSpan delay)
    {
        var payload = GetTextPayload(kind);
        var split = GetFirstTextEventEnd(payload);
        return FakeProviderResponse.EventStream(
            new FakeProviderChunk(payload[..split]),
            new FakeProviderChunk(payload[split..], delay));
    }

    public static FakeProviderResponse DelayedText(ProviderAdapterKind kind, TimeSpan delay) =>
        FakeProviderResponse.EventStream(new FakeProviderChunk(GetTextPayload(kind), delay));

    public static FakeProviderResponse CompatibleTextWithoutUsage() =>
        FakeProviderResponse.EventStream(new FakeProviderChunk(CompatibleTextWithoutUsagePayload));

    public static FakeProviderResponse InvalidJson(ProviderAdapterKind kind)
    {
        var payload = GetTextPayload(kind);
        var split = GetFirstTextEventEnd(payload);
        return FakeProviderResponse.EventStream(
            new FakeProviderChunk(payload[..split]),
            new FakeProviderChunk("data: {not-json}\n\n"));
    }

    public static FakeProviderResponse Disconnected(ProviderAdapterKind kind)
    {
        var payload = GetTextPayload(kind);
        var split = GetFirstTextEventEnd(payload);
        return FakeProviderResponse.EventStream(new FakeProviderChunk(payload[..split])) with
        {
            AbortAfterChunks = true,
            AbortDelay = TimeSpan.FromMilliseconds(100)
        };
    }

    public static string ExpectedPathSuffix(ProviderAdapterKind kind) => kind switch
    {
        ProviderAdapterKind.OpenAI => "/v1/responses",
        ProviderAdapterKind.Anthropic => "/v1/messages",
        ProviderAdapterKind.OpenAICompatible => "/v1/chat/completions",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string GetTextPayload(ProviderAdapterKind kind) => kind switch
    {
        ProviderAdapterKind.OpenAI => OpenAIText,
        ProviderAdapterKind.Anthropic => AnthropicText,
        ProviderAdapterKind.OpenAICompatible => CompatibleText,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static int GetFirstTextEventEnd(string payload)
    {
        var textIndex = payload.IndexOf("Hel", StringComparison.Ordinal);
        var eventEnd = payload.IndexOf("\n\n", textIndex, StringComparison.Ordinal);
        return eventEnd < 0
            ? throw new InvalidOperationException("The Provider script has no complete text event.")
            : eventEnd + 2;
    }

    public static FakeProviderResponse ToolCall(ProviderAdapterKind kind)
    {
        var payload = kind switch
        {
            ProviderAdapterKind.OpenAI => OpenAIToolCall,
            ProviderAdapterKind.Anthropic => AnthropicToolCall,
            ProviderAdapterKind.OpenAICompatible => CompatibleToolCall,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return FakeProviderResponse.EventStream(new FakeProviderChunk(payload));
    }

    public static FakeProviderResponse Reasoning(ProviderAdapterKind kind)
    {
        var payload = kind switch
        {
            ProviderAdapterKind.OpenAI => OpenAIReasoning,
            ProviderAdapterKind.Anthropic => AnthropicReasoning,
            ProviderAdapterKind.OpenAICompatible => CompatibleReasoning,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        return FakeProviderResponse.EventStream(new FakeProviderChunk(payload));
    }

    private const string CompatibleText =
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\n" +
        "data: [DONE]\n\n";

    private const string CompatibleTextWithoutUsagePayload =
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private const string CompatibleToolCall =
        "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"Shanghai\\\"}\"}}]},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}\n\n" +
        "data: [DONE]\n\n";

    private const string CompatibleReasoning =
        "data: {\"id\":\"chatcmpl-reasoning\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"think\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-reasoning\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\n" +
        "data: [DONE]\n\n";

    private const string AnthropicText =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"test-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private const string AnthropicToolCall =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-tool\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"test-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call-1\",\"name\":\"get_weather\",\"input\":{}}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"city\\\":\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"Shanghai\\\"}\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private const string AnthropicReasoning =
        "event: message_start\n" +
        "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-reasoning\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"test-model\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\n" +
        "event: content_block_start\n" +
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"think\"}}\n\n" +
        "event: content_block_delta\n" +
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"anthropic-signature-marker\"}}\n\n" +
        "event: content_block_stop\n" +
        "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"input_tokens\":5,\"output_tokens\":1}}\n\n" +
        "event: message_stop\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private const string OpenAIText =
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg-1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hel\",\"logprobs\":[]}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":2,\"item_id\":\"msg-1\",\"output_index\":0,\"content_index\":0,\"delta\":\"lo\",\"logprobs\":[]}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"sequence_number\":3,\"response\":{\"id\":\"resp-1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"error\":null,\"incomplete_details\":null,\"instructions\":null,\"max_output_tokens\":128,\"model\":\"test-model\",\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null,\"reasoning\":{\"effort\":null,\"summary\":null},\"store\":false,\"temperature\":0.25,\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":1.0,\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":2,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":7},\"metadata\":{}}}\n\n" +
        "data: [DONE]\n\n";

    private const string OpenAIToolCall =
        "event: response.output_item.added\n" +
        "data: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"fc-1\",\"type\":\"function_call\",\"status\":\"in_progress\",\"arguments\":\"\",\"call_id\":\"call-1\",\"name\":\"get_weather\"}}\n\n" +
        "event: response.function_call_arguments.delta\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":2,\"item_id\":\"fc-1\",\"output_index\":0,\"delta\":\"{\\\"city\\\":\"}\n\n" +
        "event: response.function_call_arguments.delta\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":3,\"item_id\":\"fc-1\",\"output_index\":0,\"delta\":\"\\\"Shanghai\\\"}\"}\n\n" +
        "event: response.function_call_arguments.done\n" +
        "data: {\"type\":\"response.function_call_arguments.done\",\"sequence_number\":4,\"item_id\":\"fc-1\",\"output_index\":0,\"arguments\":\"{\\\"city\\\":\\\"Shanghai\\\"}\"}\n\n" +
        "event: response.output_item.done\n" +
        "data: {\"type\":\"response.output_item.done\",\"sequence_number\":5,\"output_index\":0,\"item\":{\"id\":\"fc-1\",\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{\\\"city\\\":\\\"Shanghai\\\"}\",\"call_id\":\"call-1\",\"name\":\"get_weather\"}}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"sequence_number\":6,\"response\":{\"id\":\"resp-tool\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"error\":null,\"incomplete_details\":null,\"instructions\":null,\"max_output_tokens\":128,\"model\":\"test-model\",\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null,\"reasoning\":{\"effort\":null,\"summary\":null},\"store\":false,\"temperature\":0.25,\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":1.0,\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":3,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":8},\"metadata\":{}}}\n\n" +
        "data: [DONE]\n\n";

    private const string OpenAIReasoning =
        "event: response.reasoning_summary_text.delta\n" +
        "data: {\"type\":\"response.reasoning_summary_text.delta\",\"sequence_number\":1,\"item_id\":\"reasoning-1\",\"output_index\":0,\"summary_index\":0,\"delta\":\"think\"}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"sequence_number\":2,\"response\":{\"id\":\"resp-reasoning\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"error\":null,\"incomplete_details\":null,\"instructions\":null,\"max_output_tokens\":128,\"model\":\"test-model\",\"output\":[],\"parallel_tool_calls\":true,\"previous_response_id\":null,\"reasoning\":{\"effort\":\"medium\",\"summary\":\"auto\"},\"store\":false,\"temperature\":null,\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":1.0,\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":1,\"output_tokens_details\":{\"reasoning_tokens\":1},\"total_tokens\":6},\"metadata\":{}}}\n\n" +
        "data: [DONE]\n\n";
}
