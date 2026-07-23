using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ControlCommands
{
    public static Command Create(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("ctl", "Control a running Runtime instance.");
        command.Subcommands.Add(
            CreatePlaceholder("status", "Show Runtime status.", jsonOption, output));
        command.Subcommands.Add(
            CreatePlaceholder("sessions", "Show active Sessions.", jsonOption, output));
        command.Subcommands.Add(
            CreatePlaceholder("runs", "Show active Runs.", jsonOption, output));

        var cancel = CreatePlaceholder("cancel", "Cancel an active Run.", jsonOption, output);
        cancel.Arguments.Add(new Argument<string>("runId"));
        command.Subcommands.Add(cancel);

        var credential = new Command("credential", "Manage live Provider credentials.");
        credential.Subcommands.Add(
            CreatePlaceholder("credential update", "Push updated credentials.", jsonOption, output)
                .AddOptions(
                    CommandOptions.Create<string?>("--provider", "Select a Provider."),
                    CommandOptions.Create<string?>("--api-key-env", "Read the API key from an environment variable."),
                    CommandOptions.Create<string?>("--api-key-file", "Read the API key from a protected file.")));
        command.Subcommands.Add(credential);

        return command;
    }

    private static Command CreatePlaceholder(
        string name,
        string description,
        Option<bool> jsonOption,
        TextWriter output)
    {
        var commandName = name.Contains(' ')
            ? name[(name.LastIndexOf(' ') + 1)..]
            : name;
        var command = new Command(commandName, description);
        command.SetAction(parseResult =>
        {
            CliOutput.WriteNotImplemented(
                parseResult,
                jsonOption,
                output,
                $"ctl {name}",
                $"ctl {name}: NotImplemented");
            return ExitCodes.Success;
        });
        return command;
    }
}
