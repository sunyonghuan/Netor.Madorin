using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class StorageCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command("storage", "Maintain messages and Blob storage.");
        command.Subcommands.Add(CreateCheck(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output));
        command.Subcommands.Add(CreateGc(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output));
        return command;
    }

    private static Command CreateCheck(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command(
            "check",
            "Check message references and Blob integrity without modifying storage.");
        var verifyHashesOption = CommandOptions.Create<bool>(
            "--verify-hashes",
            "Stream Blob content through SHA-256 validation.");
        command.Options.Add(verifyHashesOption);
        command.SetAction(async parseResult =>
        {
            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                var result = await StorageMaintenanceService.CheckAsync(
                        context.DataDirectory,
                        parseResult.GetValue(verifyHashesOption))
                    .ConfigureAwait(false);
                WriteCheckResult(parseResult, jsonOption, output, result);
                return result.Healthy ? ExitCodes.Success : ExitCodes.GeneralError;
            }
            catch (ArgumentException ex)
            {
                WriteError(parseResult, jsonOption, output, "storage check", ex.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                WriteError(parseResult, jsonOption, output, "storage check", ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static Command CreateGc(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command(
            "gc",
            "Move old unreferenced Blob content into a recovery point.");
        var dryRunOption = CommandOptions.Create<bool>(
            "--dry-run",
            "Show the collection plan without locking or modifying storage.");
        var olderThanOption = CommandOptions.Create<int?>(
            "--older-than",
            "Only collect orphan Blobs at least this many days old; defaults to 7.");
        var confirmOption = CommandOptions.Create<bool>(
            "--confirm",
            "Confirm creation of a recovery point and collection of eligible Blobs.");
        command.Options.Add(dryRunOption);
        command.Options.Add(olderThanOption);
        command.Options.Add(confirmOption);
        command.SetAction(async parseResult =>
        {
            var dryRun = parseResult.GetValue(dryRunOption);
            var confirm = parseResult.GetValue(confirmOption);
            var olderThanDays = parseResult.GetValue(olderThanOption)
                ?? StorageMaintenanceService.DefaultOlderThanDays;
            if (olderThanDays < 0)
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "storage gc",
                    "--older-than must be zero or greater.");
                return ExitCodes.InvalidArguments;
            }

            if (dryRun && confirm)
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "storage gc",
                    "--dry-run and --confirm cannot be used together.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption));
                if (!confirm)
                {
                    var plan = await StorageMaintenanceService.PlanGcAsync(
                            context.DataDirectory,
                            olderThanDays)
                        .ConfigureAwait(false);
                    WriteGcPlan(parseResult, jsonOption, output, plan);
                    return plan.CanCollect ? ExitCodes.Success : ExitCodes.GeneralError;
                }

                var result = await StorageMaintenanceService.CollectGarbageAsync(
                        context.WorkspaceRoot,
                        context.DataDirectory,
                        olderThanDays)
                    .ConfigureAwait(false);
                WriteGcResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (StorageGcBlockedException ex)
            {
                WriteGcPlan(parseResult, jsonOption, output, ex.Plan);
                return ExitCodes.GeneralError;
            }
            catch (StorageMaintenanceLockException ex)
            {
                WriteError(parseResult, jsonOption, output, "storage gc", ex.Message);
                return ExitCodes.WorkspaceError;
            }
            catch (StorageGcException ex)
            {
                WriteGcFailure(parseResult, jsonOption, output, ex);
                return ExitCodes.GeneralError;
            }
            catch (ArgumentException ex)
            {
                WriteError(parseResult, jsonOption, output, "storage gc", ex.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException)
            {
                WriteError(parseResult, jsonOption, output, "storage gc", ex.Message);
                return ExitCodes.GeneralError;
            }
        });
        return command;
    }

    private static void WriteCheckResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        StorageCheckResult result)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "storage check");
                writer.WriteString("status", result.Healthy ? "ok" : "issues");
                writer.WriteBoolean("healthy", result.Healthy);
                writer.WriteString("dataDirectory", result.DataDirectory);
                writer.WriteString("blobDirectory", result.BlobDirectory);
                writer.WriteBoolean("verifyHashes", result.VerifyHashes);
                writer.WriteBoolean("referenceScanComplete", result.ReferenceScanComplete);
                writer.WriteNumber("referenceCount", result.ReferenceCount);
                writer.WriteNumber("blobFileCount", result.BlobFileCount);
                writer.WriteNumber("orphanCount", result.OrphanCount);
                writer.WriteNumber("issueCount", result.Issues.Length);
                WriteIssues(writer, "issues", result.Issues);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(result.Healthy
            ? $"storage check: ok; {result.ReferenceCount} reference(s), {result.BlobFileCount} Blob file(s)."
            : $"storage check: found {result.Issues.Length} issue(s).");
        WriteHumanIssues(output, result.Issues);
    }

    private static void WriteGcPlan(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        StorageGcPlan plan)
    {
        var status = plan.CanCollect ? "dry-run" : "blocked";
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "storage gc");
                writer.WriteString("status", status);
                WriteGcPlanProperties(writer, plan);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(plan.CanCollect
            ? $"storage gc plan: {plan.CandidateBlobIds.Length} Blob(s), {plan.CandidateBytes} byte(s)."
            : "storage gc: blocked because the complete Blob reference set could not be established.");
        WriteHumanIssues(output, plan.BlockingIssues);
    }

    private static void WriteGcResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        StorageGcResult result)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "storage gc");
                writer.WriteString("status", "collected");
                writer.WriteString("dataDirectory", result.DataDirectory);
                writer.WriteString("blobDirectory", result.BlobDirectory);
                writer.WriteNumber("olderThanDays", result.OlderThanDays);
                writer.WriteString("cutoffUtc", result.CutoffUtc);
                writer.WriteNumber("protectedByAgeCount", result.ProtectedByAgeCount);
                writer.WriteNumber("collectedCount", result.CollectedBlobIds.Length);
                writer.WriteNumber("collectedBytes", result.CollectedBytes);
                WriteStringArray(writer, "collectedBlobIds", result.CollectedBlobIds);
                WriteNullableString(writer, "recoveryPointPath", result.RecoveryPointPath);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine(
            $"storage gc: collected {result.CollectedBlobIds.Length} Blob(s), {result.CollectedBytes} byte(s).");
        if (result.RecoveryPointPath is not null)
        {
            output.WriteLine($"Recovery point: {result.RecoveryPointPath}");
        }
    }

    private static void WriteGcFailure(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        StorageGcException exception)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "storage gc");
                writer.WriteString("status", "error");
                writer.WriteString("message", exception.Message);
                writer.WriteNumber("collectedCount", exception.CollectedBlobIds.Length);
                WriteStringArray(writer, "collectedBlobIds", exception.CollectedBlobIds);
                WriteNullableString(writer, "recoveryPointPath", exception.RecoveryPointPath);
                writer.WriteEndObject();
            });
            return;
        }

        output.WriteLine($"storage gc: {exception.Message}");
    }

    private static void WriteError(
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

    private static void WriteGcPlanProperties(Utf8JsonWriter writer, StorageGcPlan plan)
    {
        writer.WriteString("dataDirectory", plan.DataDirectory);
        writer.WriteString("blobDirectory", plan.BlobDirectory);
        writer.WriteNumber("olderThanDays", plan.OlderThanDays);
        writer.WriteString("cutoffUtc", plan.CutoffUtc);
        writer.WriteBoolean("referenceScanComplete", plan.ReferenceScanComplete);
        writer.WriteNumber("referenceCount", plan.ReferenceCount);
        writer.WriteNumber("blobFileCount", plan.BlobFileCount);
        writer.WriteNumber("protectedByAgeCount", plan.ProtectedByAgeCount);
        writer.WriteNumber("candidateCount", plan.CandidateBlobIds.Length);
        writer.WriteNumber("candidateBytes", plan.CandidateBytes);
        WriteStringArray(writer, "candidateBlobIds", plan.CandidateBlobIds);
        WriteIssues(writer, "blockingIssues", plan.BlockingIssues);
    }

    private static void WriteIssues(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<StorageCheckIssue> issues)
    {
        writer.WriteStartArray(propertyName);
        foreach (var issue in issues)
        {
            writer.WriteStartObject();
            writer.WriteString("code", issue.Code);
            writer.WriteString("severity", issue.Severity);
            writer.WriteString("message", issue.Message);
            WriteNullableString(writer, "blobId", issue.BlobId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteHumanIssues(
        TextWriter output,
        IReadOnlyList<StorageCheckIssue> issues)
    {
        foreach (var issue in issues)
        {
            output.WriteLine(issue.BlobId is null
                ? $"  {issue.Severity}: {issue.Code}: {issue.Message}"
                : $"  {issue.Severity}: {issue.Code}: {issue.BlobId}: {issue.Message}");
        }
    }
}
