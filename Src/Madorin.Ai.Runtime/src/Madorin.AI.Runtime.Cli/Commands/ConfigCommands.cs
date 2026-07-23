using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ConfigCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory = null)
    {
        var command = new Command("config", "Manage standalone Runtime configuration.");
        command.Subcommands.Add(CreateInit(jsonOption, terminal, output, configDirectory));
        command.Subcommands.Add(CreateEdit(jsonOption, terminal, output, configDirectory));
        command.Subcommands.Add(CreateShow(jsonOption, output, configDirectory));
        command.Subcommands.Add(CreateValidate(jsonOption, output, configDirectory));
        return command;
    }

    private static Command CreateInit(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory)
    {
        var command = new Command("init", "Initialize standalone Runtime configuration.");
        command.SetAction(async parseResult =>
        {
            var loader = CreateLoader(configDirectory);
            var existing = await loader.LoadAsync().ConfigureAwait(false);
            if (CliOutput.IsJson(parseResult, jsonOption) && existing is null)
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "config init",
                    "Interactive configuration initialization is unavailable with --json.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                if (!CliOutput.IsJson(parseResult, jsonOption))
                {
                    _ = await ConfigWizard.RunInitAsync(
                            loader.ConfigDirectory,
                            terminal)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsConfigurationException(ex))
            {
                WriteError(parseResult, jsonOption, output, "config init", ex.Message);
                return ExitCodes.InvalidArguments;
            }

            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("command", "config init");
                    writer.WriteString("status", "ok");
                    writer.WriteString("path", loader.ConfigPath);
                    writer.WriteEndObject();
                });
            }
            else
            {
                output.WriteLine($"Configuration ready: {loader.ConfigPath}");
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateEdit(
        Option<bool> jsonOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory)
    {
        var command = new Command("edit", "Edit standalone Runtime configuration.");
        command.SetAction(async parseResult =>
        {
            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "config edit",
                    "Interactive configuration editing is unavailable with --json.");
                return ExitCodes.InvalidArguments;
            }

            var loader = CreateLoader(configDirectory);
            try
            {
                _ = await ConfigWizard.RunEditAsync(loader.ConfigDirectory, terminal)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConfigurationException(ex))
            {
                output.WriteLine(ex.Message);
                return ExitCodes.InvalidArguments;
            }

            output.WriteLine($"Configuration updated: {loader.ConfigPath}");
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateShow(
        Option<bool> jsonOption,
        TextWriter output,
        string? configDirectory)
    {
        var command = new Command("show", "Show standalone Runtime configuration.");
        command.SetAction(async parseResult =>
        {
            var loader = CreateLoader(configDirectory);
            StandaloneConfig? config;
            try
            {
                config = await loader.LoadAsync().ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                WriteError(parseResult, jsonOption, output, "config show", ex.Message);
                return ExitCodes.InvalidArguments;
            }

            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                WriteConfigJson(output, loader.ConfigPath, config);
            }
            else if (config is null)
            {
                output.WriteLine($"Configuration not found: {loader.ConfigPath}");
            }
            else
            {
                output.WriteLine($"Path: {loader.ConfigPath}");
                output.WriteLine($"Default provider: {config.DefaultProvider}");
                output.WriteLine($"Default model: {config.DefaultModel}");
                output.WriteLine($"Default agent: {config.DefaultAgent}");
                foreach (var provider in config.Providers)
                {
                    output.WriteLine(
                        $"Provider: {provider.Name} ({provider.Protocol}) key={StandaloneConfigLoader.MaskApiKey(provider.ApiKey)}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateValidate(
        Option<bool> jsonOption,
        TextWriter output,
        string? configDirectory)
    {
        var command = new Command("validate", "Validate standalone Runtime configuration.");
        command.SetAction(async parseResult =>
        {
            var loader = CreateLoader(configDirectory);
            StandaloneConfig? config = null;
            IReadOnlyList<StandaloneConfigValidationError> errors;
            try
            {
                config = await loader.LoadAsync().ConfigureAwait(false);
                if (config is null)
                {
                    errors =
                    [
                        new StandaloneConfigValidationError(
                            loader.ConfigPath,
                            "config",
                            "Configuration file does not exist.")
                    ];
                }
                else
                {
                    var agents = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
                    errors = StandaloneConfigValidator.Validate(
                        config,
                        loader.ConfigPath,
                        agents);
                }
            }
            catch (JsonException ex)
            {
                errors =
                [
                    new StandaloneConfigValidationError(
                        loader.ConfigPath,
                        "json",
                        ex.Message)
                ];
            }

            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("command", "config validate");
                    writer.WriteBoolean("configured", config is not null);
                    writer.WriteBoolean("valid", errors.Count == 0);
                    writer.WriteStartArray("errors");
                    foreach (var error in errors)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", error.FilePath);
                        writer.WriteString("field", error.Field);
                        writer.WriteString("message", error.Message);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else if (errors.Count == 0)
            {
                output.WriteLine("Configuration is valid.");
            }
            else
            {
                output.WriteLine("Configuration is invalid:");
                foreach (var error in errors)
                {
                    output.WriteLine($"- {error}");
                }
            }

            return errors.Count == 0 ? ExitCodes.Success : ExitCodes.InvalidArguments;
        });
        return command;
    }

    private static bool IsConfigurationException(Exception exception) =>
        exception is ArgumentException
            or ConfigBusyException
            or FileNotFoundException
            or InvalidOperationException
            or JsonException;

    private static StandaloneConfigLoader CreateLoader(string? configDirectory) =>
        configDirectory is null
            ? new StandaloneConfigLoader()
            : new StandaloneConfigLoader(configDirectory);

    private static void WriteConfigJson(
        TextWriter output,
        string configPath,
        StandaloneConfig? config)
    {
        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", "config show");
            writer.WriteString("path", configPath);
            writer.WriteBoolean("configured", config is not null);
            if (config is not null)
            {
                writer.WriteString("defaultProvider", config.DefaultProvider);
                writer.WriteString("defaultModel", config.DefaultModel);
                writer.WriteString("defaultAgent", config.DefaultAgent);
                writer.WriteStartArray("providers");
                foreach (var provider in config.Providers)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", provider.Name);
                    writer.WriteString("protocol", provider.Protocol);
                    writer.WriteString("baseUrl", provider.BaseUrl);
                    writer.WriteString("apiKey", StandaloneConfigLoader.MaskApiKey(provider.ApiKey));
                    writer.WriteStartArray("models");
                    foreach (var model in provider.Models)
                    {
                        writer.WriteStringValue(model);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
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
}
