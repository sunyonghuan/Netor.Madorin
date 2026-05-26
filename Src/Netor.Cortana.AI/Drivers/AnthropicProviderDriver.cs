using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Models;

using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// Anthropic 驱动。
/// </summary>
public sealed class AnthropicProviderDriver : AiProviderDriverBase
{
    public override AiProviderDriverDefinition Definition { get; } =
        new("Anthropic", "Anthropic", true);

    public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return CreateClient(provider).AsIChatClient(model.Name);
    }

    public override ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent)
    {
        return CreateCommonOptions(agent);
    }

    public override async Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var client = CreateClient(provider);
        var response = await client.Models.List(new ModelListParams(), cancellationToken);
        var models = new List<RemoteModelDescriptor>();

        foreach (var model in response.Items)
        {
            if (string.IsNullOrWhiteSpace(model.ID))
            {
                continue;
            }

            models.Add(new RemoteModelDescriptor(
                model.ID,
                model.DisplayName,
                model.Type.GetString(),
                "chat",
                null));
        }

        return models;
    }

    private static AnthropicClient CreateClient(AiProviderEntity provider)
    {
        var options = new ClientOptions
        {
            ApiKey = provider.Key,
            Timeout = TimeSpan.FromMinutes(10)
        };

        if (!string.IsNullOrWhiteSpace(provider.Url))
        {
            options.BaseUrl = provider.Url.TrimEnd('/');
        }

        return new AnthropicClient(options);
    }
}