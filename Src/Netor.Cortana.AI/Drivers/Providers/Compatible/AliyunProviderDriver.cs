using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.Drivers;

public sealed class AliyunProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Aliyun", "阿里云百炼 / DashScope", true);

    public override bool CanHandle(AiProviderEntity provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return base.CanHandle(provider)
            || provider.Url.Contains("dashscope.aliyuncs.com", StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains("Aliyun", StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains("阿里", StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains("通义", StringComparison.OrdinalIgnoreCase);
    }

    public override bool SupportsImageGeneration(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return CanHandle(provider)
            && model.OutputCapabilities.HasFlag(OutputCapabilities.Image)
            && IsAliyunImageModel(model.Name);
    }

    public override bool SupportsVideoGeneration(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return CanHandle(provider)
            && model.OutputCapabilities.HasFlag(OutputCapabilities.Video)
            && IsWanVideoModel(model.Name);
    }

    public override async Task<IReadOnlyList<ImageGenerationResult>> GenerateImagesAsync(
        ProviderImageGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = request.Provider;
        var model = request.Model;
        using var httpClient = _httpClientFactory.CreateClient("OpenAiCompatible");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildImageGenerationEndpoint(provider.Url))
        {
            Content = JsonContent.Create(
                new AliyunImageGenerationRequest(
                    model.Name,
                    new AliyunImageGenerationInput(
                    [
                        new AliyunImageGenerationMessage(
                            "user",
                            [new AliyunImageGenerationContent(request.Prompt)])
                    ]),
                    new AliyunImageGenerationParameters(
                        Size: "1024*1024",
                        Count: 1,
                        PromptExtend: true,
                        Watermark: false)),
                AliyunVideoGenerationJsonContext.Default.AliyunImageGenerationRequest)
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {provider.Key}");

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync(
            AliyunVideoGenerationJsonContext.Default.AliyunImageGenerationResponse,
            cancellationToken);
        var imageUrls = payload?.Output?.Choices?
            .SelectMany(static choice => choice.Message?.Content ?? [])
            .Select(static content => content.Image)
            .Where(static image => !string.IsNullOrWhiteSpace(image))
            .ToArray() ?? [];
        if (imageUrls.Length == 0)
        {
            return [];
        }

        var results = new List<ImageGenerationResult>(imageUrls.Length);
        for (var index = 0; index < imageUrls.Length; index++)
        {
            var bytes = await httpClient.GetByteArrayAsync(imageUrls[index], cancellationToken);
            if (bytes.Length == 0)
            {
                continue;
            }

            results.Add(new ImageGenerationResult(
                $"aliyun-generated-image-{index + 1}.png",
                "image/png",
                bytes));
        }

        return results;
    }

    public override async Task<IReadOnlyList<VideoGenerationResult>> GenerateVideosAsync(
        ProviderVideoGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = request.Provider;
        var model = request.Model;
        using var httpClient = _httpClientFactory.CreateClient("OpenAiCompatible");

        var createEndpoint = BuildVideoSynthesisEndpoint(provider.Url);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, createEndpoint)
        {
            Content = JsonContent.Create(
                new DashScopeVideoCreateRequest(
                    model.Name,
                    new DashScopeVideoInput(request.Prompt),
                    new DashScopeVideoParameters(
                        Size: "1280*720",
                        Duration: 5,
                        PromptExtend: true,
                        Watermark: false)),
                AliyunVideoGenerationJsonContext.Default.DashScopeVideoCreateRequest)
        };
        createRequest.Headers.Add("Authorization", $"Bearer {provider.Key}");
        createRequest.Headers.Add("X-DashScope-Async", "enable");

        using var createResponse = await httpClient.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(createResponse, cancellationToken);

        var task = await createResponse.Content.ReadFromJsonAsync(
            AliyunVideoGenerationJsonContext.Default.DashScopeTaskResponse,
            cancellationToken);
        var taskId = task?.Output?.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return [];
        }

        task = await WaitForTaskCompletionAsync(httpClient, provider, taskId, cancellationToken);
        var output = task.Output;
        if (!string.Equals(output?.TaskStatus, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            var message = output?.Message ?? output?.ErrorMessage ?? task.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"视频生成任务未完成：{output?.TaskStatus}"
                : $"视频生成任务失败：{message}");
        }

        var videoUrl = ResolveVideoUrl(output);
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            return [];
        }

        var bytes = await httpClient.GetByteArrayAsync(videoUrl, cancellationToken);
        if (bytes.Length == 0)
        {
            return [];
        }

        return
        [
            new VideoGenerationResult(
                $"{taskId}.mp4",
                "video/mp4",
                bytes)
        ];
    }

    private static bool IsWanVideoModel(string modelName)
        => modelName.Contains("wan", StringComparison.OrdinalIgnoreCase)
            && (modelName.Contains("t2v", StringComparison.OrdinalIgnoreCase)
                || modelName.Contains("i2v", StringComparison.OrdinalIgnoreCase)
                || modelName.Contains("r2v", StringComparison.OrdinalIgnoreCase));

    private static bool IsAliyunImageModel(string modelName)
        => modelName.Contains("qwen-image", StringComparison.OrdinalIgnoreCase)
            || (modelName.Contains("wan", StringComparison.OrdinalIgnoreCase)
                && modelName.Contains("image", StringComparison.OrdinalIgnoreCase));

    private static string BuildImageGenerationEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = NormalizeDashScopeApiBase(baseUrl);
        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/services/aigc/multimodal-generation/generation";
        }

        return trimmed + "/api/v1/services/aigc/multimodal-generation/generation";
    }

    private static string BuildVideoSynthesisEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = NormalizeDashScopeApiBase(baseUrl);
        if (trimmed.EndsWith("/services/aigc/video-generation/video-synthesis", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/services/aigc/video-generation/video-synthesis";
        }

        return trimmed + "/api/v1/services/aigc/video-generation/video-synthesis";
    }

    private static string BuildTaskEndpoint(string baseUrl, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = NormalizeDashScopeApiBase(baseUrl);
        if (trimmed.EndsWith("/services/aigc/video-generation/video-synthesis", StringComparison.OrdinalIgnoreCase))
        {
            var apiBase = trimmed[..trimmed.IndexOf("/services/aigc/video-generation/video-synthesis", StringComparison.OrdinalIgnoreCase)];
            return $"{apiBase}/tasks/{Uri.EscapeDataString(taskId)}";
        }

        if (trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}/tasks/{Uri.EscapeDataString(taskId)}";
        }

        return $"{trimmed}/api/v1/tasks/{Uri.EscapeDataString(taskId)}";
    }

    private static string NormalizeDashScopeApiBase(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        const string compatibleModeSuffix = "/compatible-mode/v1";
        return trimmed.EndsWith(compatibleModeSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^compatibleModeSuffix.Length]
            : trimmed;
    }

    private static async Task<DashScopeTaskResponse> WaitForTaskCompletionAsync(
        HttpClient httpClient,
        AiProviderEntity provider,
        string taskId,
        CancellationToken cancellationToken)
    {
        var startedAt = TimeProvider.System.GetTimestamp();
        var timeout = TimeSpan.FromMinutes(20);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TimeProvider.System.GetElapsedTime(startedAt) > timeout)
            {
                throw new TimeoutException("视频生成任务等待超时。");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            var endpoint = BuildTaskEndpoint(provider.Url, taskId);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("Authorization", $"Bearer {provider.Key}");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var task = await response.Content.ReadFromJsonAsync(
                AliyunVideoGenerationJsonContext.Default.DashScopeTaskResponse,
                cancellationToken);
            if (task?.Output is null)
            {
                continue;
            }

            if (string.Equals(task.Output.TaskStatus, "SUCCEEDED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Output.TaskStatus, "FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Output.TaskStatus, "CANCELED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Output.TaskStatus, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static string? ResolveVideoUrl(DashScopeTaskOutput? output)
    {
        if (output is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(output.VideoUrl))
        {
            return output.VideoUrl;
        }

        return output.Results?
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.Url) || !string.IsNullOrWhiteSpace(item.VideoUrl))
            ?.Url
            ?? output.Results?.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.VideoUrl))?.VideoUrl;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(body)
                ? $"DashScope 返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。"
                : $"DashScope 返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})：{body}",
            null,
            response.StatusCode);
    }
}

