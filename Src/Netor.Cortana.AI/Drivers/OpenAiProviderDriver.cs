using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.Drivers;

public sealed class OpenAiProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("OpenAI", "OpenAI 兼容", true);

    public override bool SupportsImageGeneration(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return string.Equals(provider.ProviderType, Definition.Id, StringComparison.OrdinalIgnoreCase)
            && model.OutputCapabilities.HasFlag(OutputCapabilities.Image);
    }

    public override bool SupportsVideoGeneration(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return string.Equals(provider.ProviderType, Definition.Id, StringComparison.OrdinalIgnoreCase)
            && model.OutputCapabilities.HasFlag(OutputCapabilities.Video);
    }

    public override async Task<IReadOnlyList<ImageGenerationResult>> GenerateImagesAsync(
        ProviderImageGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var provider = request.Provider;
        var model = request.Model;
        var endpoint = BuildImageGenerationEndpoint(provider.Url);
        using var httpClient = _httpClientFactory.CreateClient("OpenAiCompatible");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new OpenAiImageGenerationRequest(
                model.Name,
                request.Prompt,
                1,
                "1024x1024",
                "b64_json"),
                OpenAiMediaGenerationJsonContext.Default.OpenAiImageGenerationRequest)
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {provider.Key}");

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(
            OpenAiMediaGenerationJsonContext.Default.OpenAiImageGenerationResponse,
            cancellationToken);
        if (payload?.Data is null || payload.Data.Count == 0)
        {
            return [];
        }

        var results = new List<ImageGenerationResult>(payload.Data.Count);
        for (var index = 0; index < payload.Data.Count; index++)
        {
            var item = payload.Data[index];
            byte[]? bytes = null;
            if (!string.IsNullOrWhiteSpace(item.B64Json))
            {
                bytes = Convert.FromBase64String(item.B64Json);
            }
            else if (!string.IsNullOrWhiteSpace(item.Url))
            {
                bytes = await httpClient.GetByteArrayAsync(item.Url, cancellationToken);
            }

            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            results.Add(new ImageGenerationResult(
                $"generated-image-{index + 1}.png",
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

        var createEndpoint = BuildVideoEndpoint(provider.Url);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(model.Name), "model" },
            { new StringContent(request.Prompt), "prompt" },
            { new StringContent("4"), "seconds" },
            { new StringContent("1280x720"), "size" },
        };
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, createEndpoint)
        {
            Content = form
        };
        createRequest.Headers.Add("Authorization", $"Bearer {provider.Key}");

        using var createResponse = await httpClient.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(createResponse, cancellationToken);

        var video = await createResponse.Content.ReadFromJsonAsync(
            OpenAiMediaGenerationJsonContext.Default.OpenAiVideoResponse,
            cancellationToken);
        if (video is null || string.IsNullOrWhiteSpace(video.Id))
        {
            return [];
        }

        video = await WaitForVideoCompletionAsync(httpClient, provider, video, cancellationToken);
        if (!string.Equals(video.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var errorMessage = video.Error?.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage)
                ? $"视频生成任务未完成：{video.Status}"
                : $"视频生成任务失败：{errorMessage}");
        }

        var contentEndpoint = BuildVideoContentEndpoint(provider.Url, video.Id);
        using var contentRequest = new HttpRequestMessage(HttpMethod.Get, contentEndpoint);
        contentRequest.Headers.Add("Authorization", $"Bearer {provider.Key}");
        using var contentResponse = await httpClient.SendAsync(
            contentRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(contentResponse, cancellationToken);

        var bytes = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            return [];
        }

        var mimeType = contentResponse.Content.Headers.ContentType?.MediaType;
        return
        [
            new VideoGenerationResult(
                $"{video.Id}.mp4",
                string.IsNullOrWhiteSpace(mimeType) ? "video/mp4" : mimeType,
                bytes)
        ];
    }

    private static string BuildImageGenerationEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed + "/images/generations"
            : trimmed + "/v1/images/generations";
    }

    private static string BuildVideoEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed + "/videos"
            : trimmed + "/v1/videos";
    }

    private static string BuildVideoContentEndpoint(string baseUrl, string videoId)
    {
        var videosEndpoint = BuildVideoEndpoint(baseUrl);
        return $"{videosEndpoint}/{Uri.EscapeDataString(videoId)}/content";
    }

    private static string BuildVideoRetrieveEndpoint(string baseUrl, string videoId)
    {
        var videosEndpoint = BuildVideoEndpoint(baseUrl);
        return $"{videosEndpoint}/{Uri.EscapeDataString(videoId)}";
    }

    private static async Task<OpenAiVideoResponse> WaitForVideoCompletionAsync(
        HttpClient httpClient,
        AiProviderEntity provider,
        OpenAiVideoResponse initialVideo,
        CancellationToken cancellationToken)
    {
        var video = initialVideo;
        var startedAt = TimeProvider.System.GetTimestamp();
        var timeout = TimeSpan.FromMinutes(20);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (string.Equals(video.Status, "completed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(video.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return video;
            }

            if (TimeProvider.System.GetElapsedTime(startedAt) > timeout)
            {
                throw new TimeoutException("视频生成任务等待超时。");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            var endpoint = BuildVideoRetrieveEndpoint(provider.Url, video.Id);
            using var retrieveRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
            retrieveRequest.Headers.Add("Authorization", $"Bearer {provider.Key}");
            using var response = await httpClient.SendAsync(
                retrieveRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var latest = await response.Content.ReadFromJsonAsync(
                OpenAiMediaGenerationJsonContext.Default.OpenAiVideoResponse,
                cancellationToken);
            if (latest is not null)
            {
                video = latest;
            }
        }

        throw new OperationCanceledException(cancellationToken);
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
                ? $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase})."
                : $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}",
            null,
            response.StatusCode);
    }
}

internal sealed record OpenAiImageGenerationRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("n")] int Count,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("response_format")] string ResponseFormat);

internal sealed class OpenAiImageGenerationResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiImageGenerationData> Data { get; set; } = [];
}

internal sealed class OpenAiImageGenerationData
{
    [JsonPropertyName("b64_json")]
    public string? B64Json { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class OpenAiVideoResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("error")]
    public OpenAiVideoError? Error { get; set; }
}

internal sealed class OpenAiVideoError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

[JsonSerializable(typeof(OpenAiImageGenerationResponse))]
[JsonSerializable(typeof(OpenAiImageGenerationRequest))]
[JsonSerializable(typeof(OpenAiVideoResponse))]
internal sealed partial class OpenAiMediaGenerationJsonContext : JsonSerializerContext
{
}
