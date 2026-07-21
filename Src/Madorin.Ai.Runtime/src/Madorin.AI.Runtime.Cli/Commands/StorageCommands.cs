using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class StorageCommands
{
    public static Command Create()
    {
        var command = new Command("storage", "Maintain messages and Blob storage.");
        command.Subcommands.Add(
            new Command("gc", "Remove unreferenced Blob content.")
                .AddOptions(
                    CommandOptions.Create<bool>("--dry-run", "Report without deleting files."),
                    CommandOptions.Create<int?>("--older-than-days", "Only include older content.")));
        command.Subcommands.Add(
            new Command("check", "Check message and Blob integrity.")
                .AddOptions(
                    CommandOptions.Create<bool>("--repair", "Repair supported storage issues."),
                    CommandOptions.Create<bool>("--yes", "Skip interactive confirmation.")));
        return command;
    }
}
