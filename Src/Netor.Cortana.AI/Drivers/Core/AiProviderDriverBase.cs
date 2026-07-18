using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 厂商驱动基类，封装通用参数映射。
/// </summary>
public abstract class AiProviderDriverBase : IAiProviderDriver
{
    public abstract AiProviderDriverDefinition Definition { get; }

    public virtual bool CanHandle(AiProviderEntity provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return string.Equals(provider.ProviderType, Definition.Id, StringComparison.OrdinalIgnoreCase);
    }

    public abstract IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model);

    public abstract ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent);

    public virtual bool SupportsImageGeneration(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        return false;
    }

    public virtual Task<IReadOnlyList<ImageGenerationResult>> GenerateImagesAsync(
        ProviderImageGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new NotSupportedException($"厂商驱动 '{Definition.DisplayName}' 尚未实现图片生成。");
    }

    public virtual bool SupportsVideoGeneration(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        return false;
    }

    public virtual Task<IReadOnlyList<VideoGenerationResult>> GenerateVideosAsync(
        ProviderVideoGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new NotSupportedException($"厂商驱动 '{Definition.DisplayName}' 尚未实现视频生成。");
    }

    public virtual Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RemoteModelDescriptor>>([]);
    }

    protected static IReadOnlyList<string> BuildModelEndpointCandidates(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var normalizedBaseUrl = baseUrl.TrimEnd('/');
        var candidates = new List<string>(2);

        AddModelEndpointCandidate(candidates, normalizedBaseUrl + "/models");

        if (normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var withoutV1BaseUrl = normalizedBaseUrl[..^3].TrimEnd('/');
            AddModelEndpointCandidate(candidates, withoutV1BaseUrl + "/models");
        }
        else
        {
            AddModelEndpointCandidate(candidates, normalizedBaseUrl + "/v1/models");
        }

        return candidates;
    }

    private static void AddModelEndpointCandidate(ICollection<string> candidates, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        if (!candidates.Contains(candidate))
        {
            candidates.Add(candidate);
        }
    }

    protected static ChatOptions CreateCommonOptions(AgentEntity agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

#pragma warning disable MEAI001
        var options = new ChatOptions
        {
            Instructions = agent.Instructions,
            AllowBackgroundResponses = false,
            Tools = []
        };
#pragma warning restore MEAI001

        return options;
    }

    protected static ChatOptions CreateOpenAiCompatibleOptions(AgentEntity agent)
    {
        var options = CreateCommonOptions(agent);
        options.AdditionalProperties = new AdditionalPropertiesDictionary(new Dictionary<string, object?>()
        {
            ["stream_options"] = new { include_usage = true }
        });

        return options;
    }
}
