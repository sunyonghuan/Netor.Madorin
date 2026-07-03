using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

public sealed class OllamaProviderDriver(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : OpenAiCompatibleProviderDriverBase(httpClientFactory, loggerFactory)
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Ollama", "Ollama", true);

    public override async Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var httpClient = _httpClientFactory.CreateClient();
        var endpoint = BuildTagsEndpoint(provider.Url);
        var payload = await httpClient.GetFromJsonAsync(
            endpoint,
            OllamaDriverJsonContext.Default.OllamaTagsResponse,
            cancellationToken).ConfigureAwait(false);

        if (payload?.Models is null)
        {
            return [];
        }

        return payload.Models.Select(model => new RemoteModelDescriptor(
            model.Name,
            model.Name,
            model.Details?.Family,
            "chat",
            null)).ToArray();
    }

    private static string BuildTagsEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        return trimmed + "/api/tags";
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaTagModel))]
[JsonSerializable(typeof(OllamaTagDetails))]
internal sealed partial class OllamaDriverJsonContext : JsonSerializerContext;

internal sealed record OllamaTagsResponse(IReadOnlyList<OllamaTagModel> Models);

internal sealed record OllamaTagModel(string Name, OllamaTagDetails? Details);

internal sealed record OllamaTagDetails(string? Family);
