using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter>? providerAdapterFactory)
    {
        var command = new Command(
            "doctor",
            "Check configuration, Provider access, and runtime health.");
        var providerOption = CommandOptions.Create<string?>(
            "--provider",
            "Check only one Provider.");
        var fixOption = CommandOptions.Create<bool>(
            "--fix",
            "Back up data and repair recognized database issues.");
        var yesOption = CommandOptions.Create<bool>(
            "--yes",
            "Confirm repair without prompting.");
        command.Options.Add(providerOption);
        command.Options.Add(fixOption);
        command.Options.Add(yesOption);
        command.SetAction(parseResult => RunAsync(
            parseResult,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            providerOption,
            fixOption,
            yesOption,
            input,
            output,
            configDirectory,
            memoryUserHome,
            providerAdapterFactory));
        return command;
    }

    private static async Task<int> RunAsync(
        ParseResult parseResult,
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        Option<string?> providerOption,
        Option<bool> fixOption,
        Option<bool> yesOption,
        TextReader input,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter>? providerAdapterFactory)
    {
        var fix = parseResult.GetValue(fixOption);
        var yes = parseResult.GetValue(yesOption);
        var providerId = parseResult.GetValue(providerOption);
        if (yes && !fix)
        {
            WriteArgumentError(parseResult, jsonOption, output, "--yes requires --fix.");
            return ExitCodes.InvalidArguments;
        }

        if (providerId is not null && string.IsNullOrWhiteSpace(providerId))
        {
            WriteArgumentError(parseResult, jsonOption, output, "--provider must not be empty.");
            return ExitCodes.InvalidArguments;
        }

        StandaloneRuntimeContext context;
        try
        {
            context = StandaloneRuntimeContext.Create(
                parseResult.GetValue(workspaceOption),
                parseResult.GetValue(dataDirectoryOption),
                configDirectory);
        }
        catch (ArgumentException ex)
        {
            WriteArgumentError(parseResult, jsonOption, output, ex.Message);
            return ExitCodes.InvalidArguments;
        }

        var loader = new StandaloneConfigLoader(context.ConfigDirectory);
        var checks = new List<DoctorCheck>(9)
        {
            CheckRuntime()
        };
        var configInspection = await CheckPersonalConfigAsync(loader).ConfigureAwait(false);
        checks.Add(configInspection.Check);
        checks.Add(await CheckMemoryAsync(
                memoryUserHome,
                context.WorkspaceRoot)
            .ConfigureAwait(false));
        checks.Add(CheckDirectory("workspace", context.WorkspaceRoot));

        var databaseInspection = await CheckDatabaseAsync(context.DataDirectory)
            .ConfigureAwait(false);
        checks.Add(databaseInspection.Check);
        checks.Add(await CheckProviderAsync(
                providerId,
                configInspection,
                providerAdapterFactory ?? StandaloneProviderFactory.CreateAdapter)
            .ConfigureAwait(false));
        checks.Add(CheckTransport());
        checks.Add(CheckDirectory("logs", context.LogDirectory));
        checks.Add(await CheckBlobAsync(context.DataDirectory).ConfigureAwait(false));

        var isJson = CliOutput.IsJson(parseResult, jsonOption);
        if (!fix)
        {
            WriteResult(parseResult, jsonOption, output, checks, DoctorRepair.NotRequested);
            return HasErrors(checks) ? ExitCodes.GeneralError : ExitCodes.Success;
        }

        if (!isJson)
        {
            WriteHumanChecks(output, checks);
        }

        if (databaseInspection.Result is null)
        {
            WriteFixBlocked(parseResult, jsonOption, output, checks,
                "Database inspection did not produce a repair plan.");
            return ExitCodes.GeneralError;
        }

        var plan = databaseInspection.Result;
        if (plan.Healthy)
        {
            var repair = new DoctorRepair(true, "not-needed", null, [],
                "No repairable database issues were found.");
            WriteResult(parseResult, jsonOption, output, checks, repair, writeHumanChecks: false);
            return HasErrors(checks) ? ExitCodes.GeneralError : ExitCodes.Success;
        }

        if (!plan.CanRepair)
        {
            WriteFixBlocked(parseResult, jsonOption, output, checks,
                "Repair is blocked by non-repairable database issues.");
            return ExitCodes.GeneralError;
        }

        if (!isJson)
        {
            WriteHumanRepairPlan(output, plan);
        }

        if (!yes)
        {
            if (isJson)
            {
                CliOutput.WriteFailure(
                    output,
                    "ConfirmationRequired",
                    "--yes is required for doctor --fix with --json.",
                    writer => WriteFailureData(writer, checks, plan));
                return ExitCodes.InvalidArguments;
            }

            output.Write("Apply this repair plan? [y/N] ");
            var answer = await input.ReadLineAsync().ConfigureAwait(false);
            if (!IsConfirmed(answer))
            {
                output.WriteLine("No changes were made.");
                return ExitCodes.UserInterrupted;
            }
        }

        try
        {
            var result = await DatabaseMaintenanceService.RepairAsync(
                    context.WorkspaceRoot,
                    context.DataDirectory)
                .ConfigureAwait(false);
            ReplaceCheck(checks, CreateDatabaseCheck(result.After));
            ReplaceCheck(checks, await CheckBlobAsync(context.DataDirectory).ConfigureAwait(false));
            var repair = new DoctorRepair(
                true,
                "repaired",
                result.BackupPath,
                result.RepairedSessionIds,
                $"Repaired {result.RepairedSessionIds.Length} Session(s).");
            WriteResult(parseResult, jsonOption, output, checks, repair, writeHumanChecks: false);
            return HasErrors(checks) ? ExitCodes.GeneralError : ExitCodes.Success;
        }
        catch (DatabaseMaintenanceLockException ex)
        {
            WriteFixFailure(parseResult, jsonOption, output, checks, ex.Message, null);
            return ExitCodes.WorkspaceError;
        }
        catch (DatabaseRepairException ex)
        {
            WriteFixFailure(
                parseResult,
                jsonOption,
                output,
                checks,
                ex.Message,
                ex.BackupPath);
            return ExitCodes.GeneralError;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            WriteFixFailure(parseResult, jsonOption, output, checks, ex.Message, null);
            return ExitCodes.GeneralError;
        }
    }

    private static DoctorCheck CheckRuntime()
    {
        var supported = Environment.Version.Major >= 10;
        return new DoctorCheck(
            "runtime",
            supported ? "ok" : "error",
            $".NET {Environment.Version} ({Environment.Version.Major switch
            {
                >= 10 => "supported",
                _ => "requires .NET 10 or later"
            }})");
    }

    private static async Task<ConfigInspection> CheckPersonalConfigAsync(
        StandaloneConfigLoader loader)
    {
        try
        {
            var config = await loader.LoadAsync().ConfigureAwait(false);
            if (config is null)
            {
                return new ConfigInspection(
                    new DoctorCheck(
                        "personal-config",
                        "error",
                        $"Configuration '{loader.ConfigPath}' was not found."),
                    null,
                    false);
            }

            var agents = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
            var errors = StandaloneConfigValidator.Validate(config, loader.ConfigPath, agents);
            if (errors.Count > 0)
            {
                return new ConfigInspection(
                    new DoctorCheck(
                        "personal-config",
                        "error",
                        $"{errors.Count} validation error(s): {Redact(errors[0].ToString(), config)}"),
                    config,
                    false);
            }

            return new ConfigInspection(
                new DoctorCheck(
                    "personal-config",
                    "ok",
                    $"{config.Providers.Count} Provider(s), {agents.Count} Agent(s)."),
                config,
                true);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            return new ConfigInspection(
                new DoctorCheck("personal-config", "error", ex.Message),
                null,
                false);
        }
    }

    private static async Task<DoctorCheck> CheckMemoryAsync(
        string? memoryUserHome,
        string workspaceRoot)
    {
        var userHome = memoryUserHome
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
        {
            return new DoctorCheck(
                "memory",
                "error",
                "The current user profile directory is unavailable.");
        }

        try
        {
            var memory = new MemoryFileService(userHome, workspaceRoot);
            var global = await memory.ReadSnapshotAsync(MemoryScope.Global)
                .ConfigureAwait(false);
            var project = await memory.ReadSnapshotAsync(MemoryScope.Project)
                .ConfigureAwait(false);
            var missing = (global is null ? 1 : 0) + (project is null ? 1 : 0);
            return new DoctorCheck(
                "memory",
                missing == 0 ? "ok" : "warning",
                missing == 0
                    ? "Global and project memory files are readable."
                    : $"{missing} optional memory file(s) are not initialized.");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return new DoctorCheck("memory", "error", ex.Message);
        }
    }

    private static async Task<DatabaseInspection> CheckDatabaseAsync(string dataDirectory)
    {
        try
        {
            var result = await DatabaseMaintenanceService.CheckAsync(dataDirectory)
                .ConfigureAwait(false);
            return new DatabaseInspection(CreateDatabaseCheck(result), result);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            return new DatabaseInspection(
                new DoctorCheck("data", "error", ex.Message),
                null);
        }
    }

    private static DoctorCheck CreateDatabaseCheck(DatabaseCheckResult result)
    {
        var schema = result.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var detail = result.Healthy
            ? $"Database and messages are consistent; schema {schema}/{result.SupportedSchemaVersion}."
            : $"{result.Issues.Length} issue(s); {result.RepairableIssueCount} repairable; schema {schema}/{result.SupportedSchemaVersion}.";
        return new DoctorCheck("data", result.Healthy ? "ok" : "error", detail);
    }

    private static async Task<DoctorCheck> CheckProviderAsync(
        string? requestedProviderId,
        ConfigInspection configInspection,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory)
    {
        if (configInspection.Config is null)
        {
            return new DoctorCheck(
                "provider",
                "error",
                "Provider check is blocked because personal configuration is unavailable.");
        }

        var config = configInspection.Config;
        if (!configInspection.Valid)
        {
            return new DoctorCheck(
                "provider",
                "error",
                "Provider check is blocked because personal configuration is invalid.");
        }

        var providerId = requestedProviderId ?? config.DefaultProvider;
        var provider = config.Providers.FirstOrDefault(entry =>
            entry.Name.Equals(providerId, StringComparison.Ordinal));
        if (provider is null)
        {
            return new DoctorCheck(
                "provider",
                "error",
                $"Provider '{providerId}' is not configured.");
        }

        IRuntimeProviderAdapter? adapter = null;
        try
        {
            adapter = providerAdapterFactory(config, providerId);
            var probe = await adapter.ProbeCapabilitiesAsync().ConfigureAwait(false);
            return new DoctorCheck(
                "provider",
                "ok",
                $"Provider '{providerId}' is reachable; source {probe.Source}; configuration {probe.ConfigurationVersion}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DoctorCheck(
                "provider",
                "error",
                $"Provider '{providerId}' probe failed: {Redact(ex.Message, config)}");
        }
        finally
        {
            if (adapter is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (adapter is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static DoctorCheck CheckTransport() =>
        new(
            "transport",
            "ok",
            OperatingSystem.IsWindows()
                ? "Named Pipe transport is available."
                : "Unix domain socket transport is available.");

    private static async Task<DoctorCheck> CheckBlobAsync(string dataDirectory)
    {
        try
        {
            var result = await StorageMaintenanceService.CheckAsync(dataDirectory)
                .ConfigureAwait(false);
            var status = result.Issues.Any(static issue =>
                issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
                    ? "error"
                    : result.Issues.Length > 0
                        ? "warning"
                        : "ok";
            return new DoctorCheck(
                "blob",
                status,
                result.Healthy
                    ? $"{result.ReferenceCount} reference(s), {result.BlobFileCount} Blob file(s)."
                    : $"{result.Issues.Length} issue(s), including {result.OrphanCount} orphan Blob(s).");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            return new DoctorCheck("blob", "error", ex.Message);
        }
    }

    private static DoctorCheck CheckDirectory(string name, string path)
    {
        var probe = ProbeDirectory(path);
        return new DoctorCheck(name, probe.Status, probe.Detail);
    }

    private static DirectoryProbe ProbeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var existingDirectory = Directory.Exists(fullPath)
            ? fullPath
            : FindExistingAncestor(fullPath);
        if (existingDirectory is null)
        {
            return new DirectoryProbe("error", $"No existing parent directory for '{fullPath}'.");
        }

        var temporaryDirectory = Path.Combine(
            existingDirectory,
            ".madorin-doctor-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var testFile = Path.Combine(temporaryDirectory, "write-test");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            Directory.Delete(temporaryDirectory);
            return new DirectoryProbe(
                "ok",
                Directory.Exists(fullPath)
                    ? $"'{fullPath}' is readable and writable."
                    : $"'{fullPath}' can be created.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new DirectoryProbe("error", $"Access denied for '{fullPath}': {ex.Message}");
        }
        catch (IOException ex)
        {
            return new DirectoryProbe("error", $"I/O check failed for '{fullPath}': {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The diagnostic result already reports the primary access failure.
            }
        }
    }

    private static string? FindExistingAncestor(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null && !current.Exists)
        {
            current = current.Parent;
        }

        return current?.FullName;
    }

    private static bool IsConfirmed(string? answer) =>
        string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(IReadOnlyList<DoctorCheck> checks) =>
        checks.Any(static check => check.Status == "error");

    private static void ReplaceCheck(List<DoctorCheck> checks, DoctorCheck replacement)
    {
        var index = checks.FindIndex(check => check.Name == replacement.Name);
        if (index >= 0)
        {
            checks[index] = replacement;
        }
    }

    private static void WriteResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        IReadOnlyList<DoctorCheck> checks,
        DoctorRepair repair,
        bool writeHumanChecks = true)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            if (writeHumanChecks)
            {
                WriteHumanChecks(output, checks);
            }

            if (repair.Requested)
            {
                output.WriteLine($"doctor fix: {repair.Message}");
                if (repair.BackupPath is not null)
                {
                    output.WriteLine($"Recovery point: {repair.BackupPath}");
                }
            }

            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("command", "doctor");
            writer.WriteString("status", HasErrors(checks) ? "issues" : "ok");
            WriteChecks(writer, checks);
            WriteRepair(writer, repair);
            writer.WriteEndObject();
        });
    }

    private static void WriteHumanChecks(
        TextWriter output,
        IReadOnlyList<DoctorCheck> checks)
    {
        foreach (var check in checks)
        {
            output.WriteLine($"{check.Name}: {check.Status} - {check.Detail}");
        }
    }

    private static void WriteHumanRepairPlan(TextWriter output, DatabaseCheckResult plan)
    {
        var sessionCount = plan.Issues
            .Where(static issue => issue.Repairable && issue.SessionId is not null)
            .Select(static issue => issue.SessionId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        output.WriteLine(
            $"doctor repair plan: {plan.RepairableIssueCount} issue(s) across {sessionCount} Session(s); a recovery point will be created first.");
        foreach (var issue in plan.Issues)
        {
            output.WriteLine(
                $"  {issue.Severity}: {issue.Code}: {issue.Message}");
        }
    }

    private static void WriteArgumentError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string message)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteFailure(output, "InvalidArguments", message);
        }
        else
        {
            output.WriteLine($"doctor: {message}");
        }
    }

    private static void WriteFixBlocked(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        IReadOnlyList<DoctorCheck> checks,
        string message)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteFailure(output, "RepairBlocked", message, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "doctor");
                WriteChecks(writer, checks);
                writer.WriteEndObject();
            });
        }
        else
        {
            output.WriteLine($"doctor fix: {message}");
        }
    }

    private static void WriteFixFailure(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        IReadOnlyList<DoctorCheck> checks,
        string message,
        string? backupPath)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteFailure(output, "RepairFailed", message, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", "doctor");
                WriteChecks(writer, checks);
                WriteNullableString(writer, "backupPath", backupPath);
                writer.WriteEndObject();
            });
        }
        else
        {
            output.WriteLine($"doctor fix: {message}");
        }
    }

    private static void WriteFailureData(
        System.Text.Json.Utf8JsonWriter writer,
        IReadOnlyList<DoctorCheck> checks,
        DatabaseCheckResult plan)
    {
        writer.WriteStartObject();
        writer.WriteString("command", "doctor");
        WriteChecks(writer, checks);
        writer.WriteNumber("repairableIssueCount", plan.RepairableIssueCount);
        writer.WriteEndObject();
    }

    private static void WriteChecks(
        System.Text.Json.Utf8JsonWriter writer,
        IReadOnlyList<DoctorCheck> checks)
    {
        writer.WriteStartArray("checks");
        foreach (var check in checks)
        {
            writer.WriteStartObject();
            writer.WriteString("name", check.Name);
            writer.WriteString("status", check.Status);
            writer.WriteString("detail", check.Detail);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRepair(
        System.Text.Json.Utf8JsonWriter writer,
        DoctorRepair repair)
    {
        writer.WritePropertyName("repair");
        writer.WriteStartObject();
        writer.WriteBoolean("requested", repair.Requested);
        writer.WriteString("status", repair.Status);
        writer.WriteString("message", repair.Message);
        WriteNullableString(writer, "backupPath", repair.BackupPath);
        writer.WriteStartArray("repairedSessionIds");
        foreach (var sessionId in repair.RepairedSessionIds)
        {
            writer.WriteStringValue(sessionId);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNullableString(
        System.Text.Json.Utf8JsonWriter writer,
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

    private static string Redact(string message, StandaloneConfig config)
    {
        var redacted = message;
        foreach (var provider in config.Providers)
        {
            if (!string.IsNullOrEmpty(provider.ApiKey))
            {
                redacted = redacted.Replace(provider.ApiKey, "***", StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    private sealed record DoctorCheck(string Name, string Status, string Detail);

    private sealed record ConfigInspection(
        DoctorCheck Check,
        StandaloneConfig? Config,
        bool Valid);

    private sealed record DatabaseInspection(
        DoctorCheck Check,
        DatabaseCheckResult? Result);

    private sealed record DirectoryProbe(string Status, string Detail);

    private sealed record DoctorRepair(
        bool Requested,
        string Status,
        string? BackupPath,
        string[] RepairedSessionIds,
        string Message)
    {
        public static DoctorRepair NotRequested { get; } =
            new(false, "not-requested", null, [], "No repair was requested.");
    }
}
