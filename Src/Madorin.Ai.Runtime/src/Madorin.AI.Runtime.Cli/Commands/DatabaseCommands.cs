using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DatabaseCommands
{
    public static Command Create(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("db", "Maintain the Runtime state database.");
        command.Subcommands.Add(
            CreatePlaceholder("check", "Check database integrity.", jsonOption, output)
                .AddOptions(
                    CommandOptions.Create<bool>("--repair", "Repair supported integrity issues."),
                    CommandOptions.Create<bool>("--yes", "Skip interactive confirmation.")));
        command.Subcommands.Add(CreateRebuild(jsonOption, output));
        command.Subcommands.Add(
            CreatePlaceholder("vacuum", "Compact the SQLite database.", jsonOption, output));
        command.Subcommands.Add(
            CreatePlaceholder("backup", "Back up Runtime state.", jsonOption, output)
                .AddOptions(CommandOptions.Create<string?>("--output", "Set the backup path.")));
        command.Subcommands.Add(
            CreatePlaceholder("migrate", "Apply database migrations.", jsonOption, output)
                .AddOptions(CommandOptions.Create<string?>("--target", "Migrate to a target schema version.")));
        return command;
    }

    private static Command CreateRebuild(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("rebuild", "Rebuild indexes from canonical message files.");
        var dataDirOption = CommandOptions.Create<string?>(
            "--data-dir",
            "Runtime data directory that contains messages/ and state.db.");
        command.Options.Add(dataDirOption);
        command.SetAction(async parseResult =>
        {
            var dataDir = parseResult.GetValue(dataDirOption);
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".madorin",
                    "data");
            }

            dataDir = Path.GetFullPath(dataDir);
            if (!Directory.Exists(dataDir))
            {
                WriteRebuildError(
                    parseResult,
                    jsonOption,
                    output,
                    $"Data directory '{dataDir}' was not found.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                await using var connection = await DataDirectoryInitializer.InitializeAsync(dataDir)
                    .ConfigureAwait(false);
                _ = connection;
                var store = new ConversationStore(dataDir);
                var report = await store.RebuildIndexesAsync().ConfigureAwait(false);
                if (CliOutput.IsJson(parseResult, jsonOption))
                {
                    CliOutput.WriteJson(output, writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("command", "db rebuild");
                        writer.WriteString("status", "ok");
                        writer.WriteString("dataDirectory", dataDir);
                        writer.WriteNumber("sessionsScanned", report.SessionsScanned);
                        writer.WriteNumber("sessionsRebuilt", report.SessionsRebuilt);
                        writer.WriteNumber("messagesUpserted", report.MessagesUpserted);
                        writer.WriteString("backupPath", report.BackupPath);
                        writer.WritePropertyName("recoverableItems");
                        writer.WriteStartArray();
                        foreach (var item in report.RecoverableItems)
                        {
                            writer.WriteStringValue(item);
                        }

                        writer.WriteEndArray();
                        writer.WritePropertyName("unrecoverableDiagnostics");
                        writer.WriteStartArray();
                        foreach (var item in report.UnrecoverableDiagnostics)
                        {
                            writer.WriteStringValue(item);
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    });
                }
                else
                {
                    output.WriteLine(
                        $"db rebuild: rebuilt {report.SessionsRebuilt}/{report.SessionsScanned} session(s), {report.MessagesUpserted} message(s).");
                    output.WriteLine($"Backup: {report.BackupPath}");
                    foreach (var item in report.RecoverableItems)
                    {
                        output.WriteLine($"  recoverable: {item}");
                    }

                    foreach (var item in report.UnrecoverableDiagnostics)
                    {
                        output.WriteLine($"  unrecoverable: {item}");
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
            {
                WriteRebuildError(parseResult, jsonOption, output, ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static void WriteRebuildError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string message)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db rebuild");
                writer.WriteString("status", "error");
                writer.WriteString("message", message);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"db rebuild: {message}");
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
                $"db {name}",
                $"db {name}: NotImplemented");
            return ExitCodes.Success;
        });
        return command;
    }
}
