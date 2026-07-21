using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DatabaseCommands
{
    public static Command Create()
    {
        var command = new Command("db", "Maintain the Runtime state database.");
        command.Subcommands.Add(
            new Command("check", "Check database integrity.")
                .AddOptions(
                    CommandOptions.Create<bool>("--repair", "Repair supported integrity issues."),
                    CommandOptions.Create<bool>("--yes", "Skip interactive confirmation.")));
        command.Subcommands.Add(new Command("rebuild", "Rebuild indexes from canonical message files."));
        command.Subcommands.Add(new Command("vacuum", "Compact the SQLite database."));
        command.Subcommands.Add(
            new Command("backup", "Back up Runtime state.")
                .AddOptions(CommandOptions.Create<string?>("--output", "Set the backup path.")));
        command.Subcommands.Add(
            new Command("migrate", "Apply database migrations.")
                .AddOptions(CommandOptions.Create<string?>("--target", "Migrate to a target schema version.")));
        return command;
    }
}