internal sealed record AliyunImageGenerationRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] AliyunImageGenerationInput Input,
    [property: JsonPropertyName("parameters")] AliyunImageGenerationParameters Parameters);

internal sealed record AliyunImageGenerationInput(
    [property: JsonPropertyName("messages")] IReadOnlyList<AliyunImageGenerationMessage> Messages);

internal sealed record AliyunImageGenerationMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] IReadOnlyList<AliyunImageGenerationContent> Content);

internal sealed record AliyunImageGenerationContent(
    [property: JsonPropertyName("text")] string Text);

internal sealed record AliyunImageGenerationParameters(
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("n")] int Count,
    [property: JsonPropertyName("prompt_extend")] bool PromptExtend,
    [property: JsonPropertyName("watermark")] bool Watermark);

internal sealed class AliyunImageGenerationResponse
{
    [JsonPropertyName("output")]
    public AliyunImageGenerationOutput? Output { get; set; }
}

internal sealed class AliyunImageGenerationOutput
{
    [JsonPropertyName("choices")]
    public List<AliyunImageGenerationChoice>? Choices { get; set; }
}

internal sealed class AliyunImageGenerationChoice
{
    [JsonPropertyName("message")]
    public AliyunImageGenerationAssistantMessage? Message { get; set; }
}

internal sealed class AliyunImageGenerationAssistantMessage
{
    [JsonPropertyName("content")]
    public List<AliyunImageGenerationResultContent>? Content { get; set; }
}

internal sealed class AliyunImageGenerationResultContent
{
    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

internal sealed record DashScopeVideoCreateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] DashScopeVideoInput Input,
    [property: JsonPropertyName("parameters")] DashScopeVideoParameters Parameters);

internal sealed record DashScopeVideoInput(
    [property: JsonPropertyName("prompt")] string Prompt);

internal sealed record DashScopeVideoParameters(
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("duration")] int Duration,
    [property: JsonPropertyName("prompt_extend")] bool PromptExtend,
    [property: JsonPropertyName("watermark")] bool Watermark);

internal sealed class DashScopeTaskResponse
{
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("output")]
    public DashScopeTaskOutput? Output { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal sealed class DashScopeTaskOutput
{
    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("task_status")]
    public string? TaskStatus { get; set; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("results")]
    public List<DashScopeVideoResult>? Results { get; set; }
}

internal sealed class DashScopeVideoResult
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }
}

[JsonSerializable(typeof(DashScopeVideoCreateRequest))]
[JsonSerializable(typeof(DashScopeTaskResponse))]
[JsonSerializable(typeof(AliyunImageGenerationRequest))]
[JsonSerializable(typeof(AliyunImageGenerationResponse))]
internal sealed partial class AliyunVideoGenerationJsonContext : JsonSerializerContext
{
}
