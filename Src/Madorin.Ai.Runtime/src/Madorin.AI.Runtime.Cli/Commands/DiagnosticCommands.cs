using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DiagnosticCommands
{
    public static Command CreateVersion(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("version", "Display program and protocol versions.");
        command.SetAction(parseResult =>
        {
            var info = CreateVersionInfo();
            if (parseResult.GetValue(jsonOption))
            {
                var json = JsonSerializer.Serialize(
                    info,
                    RuntimeJsonContext.Default.RuntimeVersionInfo);
                output.WriteLine(json);
            }
            else
            {
                output.WriteLine(
                    $"ai-runtime {info.Version} (protocol {info.ProtocolVersion})");
                output.WriteLine($"Runtime: {info.Product}");
                output.WriteLine(
                    $"Platform: {info.Framework} / {info.Platform} {info.Architecture}");
            }

            return ExitCodes.Success;
        });

        return command;
    }

    public static Command CreateDoctor()
    {
        return new Command("doctor", "Check configuration, Provider access, and runtime health.")
            .AddOptions(
                CommandOptions.Create<string?>("--provider", "Check only one Provider."),
                CommandOptions.Create<bool>("--fix", "Back up data and repair supported issues."),
                CommandOptions.Create<bool>("--yes", "Skip interactive confirmation."));
    }

    public static Command CreateProviders()
    {
        var command = new Command("providers", "View and manage Provider configuration.");
        command.Subcommands.Add(new Command("list", "List configured Providers."));
        command.Subcommands.Add(CreateProviderCommand("show", "Show Provider capabilities."));
        command.Subcommands.Add(
            CreateProviderCommand("test", "Test Provider connectivity.")
                .AddOptions(CommandOptions.Create<string?>("--model", "Test a specific model.")));
        command.Subcommands.Add(CreateAddProvider());
        command.Subcommands.Add(
            CreateProviderCommand("remove", "Remove a Provider.")
                .AddOptions(CommandOptions.Create<bool>("--confirm", "Confirm removal.")));
        return command;
    }

    private static Command CreateProviderCommand(string name, string description)
    {
        var command = new Command(name, description);
        command.Arguments.Add(new Argument<string>("providerId"));
        return command;
    }

    private static Command CreateAddProvider()
    {
        return CreateProviderCommand("add", "Add a Provider.")
            .AddOptions(
                CommandOptions.Create<string?>("--type", "Set the Provider type."),
                CommandOptions.Create<string?>("--endpoint", "Set the Provider endpoint."),
                CommandOptions.Create<string?>("--api-key-env", "Read the API key from an environment variable."),
                CommandOptions.Create<string?>("--api-key-file", "Read the API key from a protected file."),
                CommandOptions.Create<string?>("--config-file", "Read complete configuration from a file."));
    }

    private static RuntimeVersionInfo CreateVersionInfo()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
        return new RuntimeVersionInfo(
            "Madorin.AI.Runtime",
            version,
            ProtocolVersions.Current,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}
