namespace Madorin.AI.Runtime.Cli.Config;

public static class ConfigWizard
{
    public static async Task<StandaloneConfig> RunInitAsync(
        string configDirectory,
        ICliTerminal terminal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var loader = new StandaloneConfigLoader(configDirectory);
        var existing = await loader.LoadAsync(ct).ConfigureAwait(false);
        if (existing is not null
            && !await ConfirmAsync(
                    terminal,
                    $"Configuration already exists at '{loader.ConfigPath}'. Rebuild it? [y/N]: ",
                    defaultValue: false,
                    ct)
                .ConfigureAwait(false))
        {
            return existing;
        }

        terminal.WriteLine("Madorin standalone configuration");
        var config = await ReadConfigAsync(terminal, loader.ConfigPath, existing, ct)
            .ConfigureAwait(false);
        var existingAgents = await loader.LoadAgentDocumentsAsync(ct).ConfigureAwait(false);
        var existingDefaultAgent = existingAgents
            .FirstOrDefault(document =>
                document.Agent.Id.Equals(config.DefaultAgent, StringComparison.Ordinal))
            ?.Agent;
        var systemPrompt = await ReadRawValueAsync(
                terminal,
                "System prompt",
                existingDefaultAgent?.SystemPrompt ?? "You are a helpful AI assistant.",
                ct)
            .ConfigureAwait(false);

        var defaultAgent = new AgentConfig
        {
            Id = config.DefaultAgent,
            Name = existingDefaultAgent?.Name ?? "Default",
            Description = existingDefaultAgent?.Description ?? "Default standalone agent.",
            SystemPrompt = systemPrompt,
            Provider = config.DefaultProvider,
            Model = config.DefaultModel,
            Temperature = existingDefaultAgent?.Temperature ?? 0.7f
        };
        var prospectiveAgents = existingAgents
            .Where(document => !document.Agent.Id.Equals(config.DefaultAgent, StringComparison.Ordinal))
            .Append(new AgentConfigDocument(
                Path.Combine(loader.AgentsDirectory, $"{config.DefaultAgent}.json"),
                defaultAgent))
            .ToList();
        var errors = StandaloneConfigValidator.Validate(
            config,
            loader.ConfigPath,
            prospectiveAgents);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        if (existing is null)
        {
            config = await loader.UpdateConfigAsync(current => current ?? config, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var backupPath = await loader.ReplaceConfigWithBackupAsync(config, ct)
                .ConfigureAwait(false);
            terminal.WriteLine($"Backup created: {backupPath}");
        }

        await loader.UpdateAgentAsync(
                config.DefaultAgent,
                current => current is null
                    ? defaultAgent
                    : new AgentConfig
                    {
                        Id = config.DefaultAgent,
                        Name = current.Name,
                        Description = current.Description,
                        SystemPrompt = defaultAgent.SystemPrompt,
                        Provider = defaultAgent.Provider,
                        Model = defaultAgent.Model,
                        Temperature = current.Temperature
                    },
                ct)
            .ConfigureAwait(false);

        return config;
    }

    public static async Task<StandaloneConfig> RunEditAsync(
        string configDirectory,
        ICliTerminal terminal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var loader = new StandaloneConfigLoader(configDirectory);
        var existing = await loader.LoadAsync(ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException(
                "The standalone configuration does not exist.",
                loader.ConfigPath);
        var agents = await loader.LoadAgentDocumentsAsync(ct).ConfigureAwait(false);
        var target = await ReadEditTargetAsync(terminal, ct).ConfigureAwait(false);
        return target switch
        {
            ConfigEditTarget.Provider => await EditProviderAsync(
                    loader,
                    existing,
                    agents,
                    terminal,
                    ct)
                .ConfigureAwait(false),
            _ => await EditDefaultsAsync(
                    loader,
                    existing,
                    agents,
                    terminal,
                    ct)
                .ConfigureAwait(false)
        };
    }

    private static async Task<StandaloneConfig> ReadConfigAsync(
        ICliTerminal terminal,
        string configPath,
        StandaloneConfig? existing,
        CancellationToken ct)
    {
        var existingProvider = existing?.Providers.FirstOrDefault(provider =>
            provider.Name.Equals(existing.DefaultProvider, StringComparison.Ordinal));
        var protocol = await ReadProtocolAsync(
                terminal,
                existingProvider?.Protocol ?? "OpenAI",
                ct)
            .ConfigureAwait(false);
        var providerName = await ReadRequiredValueAsync(
                terminal,
                "Provider name",
                existingProvider?.Name ?? "openai",
                ct)
            .ConfigureAwait(false);
        var baseUrl = await ReadValueAsync(
                terminal,
                "Base URL",
                existingProvider?.BaseUrl ?? GetDefaultBaseUrl(protocol),
                ct)
            .ConfigureAwait(false);

        var apiKey = await terminal.ReadSecretAsync(
                existingProvider is null
                    ? "API key: "
                    : $"API key [{StandaloneConfigLoader.MaskApiKey(existingProvider.ApiKey)}; Enter to keep]: ",
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = existingProvider?.ApiKey ?? string.Empty;
        }

        while (string.IsNullOrWhiteSpace(apiKey))
        {
            terminal.WriteLine("API key is required.");
            apiKey = await terminal.ReadSecretAsync("API key: ", ct).ConfigureAwait(false);
        }

        var models = await ReadModelsAsync(
                terminal,
                existingProvider?.Models ?? [GetDefaultModel(protocol)],
                ct)
            .ConfigureAwait(false);
        var defaultModel = await ReadDefaultModelAsync(
                terminal,
                models,
                existing is not null && models.Contains(existing.DefaultModel, StringComparer.Ordinal)
                    ? existing.DefaultModel
                    : models[0],
                ct)
            .ConfigureAwait(false);

        var provider = new ProviderEntry
        {
            Name = providerName,
            Protocol = protocol,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            AuthenticationHeader = existingProvider?.AuthenticationHeader ?? "Authorization",
            AuthenticationScheme = existingProvider?.AuthenticationScheme ?? "Bearer",
            Capabilities = existingProvider?.Capabilities,
            ConfigurationVersion = existingProvider?.ConfigurationVersion ?? "1",
            ProbePath = existingProvider?.ProbePath ?? "models",
            Models = models
        };
        var providers = existing?.Providers.ToList() ?? [];
        if (existingProvider is null)
        {
            providers.Add(provider);
        }
        else
        {
            providers[providers.IndexOf(existingProvider)] = provider;
        }

        var result = new StandaloneConfig
        {
            DefaultProvider = providerName,
            DefaultModel = defaultModel,
            DefaultAgent = existing?.DefaultAgent ?? "default",
            Providers = providers
        };
        var errors = StandaloneConfigValidator.Validate(result, configPath);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return result;
    }

    private static async Task<StandaloneConfig> EditProviderAsync(
        StandaloneConfigLoader loader,
        StandaloneConfig existing,
        IReadOnlyList<AgentConfigDocument> agents,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        var selection = await ReadProviderSelectionAsync(existing, terminal, ct)
            .ConfigureAwait(false);
        var editedProvider = await ReadProviderAsync(selection.Provider, terminal, ct)
            .ConfigureAwait(false);

        return await loader.UpdateConfigAsync(latest =>
        {
            if (latest is null)
            {
                throw new FileNotFoundException(
                    "The standalone configuration no longer exists.",
                    loader.ConfigPath);
            }

            var providers = latest.Providers.ToList();
            if (selection.OriginalName is null)
            {
                if (providers.Any(provider =>
                        provider.Name.Equals(editedProvider.Name, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Provider '{editedProvider.Name}' already exists.");
                }

                providers.Add(editedProvider);
            }
            else
            {
                var index = providers.FindIndex(provider =>
                    provider.Name.Equals(selection.OriginalName, StringComparison.Ordinal));
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"Provider '{selection.OriginalName}' no longer exists; configuration was not overwritten.");
                }

                if (!editedProvider.Name.Equals(selection.OriginalName, StringComparison.Ordinal)
                    && providers.Any(provider =>
                        provider.Name.Equals(editedProvider.Name, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Provider '{editedProvider.Name}' already exists.");
                }

                providers[index] = editedProvider;
            }

            var defaultProvider = latest.DefaultProvider;
            var defaultModel = latest.DefaultModel;
            if (selection.OriginalName is not null
                && latest.DefaultProvider.Equals(selection.OriginalName, StringComparison.Ordinal))
            {
                defaultProvider = editedProvider.Name;
                if (!editedProvider.Models.Contains(defaultModel, StringComparer.Ordinal))
                {
                    defaultModel = editedProvider.Models[0];
                }
            }

            var updated = new StandaloneConfig
            {
                DefaultProvider = defaultProvider,
                DefaultModel = defaultModel,
                DefaultAgent = latest.DefaultAgent,
                Providers = providers
            };
            ValidateOrThrow(updated, loader.ConfigPath, agents);
            return updated;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<StandaloneConfig> EditDefaultsAsync(
        StandaloneConfigLoader loader,
        StandaloneConfig existing,
        IReadOnlyList<AgentConfigDocument> agents,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        var defaultProvider = await ReadDefaultProviderAsync(existing, terminal, ct)
            .ConfigureAwait(false);
        var provider = existing.Providers.First(candidate =>
            candidate.Name.Equals(defaultProvider, StringComparison.Ordinal));
        var currentModel = provider.Models.Contains(existing.DefaultModel, StringComparer.Ordinal)
            ? existing.DefaultModel
            : provider.Models[0];
        var defaultModel = await ReadDefaultModelAsync(
                terminal,
                provider.Models,
                currentModel,
                ct)
            .ConfigureAwait(false);
        var defaultAgent = await ReadDefaultAgentAsync(
                agents,
                existing.DefaultAgent,
                terminal,
                ct)
            .ConfigureAwait(false);

        return await loader.UpdateConfigAsync(latest =>
        {
            if (latest is null)
            {
                throw new FileNotFoundException(
                    "The standalone configuration no longer exists.",
                    loader.ConfigPath);
            }

            var latestProvider = latest.Providers.FirstOrDefault(candidate =>
                candidate.Name.Equals(defaultProvider, StringComparison.Ordinal));
            if (latestProvider is null
                || !latestProvider.Models.Contains(defaultModel, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The selected default Provider or model changed before the configuration lock was acquired.");
            }

            var updated = new StandaloneConfig
            {
                DefaultProvider = defaultProvider,
                DefaultModel = defaultModel,
                DefaultAgent = defaultAgent,
                Providers = latest.Providers.ToList()
            };
            ValidateOrThrow(updated, loader.ConfigPath, agents);
            return updated;
        }, ct).ConfigureAwait(false);
    }

    private static async Task<ConfigEditTarget> ReadEditTargetAsync(
        ICliTerminal terminal,
        CancellationToken ct)
    {
        terminal.WriteLine("Edit target:");
        terminal.WriteLine("1. Provider");
        terminal.WriteLine("2. Common defaults");
        while (true)
        {
            var input = await ReadValueAsync(terminal, "Edit target", "1", ct)
                .ConfigureAwait(false);
            if (input.Equals("1", StringComparison.Ordinal)
                || input.Equals("provider", StringComparison.OrdinalIgnoreCase))
            {
                return ConfigEditTarget.Provider;
            }

            if (input.Equals("2", StringComparison.Ordinal)
                || input.Equals("defaults", StringComparison.OrdinalIgnoreCase)
                || input.Equals("common defaults", StringComparison.OrdinalIgnoreCase))
            {
                return ConfigEditTarget.Defaults;
            }

            terminal.WriteLine("Choose 1 for Provider or 2 for common defaults.");
        }
    }

    private static async Task<ProviderEditSelection> ReadProviderSelectionAsync(
        StandaloneConfig config,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        terminal.WriteLine("Providers:");
        for (var index = 0; index < config.Providers.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {config.Providers[index].Name}");
        }

        terminal.WriteLine($"{config.Providers.Count + 1}. Add Provider");
        while (true)
        {
            var input = await ReadValueAsync(terminal, "Provider", "1", ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex))
            {
                if (selectedIndex >= 1 && selectedIndex <= config.Providers.Count)
                {
                    var provider = config.Providers[selectedIndex - 1];
                    return new ProviderEditSelection(provider.Name, provider);
                }

                if (selectedIndex == config.Providers.Count + 1)
                {
                    return new ProviderEditSelection(null, null);
                }
            }

            var namedProvider = config.Providers.FirstOrDefault(provider =>
                provider.Name.Equals(input, StringComparison.Ordinal));
            if (namedProvider is not null)
            {
                return new ProviderEditSelection(namedProvider.Name, namedProvider);
            }

            if (input.Equals("new", StringComparison.OrdinalIgnoreCase)
                || input.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                return new ProviderEditSelection(null, null);
            }

            terminal.WriteLine("Choose a Provider number, exact name, or 'new'.");
        }
    }

    private static async Task<ProviderEntry> ReadProviderAsync(
        ProviderEntry? existing,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        var protocol = await ReadProtocolAsync(
                terminal,
                existing?.Protocol ?? "OpenAI",
                ct)
            .ConfigureAwait(false);
        var providerName = await ReadRequiredValueAsync(
                terminal,
                "Provider name",
                existing?.Name ?? "provider",
                ct)
            .ConfigureAwait(false);
        var baseUrl = await ReadValueAsync(
                terminal,
                "Base URL",
                existing?.BaseUrl ?? GetDefaultBaseUrl(protocol),
                ct)
            .ConfigureAwait(false);
        var apiKey = await terminal.ReadSecretAsync(
                existing is null
                    ? "API key: "
                    : $"API key [{StandaloneConfigLoader.MaskApiKey(existing.ApiKey)}; Enter to keep]: ",
                ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = existing?.ApiKey ?? string.Empty;
        }

        while (string.IsNullOrWhiteSpace(apiKey))
        {
            terminal.WriteLine("API key is required.");
            apiKey = await terminal.ReadSecretAsync("API key: ", ct).ConfigureAwait(false);
        }

        var models = await ReadModelsAsync(
                terminal,
                existing?.Models ?? [GetDefaultModel(protocol)],
                ct)
            .ConfigureAwait(false);
        return new ProviderEntry
        {
            Name = providerName,
            Protocol = protocol,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            AuthenticationHeader = existing?.AuthenticationHeader ?? "Authorization",
            AuthenticationScheme = existing?.AuthenticationScheme ?? "Bearer",
            Capabilities = existing?.Capabilities,
            ConfigurationVersion = existing?.ConfigurationVersion ?? "1",
            ProbePath = existing?.ProbePath ?? "models",
            Models = models
        };
    }

    private static async Task<string> ReadDefaultProviderAsync(
        StandaloneConfig config,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        terminal.WriteLine("Available Providers:");
        for (var index = 0; index < config.Providers.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {config.Providers[index].Name}");
        }

        while (true)
        {
            var input = await ReadValueAsync(
                    terminal,
                    "Default Provider",
                    config.DefaultProvider,
                    ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex)
                && selectedIndex >= 1
                && selectedIndex <= config.Providers.Count)
            {
                return config.Providers[selectedIndex - 1].Name;
            }

            if (config.Providers.Any(provider =>
                    provider.Name.Equals(input, StringComparison.Ordinal)))
            {
                return input;
            }

            terminal.WriteLine("Choose a Provider number or exact name.");
        }
    }

    private static async Task<string> ReadDefaultAgentAsync(
        IReadOnlyList<AgentConfigDocument> agents,
        string currentAgent,
        ICliTerminal terminal,
        CancellationToken ct)
    {
        if (agents.Count == 0)
        {
            throw new InvalidOperationException("At least one Agent is required before editing defaults.");
        }

        terminal.WriteLine("Available Agents:");
        for (var index = 0; index < agents.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {agents[index].Agent.Id}");
        }

        while (true)
        {
            var input = await ReadValueAsync(terminal, "Default Agent", currentAgent, ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex)
                && selectedIndex >= 1
                && selectedIndex <= agents.Count)
            {
                return agents[selectedIndex - 1].Agent.Id;
            }

            if (agents.Any(document =>
                    document.Agent.Id.Equals(input, StringComparison.Ordinal)))
            {
                return input;
            }

            terminal.WriteLine("Choose an Agent number or exact id.");
        }
    }

    private static void ValidateOrThrow(
        StandaloneConfig config,
        string configPath,
        IReadOnlyList<AgentConfigDocument> agents)
    {
        var errors = StandaloneConfigValidator.Validate(config, configPath, agents);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private static async Task<string> ReadProtocolAsync(
        ICliTerminal terminal,
        string defaultValue,
        CancellationToken ct)
    {
        while (true)
        {
            terminal.Write(
                $"Protocol (1=OpenAI, 2=Anthropic, 3=OpenAI Compatible, 4=DeepSeek, 5=Kimi) [{defaultValue}]: ");
            var input = (await terminal.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                return defaultValue;
            }

            switch (input.ToUpperInvariant())
            {
                case "1":
                case "OPENAI":
                    return "OpenAI";
                case "2":
                case "ANTHROPIC":
                    return "Anthropic";
                case "3":
                case "OPENAI COMPATIBLE":
                    return "OpenAI Compatible";
                case "4":
                case "DEEPSEEK":
                    return "DeepSeek";
                case "5":
                case "KIMI":
                    return "Kimi";
                default:
                    terminal.WriteLine("Choose 1 through 5, or enter a protocol name.");
                    break;
            }
        }
    }

    private static async Task<List<string>> ReadModelsAsync(
        ICliTerminal terminal,
        IReadOnlyList<string> defaultValues,
        CancellationToken ct)
    {
        while (true)
        {
            var input = await ReadValueAsync(
                    terminal,
                    "Models (comma-separated)",
                    string.Join(", ", defaultValues),
                    ct)
                .ConfigureAwait(false);
            var models = input
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (models.Count > 0)
            {
                return models;
            }

            terminal.WriteLine("At least one model is required.");
        }
    }

    private static async Task<string> ReadDefaultModelAsync(
        ICliTerminal terminal,
        List<string> models,
        string defaultValue,
        CancellationToken ct)
    {
        terminal.WriteLine("Available models:");
        for (var index = 0; index < models.Count; index++)
        {
            terminal.WriteLine($"{index + 1}. {models[index]}");
        }

        while (true)
        {
            var input = await ReadValueAsync(terminal, "Default model", defaultValue, ct)
                .ConfigureAwait(false);
            if (int.TryParse(input, out var selectedIndex)
                && selectedIndex >= 1
                && selectedIndex <= models.Count)
            {
                return models[selectedIndex - 1];
            }

            if (models.Contains(input, StringComparer.Ordinal))
            {
                return input;
            }

            terminal.WriteLine("Choose a model number or exact model name.");
        }
    }

    private static async Task<string> ReadRequiredValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue,
        CancellationToken ct)
    {
        while (true)
        {
            var value = await ReadValueAsync(terminal, prompt, defaultValue, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            terminal.WriteLine($"{prompt} is required.");
        }
    }

    private static async Task<string> ReadValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue,
        CancellationToken ct)
    {
        var defaultPrompt = string.IsNullOrEmpty(defaultValue) ? string.Empty : $" [{defaultValue}]";
        terminal.Write($"{prompt}{defaultPrompt}: ");
        var input = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
    }

    private static async Task<string> ReadRawValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue,
        CancellationToken ct)
    {
        terminal.Write($"{prompt} [Enter to keep current]: ");
        var input = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(input) ? defaultValue : input;
    }

    private static async Task<bool> ConfirmAsync(
        ICliTerminal terminal,
        string prompt,
        bool defaultValue,
        CancellationToken ct)
    {
        terminal.Write(prompt);
        var input = (await terminal.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return defaultValue;
        }

        return input.Equals("y", StringComparison.OrdinalIgnoreCase)
            || input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultBaseUrl(string protocol) => protocol switch
    {
        "OpenAI" => "https://api.openai.com/v1",
        "Anthropic" => "https://api.anthropic.com",
        "DeepSeek" => "https://api.deepseek.com/v1",
        "Kimi" => "https://api.moonshot.cn/v1",
        _ => string.Empty
    };

    private static string GetDefaultModel(string protocol) => protocol switch
    {
        "Anthropic" => "claude-opus-4-1",
        "DeepSeek" => "deepseek-chat",
        "Kimi" => "moonshot-v1-8k",
        _ => "gpt-5"
    };

    private enum ConfigEditTarget
    {
        Provider,
        Defaults
    }

    private sealed record ProviderEditSelection(
        string? OriginalName,
        ProviderEntry? Provider);
}
