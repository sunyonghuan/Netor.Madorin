using System.Net;

using Netor.Cortana.Entitys.Proxy;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama/OpenAI 兼容协议路由分发器。
/// </summary>
public sealed class ProxyRouteDispatcher(
    ProxyModelEndpoints modelEndpoints,
    ProxyChatEndpoints chatEndpoints,
    OpenAiCompatibleEndpoints openAiEndpoints)
{
    public async Task DispatchAsync(
        HttpListenerContext context,
        AiProxyOptionsSnapshot options,
        Func<Func<Task>, HttpListenerResponse, CancellationToken, Task> withLimiter,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        var path = NormalizePath(request.Url?.AbsolutePath);

        if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await OllamaHttpResponseWriter.WriteOptionsAsync(response, cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && path == "/")
        {
            response.StatusCode = 200;
            return;
        }

        if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/")
        {
            await OllamaHttpResponseWriter.WriteTextAsync(response, 200, "Ollama is running", cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/api/version")
        {
            await OllamaHttpResponseWriter.WriteJsonAsync(
                response,
                200,
                new OllamaVersionResponse(options.Version),
                OllamaProxyJsonContext.Default.OllamaVersionResponse,
                cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/api/tags")
        {
            await modelEndpoints.HandleTagsAsync(response, cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/api/chat")
        {
            await withLimiter(() => chatEndpoints.HandleChatAsync(context, options, cancellationToken), response, cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/api/generate")
        {
            await withLimiter(() => chatEndpoints.HandleGenerateAsync(context, options, cancellationToken), response, cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/api/show")
        {
            await modelEndpoints.HandleShowAsync(context, cancellationToken);
            return;
        }

        if (path == "/api/ps" && (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) || request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)))
        {
            await OllamaHttpResponseWriter.WriteJsonAsync(
                response,
                200,
                new OllamaRunningModelsResponse([]),
                OllamaProxyJsonContext.Default.OllamaRunningModelsResponse,
                cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/v1/models")
        {
            await modelEndpoints.HandleV1ModelsAsync(response, cancellationToken);
            return;
        }

        if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/v1/chat/completions")
        {
            await withLimiter(() => openAiEndpoints.HandleChatCompletionsAsync(context, cancellationToken), response, cancellationToken);
            return;
        }

        await OllamaHttpResponseWriter.WriteErrorAsync(response, 404, "not found", cancellationToken);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }
}
