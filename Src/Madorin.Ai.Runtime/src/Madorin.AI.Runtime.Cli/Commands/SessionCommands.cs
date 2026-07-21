using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class SessionCommands
{
    public static Command Create()
    {
        var command = new Command("session", "Manage persisted Sessions.");
        command.Subcommands.Add(
            new Command("list", "List Sessions.")
                .AddOptions(
                    CommandOptions.Create<string?>("--status", "Filter active, archived, or all Sessions."),
                    CommandOptions.Create<string?>("--mode", "Filter by Runtime mode."),
                    CommandOptions.Create<int?>("--limit", "Limit the result count."),
                    CommandOptions.Create<string?>("--cursor", "Continue cursor-based pagination.")));
        command.Subcommands.Add(
            CreateSessionCommand("show", "Show Session details.")
                .AddOptions(CommandOptions.Create<bool>("--messages", "Include message history.")));
        command.Subcommands.Add(
            CreateSessionCommand("export", "Export a Session.")
                .AddOptions(
                    CommandOptions.Create<string?>("--format", "Select markdown, json, or jsonl."),
                    CommandOptions.Create<string?>("--output", "Write to a file.")));
        command.Subcommands.Add(
            CreateSessionCommand("delete", "Delete a Session.")
                .AddOptions(CommandOptions.Create<bool>("--confirm", "Confirm deletion.")));
        command.Subcommands.Add(
            CreateSessionCommand("compact", "Compact Session context.")
                .AddOptions(
                    CommandOptions.Create<string?>("--strategy", "Select the compaction strategy."),
                    CommandOptions.Create<int?>("--keep-last-tokens", "Keep a trailing token window.")));
        command.Subcommands.Add(CreateSessionCommand("archive", "Archive a Session."));
        command.Subcommands.Add(CreateSessionCommand("unarchive", "Restore an archived Session."));
        return command;
    }

    private static Command CreateSessionCommand(string name, string description)
    {
        var command = new Command(name, description);
        command.Arguments.Add(new Argument<string>("sessionId"));
        return command;
    }
}
