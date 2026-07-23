namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>Describes one standalone configuration validation failure.</summary>
public sealed record StandaloneConfigValidationError(
    string FilePath,
    string Field,
    string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{FilePath}: {Field}: {Message}";
}

/// <summary>Validates standalone Provider, model, and Agent references.</summary>
public static class StandaloneConfigValidator
{
    private static readonly HashSet<string> SupportedProtocols = new(StringComparer.Ordinal)
    {
        "OpenAI",
        "OpenAI Compatible",
        "Anthropic",
        "DeepSeek",
        "Kimi"
    };

    /// <summary>Validates a complete standalone configuration snapshot.</summary>
    public static IReadOnlyList<StandaloneConfigValidationError> Validate(
        StandaloneConfig config,
        string configPath,
        IReadOnlyList<AgentConfigDocument>? agentDocuments = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var errors = new List<StandaloneConfigValidationError>();
        var providers = new Dictionary<string, ProviderEntry>(StringComparer.Ordinal);

        Required(errors, configPath, "defaultProvider", config.DefaultProvider);
        Required(errors, configPath, "defaultModel", config.DefaultModel);
        Required(errors, configPath, "defaultAgent", config.DefaultAgent);
        var providerEntries = config.Providers ?? [];
        if (providerEntries.Count == 0)
        {
            Add(errors, configPath, "providers", "At least one Provider is required.");
        }

        for (var index = 0; index < providerEntries.Count; index++)
        {
            var provider = providerEntries[index];
            var prefix = $"providers[{index}]";
            if (provider is null)
            {
                Add(errors, configPath, prefix, "Provider must not be null.");
                continue;
            }

            Required(errors, configPath, $"{prefix}.name", provider.Name);
            Required(errors, configPath, $"{prefix}.protocol", provider.Protocol);
            Required(errors, configPath, $"{prefix}.apiKey", provider.ApiKey);
            if (!string.IsNullOrWhiteSpace(provider.Name)
                && !providers.TryAdd(provider.Name, provider))
            {
                Add(errors, configPath, $"{prefix}.name", $"Duplicate Provider name '{provider.Name}'.");
            }

            if (!SupportedProtocols.Contains(provider.Protocol))
            {
                Add(errors, configPath, $"{prefix}.protocol", $"Unsupported protocol '{provider.Protocol}'.");
            }

            if (!string.IsNullOrWhiteSpace(provider.BaseUrl)
                && (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUri)
                    || baseUri.Scheme is not ("http" or "https")))
            {
                Add(errors, configPath, $"{prefix}.baseUrl", "Base URL must be an absolute HTTP or HTTPS URL.");
            }

            if (provider.Protocol == "OpenAI Compatible" && string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                Add(errors, configPath, $"{prefix}.baseUrl", "Base URL is required for OpenAI Compatible.");
            }

            var models = provider.Models ?? [];
            if (models.Count == 0)
            {
                Add(errors, configPath, $"{prefix}.models", "At least one model is required.");
            }

            var uniqueModels = new HashSet<string>(StringComparer.Ordinal);
            for (var modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                var model = models[modelIndex];
                Required(errors, configPath, $"{prefix}.models[{modelIndex}]", model);
                if (!string.IsNullOrWhiteSpace(model) && !uniqueModels.Add(model))
                {
                    Add(errors, configPath, $"{prefix}.models[{modelIndex}]", $"Duplicate model '{model}'.");
                }
            }

            if (errors.All(error => !error.Field.StartsWith(prefix, StringComparison.Ordinal)))
            {
                try
                {
                    var adapterConfig = new StandaloneConfig { Providers = [provider] };
                    using var adapter = StandaloneProviderFactory.CreateAdapter(
                        adapterConfig,
                        provider.Name) as IDisposable;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
                {
                    Add(
                        errors,
                        configPath,
                        prefix,
                        $"Provider adapter configuration is invalid: {Redact(ex.Message, provider.ApiKey)}");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(config.DefaultProvider)
            || !providers.TryGetValue(config.DefaultProvider, out var defaultProvider))
        {
            Add(errors, configPath, "defaultProvider", "The default Provider does not exist.");
        }
        else if (defaultProvider.Models?.Contains(
                     config.DefaultModel,
                     StringComparer.Ordinal) is not true)
        {
            Add(errors, configPath, "defaultModel", "The default model is not provided by defaultProvider.");
        }

        ValidateAgents(
            config,
            configPath,
            providers,
            agentDocuments ?? [],
            requireDefaultAgent: agentDocuments is not null,
            errors);
        return errors;
    }

    /// <summary>Returns whether an Agent ID is safe for use as a file name.</summary>
    public static bool IsSafeAgentId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or "..")
        {
            return false;
        }

        return id.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static void ValidateAgents(
        StandaloneConfig config,
        string configPath,
        Dictionary<string, ProviderEntry> providers,
        IReadOnlyList<AgentConfigDocument> documents,
        bool requireDefaultAgent,
        List<StandaloneConfigValidationError> errors)
    {
        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var agent = document.Agent;
            if (!IsSafeAgentId(agent.Id))
            {
                Add(errors, document.FilePath, "id", "Agent ID contains unsafe file name characters.");
            }
            else if (!agentIds.Add(agent.Id))
            {
                Add(errors, document.FilePath, "id", $"Duplicate Agent ID '{agent.Id}'.");
            }

            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(document.FilePath),
                    agent.Id,
                    StringComparison.Ordinal))
            {
                Add(errors, document.FilePath, "id", "Agent ID must match the JSON file name.");
            }

            Required(errors, document.FilePath, "name", agent.Name);
            if (agent.Temperature is < 0 or > 2)
            {
                Add(errors, document.FilePath, "temperature", "Temperature must be between 0 and 2.");
            }

            var providerName = agent.Provider ?? config.DefaultProvider;
            var model = agent.Model ?? config.DefaultModel;
            if (!providers.TryGetValue(providerName, out var provider))
            {
                Add(errors, document.FilePath, "provider", $"Provider '{providerName}' does not exist.");
            }
            else if (provider.Models?.Contains(model, StringComparer.Ordinal) is not true)
            {
                Add(errors, document.FilePath, "model", $"Model '{model}' is not provided by '{providerName}'.");
            }
        }

        if (requireDefaultAgent
            && (string.IsNullOrWhiteSpace(config.DefaultAgent)
                || !agentIds.Contains(config.DefaultAgent)))
        {
            Add(errors, configPath, "defaultAgent", "The default Agent does not exist.");
        }
    }

    private static void Required(
        List<StandaloneConfigValidationError> errors,
        string path,
        string field,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, path, field, "A value is required.");
        }
    }

    private static void Add(
        List<StandaloneConfigValidationError> errors,
        string path,
        string field,
        string message) =>
        errors.Add(new StandaloneConfigValidationError(path, field, message));

    private static string Redact(string message, string apiKey) =>
        string.IsNullOrEmpty(apiKey)
            ? message
            : message.Replace(apiKey, "***", StringComparison.Ordinal);
}

/// <summary>Associates a parsed Agent with its source file.</summary>
public sealed record AgentConfigDocument(string FilePath, AgentConfig Agent);
