using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ControlCommands
{
    public static Command Create()
    {
        var command = new Command("ctl", "Control a running Runtime instance.");
        command.Subcommands.Add(new Command("status", "Show Runtime status."));
        command.Subcommands.Add(new Command("sessions", "Show active Sessions."));
        command.Subcommands.Add(new Command("runs", "Show active Runs."));

        var cancel = new Command("cancel", "Cancel an active Run.");
        cancel.Arguments.Add(new Argument<string>("runId"));
        command.Subcommands.Add(cancel);

        var credential = new Command("credential", "Manage live Provider credentials.");
        credential.Subcommands.Add(
            new Command("update", "Push updated credentials.")
                .AddOptions(
                    CommandOptions.Create<string?>("--provider", "Select a Provider."),
                    CommandOptions.Create<string?>("--api-key-env", "Read the API key from an environment variable."),
                    CommandOptions.Create<string?>("--api-key-file", "Read the API key from a protected file.")));
        command.Subcommands.Add(credential);

        return command;
    }
}
