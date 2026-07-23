using System.CommandLine;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class StorageCommands
{
    public static Command Create(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("storage", "Maintain messages and Blob storage.");
        command.Subcommands.Add(
            CreatePlaceholder("gc", "Remove unreferenced Blob content.", jsonOption, output)
                .AddOptions(
                    CommandOptions.Create<bool>("--dry-run", "Report without deleting files."),
                    CommandOptions.Create<int?>("--older-than-days", "Only include older content.")));
        command.Subcommands.Add(
            CreatePlaceholder("check", "Check message and Blob integrity.", jsonOption, output)
                .AddOptions(
                    CommandOptions.Create<bool>("--repair", "Repair supported storage issues."),
                    CommandOptions.Create<bool>("--yes", "Skip interactive confirmation.")));
        return command;
    }

    private static Command CreatePlaceholder(
        string name,
        string description,
        Option<bool> jsonOption,
        TextWriter output)
    {
        var command = new Command(name, description);
        command.SetAction(parseResult =>
        {
            CliOutput.WriteNotImplemented(
                parseResult,
                jsonOption,
                output,
                $"storage {name}",
                $"storage {name}: NotImplemented");
            return ExitCodes.Success;
        });
        return command;
    }
}
