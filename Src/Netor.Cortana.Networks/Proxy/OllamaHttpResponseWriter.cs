using Netor.Cortana.Entitys.Proxy;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 兼容 HTTP 响应写入工具。
/// 响应头对齐真实 Ollama：使用 Content-Length，不暴露 Server 头，非流式不加 CORS。
/// </summary>
internal static class OllamaHttpResponseWriter
{
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();
    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();
    private static readonly byte[] SseDoneBytes = "data: [DONE]\n\n"u8.ToArray();

    /// <summary>
    /// 写入 JSON 响应，使用 Content-Length 而非 chunked。
    /// </summary>
    public static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        int statusCode,
        T payload,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        SuppressServerHeader(response);
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// 写入原始上游响应，保留完整 JSON/SSE 协议字段。
    /// </summary>
    public static async Task WriteRawAsync(
        HttpListenerResponse response,
        int statusCode,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/json; charset=utf-8" : contentType;
        response.ContentLength64 = body.Length;
        SuppressServerHeader(response);
        await response.OutputStream.WriteAsync(body, cancellationToken);
    }

    public static Task WriteErrorAsync(
        HttpListenerResponse response,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        return WriteJsonAsync(
            response,
            statusCode,
            new OllamaErrorResponse(message),
            OllamaProxyJsonContext.Default.OllamaErrorResponse,
            cancellationToken);
    }

    /// <summary>
    /// 写入纯文本响应，使用 Content-Length。
    /// </summary>
    public static async Task WriteTextAsync(
        HttpListenerResponse response,
        int statusCode,
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        SuppressServerHeader(response);
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Ollama /api/chat 流式响应（ndjson）。
    /// </summary>
    public static async Task WriteChatStreamAsync(
        HttpListenerResponse response,
        string model,
        IAsyncEnumerable<AiProxyChatDelta> deltas,
        CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "application/x-ndjson; charset=utf-8";
        SuppressServerHeader(response);

        await foreach (var delta in deltas.WithCancellation(cancellationToken))
        {
            var payload = new OllamaChatResponse(
                model,
                FormatUtcNow(),
                new OllamaMessage("assistant", delta.Content),
                delta.Done,
                ToOllamaDoneReason(delta.FinishReason));

            await JsonSerializer.SerializeAsync(response.OutputStream, payload, OllamaProxyJsonContext.Default.OllamaChatResponse, cancellationToken);
            await response.OutputStream.WriteAsync(NewLineBytes, cancellationToken);
            await response.OutputStream.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Ollama /api/generate 流式响应（ndjson）。
    /// </summary>
    public static async Task WriteGenerateStreamAsync(
        HttpListenerResponse response,
        string model,
        IAsyncEnumerable<AiProxyChatDelta> deltas,
        CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "application/x-ndjson; charset=utf-8";
        SuppressServerHeader(response);

        await foreach (var delta in deltas.WithCancellation(cancellationToken))
        {
            var payload = new OllamaGenerateResponse(
                model,
                FormatUtcNow(),
                delta.Content,
                delta.Done,
                ToOllamaDoneReason(delta.FinishReason));

            await JsonSerializer.SerializeAsync(response.OutputStream, payload, OllamaProxyJsonContext.Default.OllamaGenerateResponse, cancellationToken);
            await response.OutputStream.WriteAsync(NewLineBytes, cancellationToken);
            await response.OutputStream.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// OpenAI /v1/chat/completions 流式响应（SSE）。
    /// </summary>
    public static async Task WriteOpenAiStreamAsync(
        HttpListenerResponse response,
        string model,
        string requestId,
        IAsyncEnumerable<AiProxyChatDelta> deltas,
        CancellationToken cancellationToken)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        SuppressServerHeader(response);

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await foreach (var delta in deltas.WithCancellation(cancellationToken))
        {
            var finishReason = delta.Done ? (ToOpenAiFinishReason(delta.FinishReason) ?? "stop") : null;
            var chunk = new OpenAiStreamChunk(
                requestId,
                "chat.completion.chunk",
                created,
                model,
                [new OpenAiChoice(0, Delta: new OllamaMessage("assistant", delta.Content), FinishReason: finishReason)]);

            await response.OutputStream.WriteAsync(SseDataPrefix, cancellationToken);
            await JsonSerializer.SerializeAsync(response.OutputStream, chunk, OllamaProxyJsonContext.Default.OpenAiStreamChunk, cancellationToken);
            await response.OutputStream.WriteAsync(NewLineBytes, cancellationToken);
            await response.OutputStream.WriteAsync(NewLineBytes, cancellationToken);
            await response.OutputStream.FlushAsync(cancellationToken);
        }

        await response.OutputStream.WriteAsync(SseDoneBytes, cancellationToken);
        await response.OutputStream.FlushAsync(cancellationToken);
    }

    public static Task WriteOptionsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = 204;
        response.ContentLength64 = 0;
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,HEAD,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-API-Key";
        SuppressServerHeader(response);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 尝试隐藏 Microsoft-HTTPAPI/2.0 Server 头。
    /// HttpListener 不允许直接移除，设为空字符串是最接近的做法。
    /// </summary>
    private static void SuppressServerHeader(HttpListenerResponse response)
    {
        try { response.Headers.Remove("Server"); } catch { }
    }

    private static string? ToOllamaDoneReason(AiProxyFinishReason reason) => reason switch
    {
        AiProxyFinishReason.Stop => "stop",
        AiProxyFinishReason.Length => "length",
        AiProxyFinishReason.Cancelled => "cancelled",
        AiProxyFinishReason.Error => "error",
        _ => null
    };

    private static string? ToOpenAiFinishReason(AiProxyFinishReason reason) => reason switch
    {
        AiProxyFinishReason.Stop => "stop",
        AiProxyFinishReason.Length => "length",
        _ => "stop"
    };

    /// <summary>
    /// 格式化 UTC 时间为 Ollama 风格（Z 结尾，纳秒精度）。
    /// </summary>
    internal static string FormatUtcNow() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
}
