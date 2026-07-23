using System.CommandLine;
using System.Globalization;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class AgentCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory = null)
    {
        var command = new Command("agent", "Manage standalone Agent definitions.");
        command.Subcommands.Add(CreateList(jsonOption, output, configDirectory));
        command.Subcommands.Add(CreateCreate(jsonOption, terminal, output, configDirectory));
        command.Subcommands.Add(CreateEdit(jsonOption, terminal, output, configDirectory));
        command.Subcommands.Add(CreateDelete(jsonOption, terminal, output, configDirectory));
        return command;
    }

    private static Command CreateList(
        Option<bool> jsonOption,
        TextWriter output,
        string? configDirectory)
    {
        var command = new Command("list", "List configured Agents.");
        command.SetAction(async parseResult =>
        {
            var agents = await CreateLoader(configDirectory).LoadAgentsAsync().ConfigureAwait(false);
            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("command", "agent list");
                    writer.WriteStartArray("agents");
                    foreach (var agent in agents)
                    {
                        WriteAgent(writer, agent);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else if (agents.Count == 0)
            {
                output.WriteLine("No Agents configured.");
            }
            else
            {
                foreach (var agent in agents)
                {
                    output.WriteLine(
                        $"{agent.Id}\t{agent.Name}\t{agent.Provider ?? "(default)"}/{agent.Model ?? "(default)"}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateCreate(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory)
    {
        var options = CreateAgentOptions();
        var agentIdArgument = new Argument<string?>("agentId")
        {
            Description = "Agent identifier.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var command = new Command("create", "Create an Agent definition.")
            .AddOptions(options.All);
        command.Arguments.Add(agentIdArgument);
        command.SetAction(async parseResult =>
        {
            var loader = CreateLoader(configDirectory);
            try
            {
                var config = await RequireConfigAsync(loader).ConfigureAwait(false);
                var agentId = parseResult.GetValue(agentIdArgument);
                if (string.IsNullOrWhiteSpace(agentId))
                {
                    if (CliOutput.IsJson(parseResult, jsonOption))
                    {
                        throw new ArgumentException("Agent id is required with --json.");
                    }

                    agentId = await ReadRequiredValueAsync(terminal, "Agent id", string.Empty)
                        .ConfigureAwait(false);
                }

                if (!StandaloneConfigValidator.IsSafeAgentId(agentId))
                {
                    throw new ArgumentException("Agent id contains unsafe file name characters.");
                }

                var agent = await ReadAgentAsync(
                        terminal,
                        parseResult,
                        jsonOption,
                        options,
                        config,
                        agentId,
                        current: null)
                    .ConfigureAwait(false);
                var documents = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
                ValidateAgent(loader, config, documents, agent);
                agent = await loader.UpdateAgentAsync(
                        agentId,
                        current => current is null
                            ? agent
                            : throw new InvalidOperationException($"Agent '{agentId}' already exists."))
                    .ConfigureAwait(false);
                WriteSuccess(parseResult, jsonOption, output, "agent create", "Created", agent);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (IsAgentCommandException(ex))
            {
                WriteError(parseResult, jsonOption, output, "agent create", ex.Message);
                return ExitCodes.InvalidArguments;
            }
        });
        return command;
    }

    private static Command CreateEdit(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory)
    {
        var options = CreateAgentOptions();
        var agentIdArgument = new Argument<string>("agentId")
        {
            Description = "Agent identifier."
        };
        var command = new Command("edit", "Edit an Agent definition.")
            .AddOptions(options.All);
        command.Arguments.Add(agentIdArgument);
        command.SetAction(async parseResult =>
        {
            var loader = CreateLoader(configDirectory);
            var agentId = parseResult.GetValue(agentIdArgument) ?? string.Empty;
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
                var config = await RequireConfigAsync(loader).ConfigureAwait(false);
                var documents = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
                var current = documents
                    .FirstOrDefault(document =>
                        document.Agent.Id.Equals(agentId, StringComparison.Ordinal))
                    ?.Agent
                    ?? throw new FileNotFoundException($"Agent '{agentId}' was not found.");
                var edited = await ReadAgentAsync(
                        terminal,
                        parseResult,
                        jsonOption,
                        options,
                        config,
                        agentId,
                        current)
                    .ConfigureAwait(false);
                ValidateAgent(loader, config, documents, edited);
                edited = await loader.UpdateAgentAsync(
                        agentId,
                        latest => latest is null
                            ? throw new FileNotFoundException($"Agent '{agentId}' was not found.")
                            : edited)
                    .ConfigureAwait(false);
                WriteSuccess(parseResult, jsonOption, output, "agent edit", "Updated", edited);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (IsAgentCommandException(ex))
            {
                WriteError(parseResult, jsonOption, output, "agent edit", ex.Message);
                return ExitCodes.InvalidArguments;
            }
        });
        return command;
    }

    private static Command CreateDelete(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory)
    {
        var agentIdArgument = new Argument<string>("agentId")
        {
            Description = "Agent identifier."
        };
        var replacementOption = CommandOptions.Create<string?>(
            "--replacement",
            "Select the replacement default Agent when deleting the current default.");
        var yesOption = CommandOptions.Create<bool>("--yes", "Confirm deletion without prompting.");
        var command = new Command("delete", "Delete an Agent definition.")
            .AddOptions(replacementOption, yesOption);
        command.Arguments.Add(agentIdArgument);
        command.SetAction(async parseResult =>
        {
            var loader = CreateLoader(configDirectory);
            var agentId = parseResult.GetValue(agentIdArgument) ?? string.Empty;
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
                var config = await RequireConfigAsync(loader).ConfigureAwait(false);
                var documents = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
                if (!documents.Any(document =>
                        document.Agent.Id.Equals(agentId, StringComparison.Ordinal)))
                {
                    throw new FileNotFoundException($"Agent '{agentId}' was not found.");
                }

                var replacement = parseResult.GetValue(replacementOption);
                if (config.DefaultAgent.Equals(agentId, StringComparison.Ordinal))
                {
                    var alternatives = documents
                        .Select(static document => document.Agent.Id)
                        .Where(id => !id.Equals(agentId, StringComparison.Ordinal))
                        .ToList();
                    if (alternatives.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "The only configured Agent cannot be deleted while it is the default Agent.");
                    }

                    if (string.IsNullOrWhiteSpace(replacement))
                    {
                        if (CliOutput.IsJson(parseResult, jsonOption))
                        {
                            throw new ArgumentException(
                                "--replacement is required when deleting the default Agent with --json.");
                        }

                        terminal.WriteLine($"Available replacements: {string.Join(", ", alternatives)}");
                        replacement = await ReadRequiredValueAsync(
                                terminal,
                                "Replacement default Agent",
                                alternatives[0])
                            .ConfigureAwait(false);
                    }

                    if (!alternatives.Contains(replacement, StringComparer.Ordinal))
                    {
                        throw new ArgumentException(
                            $"Replacement Agent '{replacement}' is not an available Agent.");
                    }
                }

                if (!parseResult.GetValue(yesOption))
                {
                    if (CliOutput.IsJson(parseResult, jsonOption))
                    {
                        throw new ArgumentException("--yes is required for agent delete with --json.");
                    }

                    var confirmed = await ConfirmAsync(
                            terminal,
                            $"Delete Agent '{agentId}'? [y/N]: ")
                        .ConfigureAwait(false);
                    if (!confirmed)
                    {
                        output.WriteLine("Agent deletion cancelled.");
                        return ExitCodes.Success;
                    }
                }

                await loader.DeleteAgentAsync(agentId, replacement).ConfigureAwait(false);
                WriteDeleteSuccess(parseResult, jsonOption, output, agentId, replacement);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (IsAgentCommandException(ex))
            {
                WriteError(parseResult, jsonOption, output, "agent delete", ex.Message);
                return ExitCodes.InvalidArguments;
            }
        });
        return command;
    }

    private static AgentCommandOptions CreateAgentOptions()
    {
        var name = CommandOptions.Create<string?>("--name", "Set the Agent display name.");
        var description = CommandOptions.Create<string?>("--description", "Set the Agent description.");
        var systemPrompt = CommandOptions.Create<string?>("--system-prompt", "Set the Agent system prompt.");
        var provider = CommandOptions.Create<string?>("--provider", "Select a Provider.");
        var model = CommandOptions.Create<string?>("--model", "Select a model.");
        var temperature = CommandOptions.Create<float?>("--temperature", "Set temperature from 0 through 2.");
        return new AgentCommandOptions(name, description, systemPrompt, provider, model, temperature);
    }

    private static async Task<AgentConfig> ReadAgentAsync(
        ICliTerminal terminal,
        ParseResult parseResult,
        Option<bool> jsonOption,
        AgentCommandOptions options,
        StandaloneConfig config,
        string agentId,
        AgentConfig? current)
    {
        var isInteractive = !CliOutput.IsJson(parseResult, jsonOption);
        var name = parseResult.GetValue(options.Name);
        if (name is null && isInteractive)
        {
            name = await ReadRequiredValueAsync(terminal, "Name", current?.Name ?? agentId)
                .ConfigureAwait(false);
        }

        var description = parseResult.GetValue(options.Description);
        if (description is null && isInteractive)
        {
            description = await ReadRawValueAsync(
                    terminal,
                    "Description",
                    current?.Description ?? string.Empty)
                .ConfigureAwait(false);
        }

        var systemPrompt = parseResult.GetValue(options.SystemPrompt);
        if (systemPrompt is null && isInteractive)
        {
            systemPrompt = await ReadRawValueAsync(
                    terminal,
                    "System prompt",
                    current?.SystemPrompt ?? "You are a helpful AI assistant.")
                .ConfigureAwait(false);
        }

        var provider = parseResult.GetValue(options.Provider);
        if (provider is null && isInteractive)
        {
            terminal.WriteLine(
                $"Available Providers: {string.Join(", ", config.Providers.Select(static item => item.Name))}");
            provider = await ReadRequiredValueAsync(
                    terminal,
                    "Provider",
                    current?.Provider ?? config.DefaultProvider)
                .ConfigureAwait(false);
        }

        provider ??= current?.Provider ?? config.DefaultProvider;
        var providerEntry = config.Providers.FirstOrDefault(item =>
            item.Name.Equals(provider, StringComparison.Ordinal));
        var modelDefault = current?.Model;
        if (providerEntry is not null
            && (modelDefault is null
                || !providerEntry.Models.Contains(modelDefault, StringComparer.Ordinal)))
        {
            modelDefault = providerEntry.Name.Equals(config.DefaultProvider, StringComparison.Ordinal)
                ? config.DefaultModel
                : providerEntry.Models.FirstOrDefault();
        }

        var model = parseResult.GetValue(options.Model);
        if (model is null && isInteractive)
        {
            if (providerEntry is not null)
            {
                terminal.WriteLine($"Available models: {string.Join(", ", providerEntry.Models)}");
            }

            model = await ReadRequiredValueAsync(
                    terminal,
                    "Model",
                    modelDefault ?? config.DefaultModel)
                .ConfigureAwait(false);
        }

        var temperature = parseResult.GetValue(options.Temperature);
        if (temperature is null && isInteractive)
        {
            temperature = await ReadTemperatureAsync(
                    terminal,
                    current?.Temperature ?? 0.7f)
                .ConfigureAwait(false);
        }

        return new AgentConfig
        {
            Id = agentId,
            Name = name ?? current?.Name ?? agentId,
            Description = description ?? current?.Description ?? string.Empty,
            SystemPrompt = systemPrompt ?? current?.SystemPrompt ?? "You are a helpful AI assistant.",
            Provider = provider,
            Model = model ?? modelDefault ?? config.DefaultModel,
            Temperature = temperature ?? current?.Temperature ?? 0.7f
        };
    }

    private static void ValidateAgent(
        StandaloneConfigLoader loader,
        StandaloneConfig config,
        IReadOnlyList<AgentConfigDocument> documents,
        AgentConfig agent)
    {
        var path = Path.Combine(loader.AgentsDirectory, $"{agent.Id}.json");
        var prospectiveDocuments = documents
            .Where(document => !document.Agent.Id.Equals(agent.Id, StringComparison.Ordinal))
            .Append(new AgentConfigDocument(path, agent))
            .ToList();
        var errors = StandaloneConfigValidator.Validate(
            config,
            loader.ConfigPath,
            prospectiveDocuments);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private static async Task<StandaloneConfig> RequireConfigAsync(StandaloneConfigLoader loader) =>
        await loader.LoadAsync().ConfigureAwait(false)
        ?? throw new FileNotFoundException(
            "Standalone configuration is required before managing Agents.",
            loader.ConfigPath);

    private static async Task<string> ReadRequiredValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue)
    {
        while (true)
        {
            var value = await ReadRawValueAsync(terminal, prompt, defaultValue).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            terminal.WriteLine($"{prompt} is required.");
        }
    }

    private static async Task<string> ReadRawValueAsync(
        ICliTerminal terminal,
        string prompt,
        string defaultValue)
    {
        var defaultPrompt = string.IsNullOrEmpty(defaultValue) ? string.Empty : " [Enter to keep current]";
        terminal.Write($"{prompt}{defaultPrompt}: ");
        var value = await terminal.ReadLineAsync().ConfigureAwait(false);
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    private static async Task<float> ReadTemperatureAsync(
        ICliTerminal terminal,
        float defaultValue)
    {
        while (true)
        {
            terminal.Write($"Temperature [{defaultValue.ToString(CultureInfo.InvariantCulture)}]: ");
            var input = await terminal.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input))
            {
                return defaultValue;
            }

            if (float.TryParse(
                    input,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value)
                && value is >= 0 and <= 2)
            {
                return value;
            }

            terminal.WriteLine("Temperature must be a number from 0 through 2.");
        }
    }

    private static async Task<bool> ConfirmAsync(ICliTerminal terminal, string prompt)
    {
        terminal.Write(prompt);
        var input = (await terminal.ReadLineAsync().ConfigureAwait(false))?.Trim();
        return input?.Equals("y", StringComparison.OrdinalIgnoreCase) == true
            || input?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsAgentCommandException(Exception exception) =>
        exception is ArgumentException
            or ConfigBusyException
            or FileNotFoundException
            or InvalidOperationException
            or System.Text.Json.JsonException;

    private static StandaloneConfigLoader CreateLoader(string? configDirectory) =>
        configDirectory is null
            ? new StandaloneConfigLoader()
            : new StandaloneConfigLoader(configDirectory);

    private static void WriteSuccess(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
        string action,
        AgentConfig agent)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"{action} Agent: {agent.Id}");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString("status", "ok");
            writer.WritePropertyName("agent");
            WriteAgent(writer, agent);
            writer.WriteEndObject();
        });
    }

    private static void WriteDeleteSuccess(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string agentId,
        string? replacement)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"Deleted Agent: {agentId}");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", "agent delete");
            writer.WriteString("status", "ok");
            writer.WriteString("agentId", agentId);
            writer.WriteString("replacementDefaultAgent", replacement);
            writer.WriteEndObject();
        });
    }

    private static void WriteAgent(System.Text.Json.Utf8JsonWriter writer, AgentConfig agent)
    {
        writer.WriteStartObject();
        writer.WriteString("id", agent.Id);
        writer.WriteString("name", agent.Name);
        writer.WriteString("description", agent.Description);
        writer.WriteString("systemPrompt", agent.SystemPrompt);
        writer.WriteString("provider", agent.Provider);
        writer.WriteString("model", agent.Model);
        writer.WriteNumber("temperature", agent.Temperature);
        writer.WriteEndObject();
    }

    private static void WriteError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
        string message)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine(message);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteString("status", "error");
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
    }

    private sealed record AgentCommandOptions(
        Option<string?> Name,
        Option<string?> Description,
        Option<string?> SystemPrompt,
        Option<string?> Provider,
        Option<string?> Model,
        Option<float?> Temperature)
    {
        public Option[] All => [Name, Description, SystemPrompt, Provider, Model, Temperature];
    }
}
