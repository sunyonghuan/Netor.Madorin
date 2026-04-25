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
            Temperature = (float)agent.Temperature,
            TopP = (float)agent.TopP,
            Instructions = agent.Instructions,
            AllowBackgroundResponses = false,
            Tools = []
        };
#pragma warning restore MEAI001

        if (agent.MaxTokens > 0)
        {
            options.MaxOutputTokens = agent.MaxTokens;
        }

        return options;
    }

    protected static ChatOptions CreateOpenAiCompatibleOptions(AgentEntity agent)
    {
        var options = CreateCommonOptions(agent);
        options.FrequencyPenalty = (float)agent.FrequencyPenalty;
        options.PresencePenalty = (float)agent.PresencePenalty;
        options.AdditionalProperties = new AdditionalPropertiesDictionary(new Dictionary<string, object?>()
        {
            ["stream_options"] = new { include_usage = true }
        });

        return options;
    }
}