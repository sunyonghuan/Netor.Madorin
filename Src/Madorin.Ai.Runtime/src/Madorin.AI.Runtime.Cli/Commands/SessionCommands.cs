using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class SessionCommands
{
    public static Command Create(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("session", "Manage persisted Sessions.");
        command.Subcommands.Add(
            CreatePlaceholder(
                    "list",
                    "List Sessions.",
                    jsonOption,
                    output,
                    "session list: NotImplemented (阶段6A 完成后可用)")
                .AddOptions(
                    CommandOptions.Create<string?>("--status", "Filter active, archived, or all Sessions."),
                    CommandOptions.Create<string?>("--mode", "Filter by Runtime mode."),
                    CommandOptions.Create<int?>("--limit", "Limit the result count."),
                    CommandOptions.Create<string?>("--cursor", "Continue cursor-based pagination.")));
        command.Subcommands.Add(
            CreateSessionCommand(
                    "show",
                    "Show Session details.",
                    jsonOption,
                    output)
                .AddOptions(CommandOptions.Create<bool>("--messages", "Include message history.")));
        command.Subcommands.Add(
            CreateSessionCommand("export", "Export a Session.", jsonOption, output)
                .AddOptions(
                    CommandOptions.Create<string?>("--format", "Select markdown, json, or jsonl."),
                    CommandOptions.Create<string?>("--output", "Write to a file.")));
        command.Subcommands.Add(
            CreateSessionCommand("delete", "Delete a Session.", jsonOption, output)
                .AddOptions(CommandOptions.Create<bool>("--confirm", "Confirm deletion.")));
        command.Subcommands.Add(
            CreateSessionCommand("compact", "Compact Session context.", jsonOption, output)
                .AddOptions(
                    CommandOptions.Create<string?>("--strategy", "Select the compaction strategy."),
                    CommandOptions.Create<int?>("--keep-last-tokens", "Keep a trailing token window.")));
        command.Subcommands.Add(
            CreateSessionCommand("archive", "Archive a Session.", jsonOption, output));
        command.Subcommands.Add(
            CreateSessionCommand("unarchive", "Restore an archived Session.", jsonOption, output));
        return command;
    }

    private static Command CreateSessionCommand(
        string name,
        string description,
        Option<bool> jsonOption,
        TextWriter output)
    {
        var message = name == "show"
            ? "session show: NotImplemented"
            : $"session {name}: NotImplemented";
        var command = CreatePlaceholder(name, description, jsonOption, output, message);
        command.Arguments.Add(new Argument<string>("sessionId"));
        return command;
    }

    private static Command CreatePlaceholder(
        string name,
        string description,
        Option<bool> jsonOption,
        TextWriter output,
        string message)
    {
        var command = new Command(name, description);
        command.SetAction(parseResult =>
        {
            CliOutput.WriteNotImplemented(
                parseResult,
                jsonOption,
                output,
                $"session {name}",
                message);
            return ExitCodes.Success;
        });
        return command;
    }
}
