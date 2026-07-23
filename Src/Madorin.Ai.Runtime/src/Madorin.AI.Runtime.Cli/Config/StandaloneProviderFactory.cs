using System.Collections.Concurrent;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic;
using Madorin.AI.Runtime.Providers.DeepSeek;
using Madorin.AI.Runtime.Providers.Kimi;
using Madorin.AI.Runtime.Providers.OpenAI;
using Madorin.AI.Runtime.Providers.OpenAICompatible;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>
/// Builds a <see cref="Func{NextTurnSelection, IRuntimeProviderAdapter}"/> resolver
/// from the standalone config file so the Runtime server can dispatch to
/// the correct provider without requiring a DI container.
/// </summary>
internal static class StandaloneProviderFactory
{
    public static Func<NextTurnSelection, IRuntimeProviderAdapter> Create(StandaloneConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var cache = new ConcurrentDictionary<string, IRuntimeProviderAdapter>(StringComparer.Ordinal);
        return selection =>
        {
            var providerId = selection.DefaultSelection.ProviderId;
            return cache.GetOrAdd(providerId, id => BuildAdapter(config, id));
        };
    }

    public static IRuntimeProviderAdapter CreateAdapter(
        StandaloneConfig config,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return BuildAdapter(config, providerId);
    }

    private static IRuntimeProviderAdapter BuildAdapter(StandaloneConfig config, string providerId)
    {
        var entry = config.Providers.FirstOrDefault(p =>
            p.Name.Equals(providerId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Provider '{providerId}' is not configured in ~/.madorin/config.json.");

        return entry.Protocol switch
        {
            "OpenAI" => BuildOpenAI(entry),
            "Anthropic" => BuildAnthropic(entry),
            "OpenAI Compatible" => BuildCompatible(entry),
            "DeepSeek" => BuildDeepSeek(entry),
            "Kimi" => BuildKimi(entry),
            _ => throw new InvalidOperationException(
                $"Provider '{providerId}' uses unsupported protocol '{entry.Protocol}'.")
        };
    }

    private static OpenAIProviderAdapter BuildOpenAI(ProviderEntry entry)
    {
        var baseUrl = string.IsNullOrWhiteSpace(entry.BaseUrl)
            ? "https://api.openai.com/v1"
            : entry.BaseUrl;
        return new OpenAIProviderAdapter(
            entry.Name,
            entry.ApiKey,
            baseUrl,
            ToProviderModels(entry.Models));
    }

    private static AnthropicProviderAdapter BuildAnthropic(ProviderEntry entry)
    {
        var baseUrl = string.IsNullOrWhiteSpace(entry.BaseUrl)
            ? "https://api.anthropic.com"
            : entry.BaseUrl;
        return new AnthropicProviderAdapter(
            entry.Name,
            entry.ApiKey,
            baseUrl,
            ToProviderModels(entry.Models));
    }

    private static DeepSeekProviderAdapter BuildDeepSeek(ProviderEntry entry)
    {
        var baseUrl = string.IsNullOrWhiteSpace(entry.BaseUrl)
            ? "https://api.deepseek.com/v1"
            : entry.BaseUrl;
        var models = ToProviderModels(entry.Models);
        return new DeepSeekProviderAdapter(baseUrl, entry.ApiKey, models: models);
    }

    private static KimiProviderAdapter BuildKimi(ProviderEntry entry)
    {
        var models = ToProviderModels(entry.Models);
        return new KimiProviderAdapter(
            entry.ApiKey,
            baseUrl: string.IsNullOrWhiteSpace(entry.BaseUrl) ? null : entry.BaseUrl,
            models: models);
    }

    private static List<ProviderModel> ToProviderModels(IEnumerable<string> ids) =>
        ids.Select(id => new ProviderModel(id, id)).ToList();

    private static OpenAICompatibleProviderAdapter BuildCompatible(ProviderEntry entry)
    {
        var baseUrl = string.IsNullOrWhiteSpace(entry.BaseUrl)
            ? "https://api.openai.com/v1"
            : entry.BaseUrl;
        var options = new OpenAICompatibleProviderOptions(
            entry.Name,
            baseUrl,
            entry.ApiKey,
            entry.AuthenticationHeader,
            entry.AuthenticationScheme,
            entry.Capabilities,
            ToProviderModels(entry.Models),
            entry.ConfigurationVersion,
            entry.ProbePath);
        return new OpenAICompatibleProviderAdapter(
            options,
            ProviderHttpClientFactory.Shared.CreateClient(entry.Name),
            disposeHttpClient: true);
    }
}
