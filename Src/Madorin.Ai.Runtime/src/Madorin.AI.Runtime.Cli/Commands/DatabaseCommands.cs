using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DatabaseCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output)
    {
        var command = new Command("db", "Maintain the Runtime state database.");
        command.Subcommands.Add(CreateCheck(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output));
        command.Subcommands.Add(CreateRepair(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            input,
            output));
        command.Subcommands.Add(CreateRebuild(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            input,
            output));
        command.Subcommands.Add(CreateVacuum(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output));
        command.Subcommands.Add(CreateBackup(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output));
        command.Subcommands.Add(CreateMigrate(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output));
        return command;
    }

    private static Command CreateMigrate(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command("migrate", "Apply forward database migrations.");
        var targetOption = CommandOptions.Create<int?>(
            "--to",
            "Migrate to a target schema version; defaults to the latest supported version.");
        var dryRunOption = CommandOptions.Create<bool>(
            "--dry-run",
            "Show the migration plan without locking or writing.");
        command.Options.Add(targetOption);
        command.Options.Add(dryRunOption);
        command.SetAction(async parseResult =>
        {
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var targetVersion = parseResult.GetValue(targetOption);
                if (parseResult.GetValue(dryRunOption))
                {
                    var plan = await DatabaseMaintenanceService.PlanMigrationAsync(
                            context.DataDirectory,
                            targetVersion)
                        .ConfigureAwait(false);
                    WriteMigrationPlan(parseResult, jsonOption, output, plan);
                    return ExitCodes.Success;
                }

                var result = await DatabaseMaintenanceService.MigrateAsync(
                        context.WorkspaceRoot,
                        context.DataDirectory,
                        targetVersion)
                    .ConfigureAwait(false);
                WriteMigrationResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (DatabaseMaintenanceLockException ex)
            {
                WriteMigrationError(parseResult, jsonOption, output, ex.Message);
                return ExitCodes.WorkspaceError;
            }
            catch (DatabaseMigrationException ex)
            {
                WriteMigrationError(
                    parseResult,
                    jsonOption,
                    output,
                    ex.Message,
                    ex.BackupPath);
                return ExitCodes.GeneralError;
            }
            catch (ArgumentException ex)
            {
                WriteMigrationError(parseResult, jsonOption, output, ex.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                WriteMigrationError(parseResult, jsonOption, output, ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateVacuum(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command("vacuum", "Compact the SQLite database.");
        command.SetAction(async parseResult =>
        {
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var result = await DatabaseMaintenanceService.VacuumAsync(
                        context.WorkspaceRoot,
                        context.DataDirectory)
                    .ConfigureAwait(false);
                WriteVacuumResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (DatabaseMaintenanceLockException ex)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db vacuum",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
            catch (DatabaseVacuumException ex)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db vacuum",
                    ex.Message);
                return ExitCodes.GeneralError;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db vacuum",
                    ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateBackup(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command("backup", "Back up Runtime state.");
        var outputOption = CommandOptions.Create<string?>(
            "--output",
            "Set the destination database path.");
        var includeMessagesOption = CommandOptions.Create<bool>(
            "--include-messages",
            "Archive the messages directory beside the database backup.");
        command.Options.Add(outputOption);
        command.Options.Add(includeMessagesOption);
        command.SetAction(async parseResult =>
        {
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var result = await DatabaseMaintenanceService.BackupAsync(
                        context.WorkspaceRoot,
                        context.DataDirectory,
                        parseResult.GetValue(outputOption),
                        parseResult.GetValue(includeMessagesOption))
                    .ConfigureAwait(false);
                WriteBackupResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (DatabaseMaintenanceLockException ex)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db backup",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
            catch (DatabaseBackupException ex)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db backup",
                    ex.Message);
                return ExitCodes.GeneralError;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db backup",
                    ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateCheck(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command("check", "Check database integrity without modifying storage.");
        var verboseOption = CommandOptions.Create<bool>(
            "--verbose",
            "Show details for each database check.");
        command.Options.Add(verboseOption);
        command.SetAction(async parseResult =>
        {
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var result = await DatabaseMaintenanceService.CheckAsync(context.DataDirectory)
                    .ConfigureAwait(false);
                WriteCheckResult(
                    parseResult,
                    jsonOption,
                    output,
                    result,
                    parseResult.GetValue(verboseOption));
                return result.Healthy ? ExitCodes.Success : ExitCodes.GeneralError;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db check",
                    ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateRepair(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output)
    {
        var command = new Command(
            "repair",
            "Repair recognized JSONL tail damage and message-index drift.");
        var dryRunOption = CommandOptions.Create<bool>(
            "--dry-run",
            "Show the repair plan without locking, backing up, or modifying storage.");
        var yesOption = CommandOptions.Create<bool>(
            "--yes",
            "Confirm repair without prompting.");
        command.Options.Add(dryRunOption);
        command.Options.Add(yesOption);
        command.SetAction(async parseResult =>
        {
            var isJson = CliOutput.IsJson(parseResult, jsonOption);
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var plan = await DatabaseMaintenanceService.CheckAsync(context.DataDirectory)
                    .ConfigureAwait(false);
                if (plan.Healthy)
                {
                    WriteRepairResult(parseResult, jsonOption, output, "ok", plan, null, [],
                        "No repairable issues were found.");
                    return ExitCodes.Success;
                }

                if (!isJson)
                {
                    WriteHumanRepairPlan(output, plan);
                }

                if (!plan.CanRepair)
                {
                    WriteRepairResult(parseResult, jsonOption, output, "blocked", plan, null, [],
                        "Repair is blocked by non-repairable integrity issues.");
                    return ExitCodes.GeneralError;
                }

                if (parseResult.GetValue(dryRunOption))
                {
                    WriteRepairResult(parseResult, jsonOption, output, "dry-run", plan, null, [],
                        "No changes were made.");
                    return ExitCodes.Success;
                }

                if (!parseResult.GetValue(yesOption))
                {
                    if (isJson)
                    {
                        WriteRepairResult(parseResult, jsonOption, output, "error", plan, null, [],
                            "--yes is required for db repair with --json.");
                        return ExitCodes.InvalidArguments;
                    }

                    output.Write("Apply this repair plan? [y/N] ");
                    var answer = await input.ReadLineAsync().ConfigureAwait(false);
                    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        output.WriteLine("No changes were made.");
                        return ExitCodes.UserInterrupted;
                    }
                }
                var result = await DatabaseMaintenanceService.RepairAsync(
                        context.WorkspaceRoot,
                        context.DataDirectory)
                    .ConfigureAwait(false);
                WriteRepairResult(
                    parseResult,
                    jsonOption,
                    output,
                    "ok",
                    result.Before,
                    result.BackupPath,
                    result.RepairedSessionIds,
                    $"Repaired {result.RepairedSessionIds.Length} Session(s).");
                return ExitCodes.Success;
            }
            catch (DatabaseMaintenanceLockException ex)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db repair",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
            catch (DatabaseRepairException ex)
            {
                WriteRepairFailure(parseResult, jsonOption, output, ex);
                return ExitCodes.GeneralError;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                WriteMaintenanceError(
                    parseResult,
                    jsonOption,
                    output,
                    "db repair",
                    ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateRebuild(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output)
    {
        var command = new Command("rebuild", "Rebuild indexes from canonical message files.");
        var dryRunOption = CommandOptions.Create<bool>(
            "--dry-run",
            "Show the rebuild plan without locking, backing up, or modifying storage.");
        var confirmOption = CommandOptions.Create<bool>(
            "--confirm",
            "Confirm replacement of the existing state database.");
        command.Options.Add(dryRunOption);
        command.Options.Add(confirmOption);
        command.SetAction(async parseResult =>
        {
            var isJson = CliOutput.IsJson(parseResult, jsonOption);
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var plan = await DatabaseMaintenanceService.PlanRebuildAsync(
                        context.DataDirectory)
                    .ConfigureAwait(false);

                if (!isJson)
                {
                    WriteHumanRebuildPlan(output, plan);
                }

                if (parseResult.GetValue(dryRunOption))
                {
                    if (isJson)
                    {
                        WriteRebuildPlan(
                            parseResult,
                            jsonOption,
                            output,
                            "dry-run",
                            plan,
                            "No changes were made.");
                    }
                    else
                    {
                        output.WriteLine("No changes were made.");
                    }

                    return ExitCodes.Success;
                }

                if (!parseResult.GetValue(confirmOption))
                {
                    if (isJson)
                    {
                        WriteRebuildPlan(
                            parseResult,
                            jsonOption,
                            output,
                            "error",
                            plan,
                            "--confirm is required for db rebuild with --json.");
                        return ExitCodes.InvalidArguments;
                    }

                    output.Write("Replace state.db with the rebuild result? [y/N] ");
                    var answer = await input.ReadLineAsync().ConfigureAwait(false);
                    if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                    {
                        output.WriteLine("No changes were made.");
                        return ExitCodes.UserInterrupted;
                    }
                }

                var result = await DatabaseMaintenanceService.RebuildAsync(
                        context.WorkspaceRoot,
                        context.DataDirectory)
                    .ConfigureAwait(false);
                WriteRebuildResult(
                    parseResult,
                    jsonOption,
                    output,
                    result.Complete ? "ok" : "partial",
                    result);
                return result.Complete ? ExitCodes.Success : ExitCodes.GeneralError;
            }
            catch (DatabaseMaintenanceLockException ex)
            {
                WriteRebuildError(parseResult, jsonOption, output, ex.Message);
                return ExitCodes.WorkspaceError;
            }
            catch (DatabaseRebuildException ex)
            {
                WriteRebuildError(
                    parseResult,
                    jsonOption,
                    output,
                    ex.Message,
                    ex.BackupPath);
                return ExitCodes.GeneralError;
            }
            catch (Exception ex) when (ex is InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                WriteRebuildError(parseResult, jsonOption, output, ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static void WriteCheckResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        DatabaseCheckResult result,
        bool verbose)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db check");
                writer.WriteString("status", result.Healthy ? "ok" : "issues");
                WriteCheckProperties(writer, result);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(result.Healthy
            ? "db check: ok"
            : $"db check: found {result.Issues.Length} issue(s).");
        if (verbose)
        {
            output.WriteLine($"Data: {result.DataDirectory}");
            output.WriteLine(
                $"Schema: {result.SchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}/{result.SupportedSchemaVersion}");
            output.WriteLine($"Journal mode: {result.JournalMode ?? "unknown"}");
        }

        WriteHumanIssues(output, result.Issues);
    }

    private static void WriteBackupResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        DatabaseBackupResult result)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db backup");
                writer.WriteString("status", "ok");
                writer.WriteString("dataDirectory", result.DataDirectory);
                writer.WriteString("databasePath", result.DatabasePath);
                if (result.MessagesArchivePath is null)
                {
                    writer.WriteNull("messagesArchivePath");
                }
                else
                {
                    writer.WriteString("messagesArchivePath", result.MessagesArchivePath);
                }

                writer.WriteNumber("messageFileCount", result.MessageFileCount);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine("db backup: ok");
        output.WriteLine($"Database: {result.DatabasePath}");
        if (result.MessagesArchivePath is not null)
        {
            output.WriteLine(
                $"Messages: {result.MessagesArchivePath} ({result.MessageFileCount} file(s))");
        }
    }

    private static void WriteVacuumResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        DatabaseVacuumResult result)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db vacuum");
                writer.WriteString("status", "ok");
                writer.WriteString("dataDirectory", result.DataDirectory);
                writer.WriteString("databasePath", result.DatabasePath);
                writer.WriteNumber("bytesBefore", result.BytesBefore);
                writer.WriteNumber("bytesAfter", result.BytesAfter);
                writer.WriteNumber("bytesReclaimed", result.BytesReclaimed);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(
            $"db vacuum: reclaimed {result.BytesReclaimed} byte(s); "
            + $"database is {result.BytesAfter} byte(s).");
        output.WriteLine($"Database: {result.DatabasePath}");
    }

    private static void WriteMigrationPlan(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        DatabaseMigrationPlan plan)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db migrate");
                writer.WriteString("status", "dry-run");
                WriteMigrationProperties(
                    writer,
                    plan.DataDirectory,
                    plan.DatabasePath,
                    plan.FromVersion,
                    plan.TargetVersion,
                    "pendingVersions",
                    plan.PendingVersions,
                    null);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(
            $"db migrate plan: schema {plan.FromVersion} -> {plan.TargetVersion}; "
            + $"{plan.PendingVersions.Length} migration(s).");
    }

    private static void WriteMigrationResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        DatabaseMigrationResult result)
    {
        var status = result.AppliedVersions.Length == 0 ? "up-to-date" : "migrated";
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db migrate");
                writer.WriteString("status", status);
                WriteMigrationProperties(
                    writer,
                    result.DataDirectory,
                    result.DatabasePath,
                    result.FromVersion,
                    result.TargetVersion,
                    "appliedVersions",
                    result.AppliedVersions,
                    result.BackupPath);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(result.AppliedVersions.Length == 0
            ? $"db migrate: up-to-date at schema {result.TargetVersion}."
            : $"db migrate: schema {result.FromVersion} -> {result.TargetVersion}; {result.AppliedVersions.Length} migration(s) applied.");
        if (result.BackupPath is not null)
        {
            output.WriteLine($"Recovery point: {result.BackupPath}");
        }
    }

    private static void WriteMigrationProperties(
        Utf8JsonWriter writer,
        string dataDirectory,
        string databasePath,
        int fromVersion,
        int targetVersion,
        string versionsPropertyName,
        IReadOnlyList<int> versions,
        string? backupPath)
    {
        writer.WriteString("dataDirectory", dataDirectory);
        writer.WriteString("databasePath", databasePath);
        writer.WriteNumber("fromVersion", fromVersion);
        writer.WriteNumber("targetVersion", targetVersion);
        writer.WriteStartArray(versionsPropertyName);
        foreach (var version in versions)
        {
            writer.WriteNumberValue(version);
        }

        writer.WriteEndArray();
        if (backupPath is null)
        {
            writer.WriteNull("backupPath");
        }
        else
        {
            writer.WriteString("backupPath", backupPath);
        }
    }

    private static void WriteMigrationError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string message,
        string? backupPath = null)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db migrate");
                writer.WriteString("status", "error");
                writer.WriteString("message", message);
                if (backupPath is null)
                {
                    writer.WriteNull("backupPath");
                }
                else
                {
                    writer.WriteString("backupPath", backupPath);
                }

                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"db migrate: {message}");
    }

    private static void WriteRepairResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string status,
        DatabaseCheckResult plan,
        string? backupPath,
        IReadOnlyList<string> repairedSessionIds,
        string message)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db repair");
                writer.WriteString("status", status);
                writer.WriteString("message", message);
                WriteCheckProperties(writer, plan);
                if (backupPath is null)
                {
                    writer.WriteNull("backupPath");
                }
                else
                {
                    writer.WriteString("backupPath", backupPath);
                }

                writer.WriteStartArray("repairedSessionIds");
                foreach (var sessionId in repairedSessionIds)
                {
                    writer.WriteStringValue(sessionId);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"db repair: {message}");
        if (backupPath is not null)
        {
            output.WriteLine($"Backup: {backupPath}");
        }
    }

    private static void WriteHumanRepairPlan(TextWriter output, DatabaseCheckResult plan)
    {
        output.WriteLine(
            $"db repair plan: {plan.RepairableIssueCount} repairable issue(s) across "
            + $"{plan.Issues.Where(static issue => issue.Repairable && issue.SessionId is not null).Select(static issue => issue.SessionId).Distinct(StringComparer.Ordinal).Count()} Session(s).");
        WriteHumanIssues(output, plan.Issues);
    }

    private static void WriteHumanIssues(
        TextWriter output,
        IReadOnlyList<DatabaseCheckIssue> issues)
    {
        foreach (var issue in issues)
        {
            var session = issue.SessionId is null ? string.Empty : $" [{issue.SessionId}]";
            var repairable = issue.Repairable ? " (repairable)" : string.Empty;
            output.WriteLine(
                $"  {issue.Severity}: {issue.Code}{session}{repairable}: {issue.Message}");
        }
    }

    private static void WriteCheckProperties(
        Utf8JsonWriter writer,
        DatabaseCheckResult result)
    {
        writer.WriteString("dataDirectory", result.DataDirectory);
        writer.WriteBoolean("healthy", result.Healthy);
        writer.WriteNumber("issueCount", result.Issues.Length);
        writer.WriteNumber("repairableIssueCount", result.RepairableIssueCount);
        if (result.SchemaVersion is { } schemaVersion)
        {
            writer.WriteNumber("schemaVersion", schemaVersion);
        }
        else
        {
            writer.WriteNull("schemaVersion");
        }

        writer.WriteNumber("supportedSchemaVersion", result.SupportedSchemaVersion);
        if (result.JournalMode is null)
        {
            writer.WriteNull("journalMode");
        }
        else
        {
            writer.WriteString("journalMode", result.JournalMode);
        }

        writer.WriteStartArray("issues");
        foreach (var issue in result.Issues)
        {
            writer.WriteStartObject();
            writer.WriteString("code", issue.Code);
            writer.WriteString("severity", issue.Severity);
            writer.WriteString("message", issue.Message);
            writer.WriteBoolean("repairable", issue.Repairable);
            if (issue.SessionId is null)
            {
                writer.WriteNull("sessionId");
            }
            else
            {
                writer.WriteString("sessionId", issue.SessionId);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRepairFailure(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        DatabaseRepairException exception)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db repair");
                writer.WriteString("status", "error");
                writer.WriteString("message", exception.Message);
                if (exception.BackupPath is null)
                {
                    writer.WriteNull("backupPath");
                }
                else
                {
                    writer.WriteString("backupPath", exception.BackupPath);
                }

                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"db repair: {exception.Message}");
    }

    private static void WriteMaintenanceError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
        string message)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", command);
                writer.WriteString("status", "error");
                writer.WriteString("message", message);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"{command}: {message}");
    }

    private static void WriteHumanRebuildPlan(
        TextWriter output,
        DatabaseRebuildPlan plan)
    {
        output.WriteLine(
            $"db rebuild plan: {plan.SessionsRecoverable}/{plan.SessionsScanned} Session(s) and "
            + $"{plan.MessagesRecoverable} message(s) can be restored; "
            + $"{plan.SessionsUnrecoverable} Session(s) cannot be restored.");
        foreach (var item in plan.RecoverableItems)
        {
            output.WriteLine($"  recoverable: {item}");
        }

        foreach (var diagnostic in plan.UnrecoverableDiagnostics)
        {
            output.WriteLine($"  unrecoverable: {diagnostic}");
        }
    }

    private static void WriteRebuildPlan(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string status,
        DatabaseRebuildPlan plan,
        string message)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", "db rebuild");
            writer.WriteString("status", status);
            writer.WriteString("message", message);
            writer.WriteString("dataDirectory", plan.DataDirectory);
            writer.WriteNumber("sessionsScanned", plan.SessionsScanned);
            writer.WriteNumber("sessionsRecoverable", plan.SessionsRecoverable);
            writer.WriteNumber("sessionsUnrecoverable", plan.SessionsUnrecoverable);
            writer.WriteNumber("messagesRecoverable", plan.MessagesRecoverable);
            writer.WriteNull("backupPath");
            writer.WriteStartArray("recoverableItems");
            foreach (var item in plan.RecoverableItems)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("unrecoverableDiagnostics");
            foreach (var diagnostic in plan.UnrecoverableDiagnostics)
            {
                writer.WriteStringValue(diagnostic);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static void WriteRebuildResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string status,
        DatabaseRebuildResult result)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db rebuild");
                writer.WriteString("status", status);
                writer.WriteString("dataDirectory", result.DataDirectory);
                writer.WriteNumber("sessionsScanned", result.SessionsScanned);
                writer.WriteNumber("sessionsRebuilt", result.SessionsRebuilt);
                writer.WriteNumber(
                    "sessionsUnrecoverable",
                    result.SessionsScanned - result.SessionsRebuilt);
                writer.WriteNumber("messagesUpserted", result.MessagesUpserted);
                writer.WriteString("backupPath", result.BackupPath);
                writer.WriteStartArray("recoverableItems");
                foreach (var item in result.RecoverableItems)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("unrecoverableDiagnostics");
                foreach (var diagnostic in result.UnrecoverableDiagnostics)
                {
                    writer.WriteStringValue(diagnostic);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(
            $"db rebuild: rebuilt {result.SessionsRebuilt}/{result.SessionsScanned} Session(s), "
            + $"{result.MessagesUpserted} message(s).");
        output.WriteLine($"Recovery point: {result.BackupPath}");
        foreach (var diagnostic in result.UnrecoverableDiagnostics)
        {
            output.WriteLine($"  unrecoverable: {diagnostic}");
        }
    }

    private static void WriteRebuildError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string message,
        string? backupPath = null)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "db rebuild");
                writer.WriteString("status", "error");
                writer.WriteString("message", message);
                if (backupPath is null)
                {
                    writer.WriteNull("backupPath");
                }
                else
                {
                    writer.WriteString("backupPath", backupPath);
                }

                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"db rebuild: {message}");
    }

}
