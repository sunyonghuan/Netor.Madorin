using System.CommandLine;
using System.Data.Common;
using System.Globalization;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class SessionCommands
{
    private const int DefaultListLimit = 20;
    private const int MaximumListLimit = 200;
    private const int MessagePageSize = 500;

    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory)
    {
        var command = new Command("session", "Manage persisted Sessions.");
        command.Subcommands.Add(CreateListCommand(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome));
        command.Subcommands.Add(CreateShowCommand(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome));
        command.Subcommands.Add(CreateExportCommand(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome));
        command.Subcommands.Add(CreateDeleteCommand(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome));
        command.Subcommands.Add(CreateCompactCommand(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome,
            providerResolverFactory));
        command.Subcommands.Add(CreateStatusCommand(
            "archive",
            "Archive a Session.",
            SessionStatus.Archived,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome));
        command.Subcommands.Add(CreateStatusCommand(
            "unarchive",
            "Restore an archived Session.",
            SessionStatus.Active,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            configDirectory,
            memoryUserHome));
        return command;
    }

    private static Command CreateListCommand(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome)
    {
        var statusOption = CommandOptions.Create<string?>(
            "--status",
            "Filter active, archived, or all Sessions. Default: active.");
        var modeOption = CommandOptions.Create<string?>(
            "--mode",
            "Filter by expert, meeting, or work mode.");
        var sinceOption = CommandOptions.Create<string?>(
            "--since",
            "Filter by a timezone-qualified ISO 8601 timestamp.");
        var searchOption = CommandOptions.Create<string?>(
            "--search",
            "Filter by Session title keyword.");
        var limitOption = CommandOptions.Create<int?>(
            "--limit",
            $"Limit the result count. Default: {DefaultListLimit}.");
        var cursorOption = CommandOptions.Create<string?>(
            "--cursor",
            "Continue cursor-based pagination.");
        var command = new Command("list", "List Sessions.")
            .AddOptions(
                statusOption,
                modeOption,
                sinceOption,
                searchOption,
                limitOption,
                cursorOption);

        command.SetAction(async parseResult =>
        {
            if (!TryCreateListParameters(
                    parseResult,
                    statusOption,
                    modeOption,
                    sinceOption,
                    searchOption,
                    limitOption,
                    cursorOption,
                    out var parameters,
                    out var validationError))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionFilter",
                    validationError);
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
                await using var runtime = await StartQueryRuntimeAsync(context, memoryUserHome)
                    .ConfigureAwait(false);
                var result = await runtime.ListSessionsAsync(parameters).ConfigureAwait(false);
                WriteListResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionFilter",
                    ex.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (IsWorkspaceOrDataException(ex))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionDataUnavailable",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
        });

        return command;
    }

    private static Command CreateShowCommand(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome)
    {
        var sessionIdArgument = new Argument<string>("sessionId");
        var messagesOption = CommandOptions.Create<bool>(
            "--messages",
            "Include message metadata summaries.");
        var command = new Command("show", "Show Session details.")
            .AddOptions(messagesOption);
        command.Arguments.Add(sessionIdArgument);
        command.SetAction(async parseResult =>
        {
            var sessionId = parseResult.GetValue(sessionIdArgument);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionId",
                    "Session id must not be empty.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
                await using var runtime = await StartQueryRuntimeAsync(context, memoryUserHome)
                    .ConfigureAwait(false);
                var snapshot = await runtime.GetSessionSnapshotAsync(sessionId).ConfigureAwait(false);
                if (snapshot is null)
                {
                    WriteFailure(
                        parseResult,
                        jsonOption,
                        output,
                        "SessionNotFound",
                        $"Session '{sessionId}' was not found.");
                    return ExitCodes.InvalidArguments;
                }

                var selection = snapshot.SelectionVersion is null
                    ? null
                    : await runtime.GetSessionSelectionAsync(sessionId).ConfigureAwait(false);
                var messages = parseResult.GetValue(messagesOption)
                    ? await ListAllMessagesAsync(runtime, sessionId).ConfigureAwait(false)
                    : null;
                WriteShowResult(
                    parseResult,
                    jsonOption,
                    output,
                    snapshot,
                    selection,
                    messages);
                return ExitCodes.Success;
            }
            catch (KeyNotFoundException)
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionNotFound",
                    $"Session '{sessionId}' was not found.");
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionId",
                    ex.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (IsWorkspaceOrDataException(ex))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionDataUnavailable",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
        });

        return command;
    }

    private static Command CreateExportCommand(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome)
    {
        var sessionIdArgument = new Argument<string>("sessionId");
        var formatOption = CommandOptions.Create<string?>(
            "--format",
            "Select markdown, txt, or jsonl. Default: markdown.");
        var outputOption = CommandOptions.Create<string?>(
            "--output",
            "Write atomically to a file instead of stdout.");
        var includeReasoningOption = CommandOptions.Create<bool>(
            "--include-reasoning",
            "Include reasoning details.");
        var includeToolCallsOption = CommandOptions.Create<bool>(
            "--include-tool-calls",
            "Include tool call and result details.");
        var command = new Command("export", "Export a Session.")
            .AddOptions(
                formatOption,
                outputOption,
                includeReasoningOption,
                includeToolCallsOption);
        command.Arguments.Add(sessionIdArgument);
        command.SetAction(async parseResult =>
        {
            var sessionId = parseResult.GetValue(sessionIdArgument);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionId",
                    "Session id must not be empty.");
                return ExitCodes.InvalidArguments;
            }

            var formatValue = parseResult.GetValue(formatOption);
            if (!SessionExportWriter.TryParseFormat(formatValue, out var format))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionExportFormat",
                    "Format must be markdown, txt, or jsonl.");
                return ExitCodes.InvalidArguments;
            }

            var outputPath = parseResult.GetValue(outputOption);
            if (outputPath is not null && string.IsNullOrWhiteSpace(outputPath))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionExportOutput",
                    "Output path must not be empty.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
                await using var runtime = await StartQueryRuntimeAsync(context, memoryUserHome)
                    .ConfigureAwait(false);
                var snapshot = await runtime.GetSessionSnapshotAsync(sessionId).ConfigureAwait(false);
                if (snapshot is null)
                {
                    WriteFailure(
                        parseResult,
                        jsonOption,
                        output,
                        "SessionNotFound",
                        $"Session '{sessionId}' was not found.");
                    return ExitCodes.InvalidArguments;
                }

                var records = await runtime.ReadSessionHistoryAsync(sessionId).ConfigureAwait(false);
                var includeReasoning = parseResult.GetValue(includeReasoningOption);
                var includeToolCalls = parseResult.GetValue(includeToolCallsOption);
                if (outputPath is null)
                {
                    await SessionExportWriter.WriteAsync(
                            output,
                            snapshot,
                            records,
                            format,
                            includeReasoning,
                            includeToolCalls)
                        .ConfigureAwait(false);
                }
                else
                {
                    await using var outputFile = AtomicOutputFile.Create(outputPath);
                    await SessionExportWriter.WriteAsync(
                            outputFile.Writer,
                            snapshot,
                            records,
                            format,
                            includeReasoning,
                            includeToolCalls)
                        .ConfigureAwait(false);
                    await outputFile.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }
            catch (KeyNotFoundException)
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionNotFound",
                    $"Session '{sessionId}' was not found.");
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (IsSessionExportFailure(ex))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionExportFailed",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
        });

        return command;
    }

    private static async Task<LocalRuntime> StartQueryRuntimeAsync(
        StandaloneRuntimeContext context,
        string? memoryUserHome) =>
        await LocalRuntime.StartAsync(new LocalRuntimeOptions(
            context.WorkspaceRoot,
            context.DataDirectory,
            context.LogDirectory,
            context.RuntimeInstanceId,
            static _ => throw new InvalidOperationException(
                "Provider resolution is unavailable for Session queries."))
        {
            MemoryUserHome = memoryUserHome
        }).ConfigureAwait(false);

    private static Command CreateCompactCommand(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory)
    {
        var sessionIdArgument = new Argument<string>("sessionId");
        var strategyOption = CommandOptions.Create<string?>(
            "--strategy",
            "Select full, sliding-window, or summary. Default: summary.");
        var providerOption = CommandOptions.Create<string?>(
            "--provider",
            "Temporarily override the Session Provider.");
        var modelOption = CommandOptions.Create<string?>(
            "--model",
            "Temporarily override the Session model.");
        var keepLastTokensOption = CommandOptions.Create<int?>(
            "--keep-last-tokens",
            "Set the sliding-window token budget.");
        var dryRunOption = CommandOptions.Create<bool>(
            "--dry-run",
            "Show the projection plan without generating a summary or writing cache.");
        var forceOption = CommandOptions.Create<bool>(
            "--force",
            "Regenerate even when the projection cache is current.");
        var command = new Command("compact", "Compact Session context.")
            .AddOptions(
                strategyOption,
                providerOption,
                modelOption,
                keepLastTokensOption,
                dryRunOption,
                forceOption);
        command.Arguments.Add(sessionIdArgument);
        command.SetAction(async parseResult =>
        {
            var sessionId = parseResult.GetValue(sessionIdArgument);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionId",
                    "Session id must not be empty.");
                return ExitCodes.InvalidArguments;
            }

            if (!TryCreateCompactionOptions(
                    parseResult,
                    strategyOption,
                    providerOption,
                    modelOption,
                    keepLastTokensOption,
                    dryRunOption,
                    forceOption,
                    out var options,
                    out var optionsError))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidCompactionOptions",
                    optionsError);
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
                await using var runtime = await StartCompactionRuntimeAsync(
                        context,
                        memoryUserHome,
                        providerResolverFactory)
                    .ConfigureAwait(false);
                var result = await runtime.CompactSessionAsync(sessionId, options)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    WriteFailure(
                        parseResult,
                        jsonOption,
                        output,
                        "SessionNotFound",
                        $"Session '{sessionId}' was not found.");
                    return ExitCodes.InvalidArguments;
                }

                WriteCompactionResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (KeyNotFoundException ex)
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidCompactionSelection",
                    ex.Message);
                return ExitCodes.InvalidArguments;
            }
            catch (Exception ex) when (IsSessionCompactionFailure(ex))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionCompactionFailed",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
        });
        return command;
    }

    private static async Task<LocalRuntime> StartCompactionRuntimeAsync(
        StandaloneRuntimeContext context,
        string? memoryUserHome,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory)
    {
        var loader = new StandaloneConfigLoader(context.ConfigDirectory);
        var config = await loader.LoadAsync().ConfigureAwait(false)
            ?? throw new InvalidDataException($"Configuration not found: {loader.ConfigPath}");
        var agentDocuments = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
        var validationErrors = StandaloneConfigValidator.Validate(
            config,
            loader.ConfigPath,
            agentDocuments);
        if (validationErrors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validationErrors));
        }

        var providerResolver = providerResolverFactory is null
            ? StandaloneProviderFactory.Create(config)
            : providerResolverFactory(config);
        return await LocalRuntime.StartAsync(new LocalRuntimeOptions(
            context.WorkspaceRoot,
            context.DataDirectory,
            context.LogDirectory,
            context.RuntimeInstanceId,
            providerResolver)
        {
            MemoryUserHome = memoryUserHome
        }).ConfigureAwait(false);
    }

    private static Command CreateDeleteCommand(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome)
    {
        var sessionIdArgument = new Argument<string>("sessionId");
        var confirmOption = CommandOptions.Create<bool>(
            "--confirm",
            "Confirm deletion.");
        var includeBlobsOption = CommandOptions.Create<bool>(
            "--include-blobs",
            "Also remove Blobs that are not referenced by another Session.");
        var command = new Command("delete", "Delete a Session.")
            .AddOptions(confirmOption, includeBlobsOption);
        command.Arguments.Add(sessionIdArgument);
        command.SetAction(async parseResult =>
        {
            var sessionId = parseResult.GetValue(sessionIdArgument);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionId",
                    "Session id must not be empty.");
                return ExitCodes.InvalidArguments;
            }

            if (!parseResult.GetValue(confirmOption))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "ConfirmationRequired",
                    "Session deletion requires --confirm.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
                await using var runtime = await StartQueryRuntimeAsync(context, memoryUserHome)
                    .ConfigureAwait(false);
                var result = await runtime.DeleteSessionAsync(
                        sessionId,
                        parseResult.GetValue(includeBlobsOption))
                    .ConfigureAwait(false);
                if (result is null)
                {
                    WriteFailure(
                        parseResult,
                        jsonOption,
                        output,
                        "SessionNotFound",
                        $"Session '{sessionId}' was not found.");
                    return ExitCodes.InvalidArguments;
                }

                WriteDeleteResult(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (IsSessionDeleteFailure(ex))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionDeleteFailed",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
        });
        return command;
    }

    private static Command CreateStatusCommand(
        string name,
        string description,
        SessionStatus targetStatus,
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        string? configDirectory,
        string? memoryUserHome)
    {
        var sessionIdArgument = new Argument<string>("sessionId");
        var command = new Command(name, description);
        command.Arguments.Add(sessionIdArgument);
        command.SetAction(async parseResult =>
        {
            var sessionId = parseResult.GetValue(sessionIdArgument);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "InvalidSessionId",
                    "Session id must not be empty.");
                return ExitCodes.InvalidArguments;
            }

            try
            {
                var context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
                await using var runtime = await StartQueryRuntimeAsync(context, memoryUserHome)
                    .ConfigureAwait(false);
                if (!await runtime.TrySetSessionStatusAsync(sessionId, targetStatus)
                        .ConfigureAwait(false))
                {
                    WriteFailure(
                        parseResult,
                        jsonOption,
                        output,
                        "SessionNotFound",
                        $"Session '{sessionId}' was not found.");
                    return ExitCodes.InvalidArguments;
                }

                WriteStatusResult(parseResult, jsonOption, output, sessionId, targetStatus);
                return ExitCodes.Success;
            }
            catch (Exception ex) when (IsSessionStatusFailure(ex))
            {
                WriteFailure(
                    parseResult,
                    jsonOption,
                    output,
                    "SessionDataUnavailable",
                    ex.Message);
                return ExitCodes.WorkspaceError;
            }
        });
        return command;
    }

    private static async Task<SessionMessageDescriptor[]> ListAllMessagesAsync(
        LocalRuntime runtime,
        string sessionId)
    {
        var messages = new List<SessionMessageDescriptor>();
        long? cursor = null;
        do
        {
            var page = await runtime.ListSessionMessagesAsync(
                    new SessionMessagesListParameters(sessionId, cursor, MessagePageSize))
                .ConfigureAwait(false);
            messages.AddRange(page.Messages);
            if (page.NextCursor == cursor && cursor is not null)
            {
                throw new InvalidDataException("Session message pagination did not advance.");
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return [.. messages];
    }

    private static bool TryCreateCompactionOptions(
        ParseResult parseResult,
        Option<string?> strategyOption,
        Option<string?> providerOption,
        Option<string?> modelOption,
        Option<int?> keepLastTokensOption,
        Option<bool> dryRunOption,
        Option<bool> forceOption,
        out SessionCompactionOptions options,
        out string error)
    {
        options = new SessionCompactionOptions();
        error = string.Empty;
        var strategy = parseResult.GetValue(strategyOption)?.ToLowerInvariant() switch
        {
            null or "summary" => SessionCompactionStrategy.Summary,
            "full" => SessionCompactionStrategy.Full,
            "sliding-window" => SessionCompactionStrategy.SlidingWindow,
            _ => (SessionCompactionStrategy?)null
        };
        if (strategy is null)
        {
            error = "Strategy must be full, sliding-window, or summary.";
            return false;
        }

        var providerId = parseResult.GetValue(providerOption);
        if (providerId is not null && string.IsNullOrWhiteSpace(providerId))
        {
            error = "Provider must not be whitespace-only.";
            return false;
        }

        var modelId = parseResult.GetValue(modelOption);
        if (modelId is not null && string.IsNullOrWhiteSpace(modelId))
        {
            error = "Model must not be whitespace-only.";
            return false;
        }

        var keepLastTokens = parseResult.GetValue(keepLastTokensOption);
        if (keepLastTokens is <= 0)
        {
            error = "Keep-last-tokens must be greater than zero.";
            return false;
        }

        if (keepLastTokens is not null
            && strategy is not SessionCompactionStrategy.SlidingWindow)
        {
            error = "Keep-last-tokens can only be used with sliding-window.";
            return false;
        }

        options = new SessionCompactionOptions(
            strategy.Value,
            providerId,
            modelId,
            keepLastTokens,
            parseResult.GetValue(dryRunOption),
            parseResult.GetValue(forceOption));
        return true;
    }

    private static bool TryCreateListParameters(
        ParseResult parseResult,
        Option<string?> statusOption,
        Option<string?> modeOption,
        Option<string?> sinceOption,
        Option<string?> searchOption,
        Option<int?> limitOption,
        Option<string?> cursorOption,
        out SessionListParameters parameters,
        out string error)
    {
        parameters = new SessionListParameters();
        error = string.Empty;

        if (!TryParseMode(parseResult.GetValue(modeOption), out var mode))
        {
            error = "Mode must be expert, meeting, or work.";
            return false;
        }

        if (!TryParseStatus(parseResult.GetValue(statusOption), out var status))
        {
            error = "Status must be active, archived, or all.";
            return false;
        }

        var sinceValue = parseResult.GetValue(sinceOption);
        DateTimeOffset? since = null;
        if (sinceValue is not null)
        {
            if (!HasExplicitTimeZone(sinceValue)
                || !DateTimeOffset.TryParse(
                    sinceValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedSince))
            {
                error = "Since must be a timezone-qualified ISO 8601 timestamp.";
                return false;
            }

            since = parsedSince;
        }

        var limit = parseResult.GetValue(limitOption) ?? DefaultListLimit;
        if (limit is < 1 or > MaximumListLimit)
        {
            error = $"Limit must be between 1 and {MaximumListLimit}.";
            return false;
        }

        var search = parseResult.GetValue(searchOption);
        if (search is not null && string.IsNullOrWhiteSpace(search))
        {
            error = "Search must not be whitespace-only.";
            return false;
        }

        var cursor = parseResult.GetValue(cursorOption);
        if (cursor is not null && string.IsNullOrWhiteSpace(cursor))
        {
            error = "Cursor must not be whitespace-only.";
            return false;
        }

        parameters = new SessionListParameters(mode, status, since, search, limit, cursor);
        return true;
    }

    private static bool TryParseMode(string? value, out RuntimeMode? mode)
    {
        mode = null;
        if (value is null)
        {
            return true;
        }

        mode = value.ToLowerInvariant() switch
        {
            "expert" => RuntimeMode.Expert,
            "meeting" => RuntimeMode.Meeting,
            "work" => RuntimeMode.Work,
            _ => null
        };
        return mode is not null;
    }

    private static bool TryParseStatus(string? value, out SessionStatus? status)
    {
        switch (value?.ToLowerInvariant())
        {
            case null:
            case "active":
                status = SessionStatus.Active;
                return true;
            case "archived":
                status = SessionStatus.Archived;
                return true;
            case "all":
                status = null;
                return true;
            default:
                status = null;
                return false;
        }
    }

    private static bool HasExplicitTimeZone(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
        {
            return true;
        }

        if (value.Length < 6)
        {
            return false;
        }

        var offsetStart = value.Length - 6;
        return value[offsetStart] is '+' or '-'
            && value[offsetStart + 3] == ':'
            && char.IsAsciiDigit(value[offsetStart + 1])
            && char.IsAsciiDigit(value[offsetStart + 2])
            && char.IsAsciiDigit(value[offsetStart + 4])
            && char.IsAsciiDigit(value[offsetStart + 5]);
    }

    private static bool IsWorkspaceOrDataException(Exception exception) =>
        exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or DbException
            or FormatException
            or System.Text.Json.JsonException;

    private static bool IsSessionExportFailure(Exception exception) =>
        IsWorkspaceOrDataException(exception)
        || exception is ArgumentException
            or NotSupportedException;

    private static bool IsSessionStatusFailure(Exception exception) =>
        IsWorkspaceOrDataException(exception)
        || exception is ArgumentException
            or NotSupportedException;

    private static bool IsSessionDeleteFailure(Exception exception) =>
        IsWorkspaceOrDataException(exception)
        || exception is ArgumentException
            or NotSupportedException;

    private static bool IsSessionCompactionFailure(Exception exception) =>
        IsWorkspaceOrDataException(exception)
        || exception is ArgumentException
            or NotSupportedException
            or HttpRequestException
            or TimeoutException;

    private static void WriteFailure(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string code,
        string message)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine(message);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static void WriteListResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        SessionListResult result)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            WriteHumanList(output, result);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("sessions");
            foreach (var session in result.Sessions)
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WriteString("mode", ToWireValue(session.Mode));
                writer.WriteString("status", ToWireValue(session.Status));
                writer.WriteString("updatedAt", session.UpdatedAt);
                WriteNullableString(writer, "title", session.Title);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteNullableString(writer, "nextCursor", result.NextCursor);
            writer.WriteEndObject();
        });
    }

    private static void WriteStatusResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string sessionId,
        SessionStatus status)
    {
        var wireStatus = ToWireValue(status);
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"Session '{sessionId}' is now {wireStatus}.");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("status", wireStatus);
            writer.WriteEndObject();
        });
    }

    private static void WriteDeleteResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        SessionDeletionResult result)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"Session '{result.SessionId}' was deleted.");
            output.WriteLine($"Recovery point: {result.RecoveryPoint}");
            if (result.IncludeBlobs)
            {
                output.WriteLine($"Unshared Blobs removed: {result.DeletedBlobCount}");
            }

            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", result.SessionId);
            writer.WriteBoolean("deleted", true);
            writer.WriteBoolean("includeBlobs", result.IncludeBlobs);
            writer.WriteNumber("deletedBlobCount", result.DeletedBlobCount);
            writer.WriteString("recoveryPoint", result.RecoveryPoint);
            writer.WriteEndObject();
        });
    }

    private static void WriteCompactionResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        SessionCompactionResult result)
    {
        var strategy = result.Strategy switch
        {
            SessionCompactionStrategy.Full => "full",
            SessionCompactionStrategy.SlidingWindow => "sliding-window",
            SessionCompactionStrategy.Summary => "summary",
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            output.WriteLine($"Session: {result.SessionId}");
            output.WriteLine($"Strategy: {strategy}");
            output.WriteLine($"Selection: {result.ProviderId}/{result.ModelId}");
            output.WriteLine(
                $"Messages: {result.SourceMessageCount} -> {result.ProjectedMessageCount}");
            output.WriteLine($"Before tokens: {result.BeforeEstimatedTokens}");
            output.WriteLine(result.AfterEstimatedTokens is { } after
                ? $"After tokens: {after}"
                : "After tokens: pending summary generation");
            output.WriteLine(result.DryRun
                ? "Projection cache: dry-run"
                : result.CacheHit
                    ? "Projection cache: reused"
                    : "Projection cache: updated");
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", result.SessionId);
            writer.WriteString("strategy", strategy);
            writer.WriteString("providerId", result.ProviderId);
            writer.WriteString("modelId", result.ModelId);
            if (result.KeepLastTokens is { } keepLastTokens)
            {
                writer.WriteNumber("keepLastTokens", keepLastTokens);
            }
            else
            {
                writer.WriteNull("keepLastTokens");
            }

            writer.WriteNumber("sourceMessageCount", result.SourceMessageCount);
            writer.WriteNumber("projectedMessageCount", result.ProjectedMessageCount);
            writer.WriteNumber("droppedMessageCount", result.DroppedMessageCount);
            writer.WriteNumber("beforeEstimatedTokens", result.BeforeEstimatedTokens);
            if (result.AfterEstimatedTokens is { } afterEstimatedTokens)
            {
                writer.WriteNumber("afterEstimatedTokens", afterEstimatedTokens);
            }
            else
            {
                writer.WriteNull("afterEstimatedTokens");
            }

            writer.WriteString("estimateSource", result.EstimateSource);
            writer.WriteBoolean("dryRun", result.DryRun);
            writer.WriteBoolean("cacheHit", result.CacheHit);
            writer.WriteBoolean("cacheWritten", result.CacheWritten);
            writer.WriteEndObject();
        });
    }

    private static void WriteHumanList(TextWriter output, SessionListResult result)
    {
        if (result.Sessions.Length == 0)
        {
            output.WriteLine("No Sessions found.");
        }
        else
        {
            output.WriteLine("SESSION ID\tMODE\tSTATUS\tUPDATED AT\tTITLE");
            foreach (var session in result.Sessions)
            {
                output.WriteLine(
                    $"{session.SessionId}\t{session.Mode.ToString().ToUpperInvariant()}\t" +
                    $"{session.Status.ToString().ToUpperInvariant()}\t{session.UpdatedAt:O}\t" +
                    $"{session.Title ?? "-"}");
            }
        }

        if (result.NextCursor is not null)
        {
            output.WriteLine($"Next cursor: {result.NextCursor}");
        }
    }

    private static void WriteShowResult(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        PersistedSessionSnapshot snapshot,
        NextTurnSelection? selection,
        SessionMessageDescriptor[]? messages)
    {
        if (!CliOutput.IsJson(parseResult, jsonOption))
        {
            WriteHumanShow(output, snapshot, selection, messages);
            return;
        }

        CliOutput.WriteJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", snapshot.SessionId);
            writer.WriteString("mode", ToWireValue(snapshot.Mode));
            writer.WriteString("status", ToWireValue(snapshot.Status));
            writer.WriteString("createdAt", snapshot.CreatedAt);
            writer.WriteString("updatedAt", snapshot.UpdatedAt);
            if (snapshot.SelectionVersion is { } selectionVersion)
            {
                writer.WriteNumber("selectionVersion", selectionVersion);
            }
            else
            {
                writer.WriteNull("selectionVersion");
            }

            writer.WriteNumber("lastGsn", snapshot.LastGsn);
            WriteSelection(writer, selection);
            WriteLatestRun(writer, snapshot.LatestRun);
            if (messages is not null)
            {
                WriteMessages(writer, messages);
            }

            writer.WriteEndObject();
        });
    }

    private static void WriteHumanShow(
        TextWriter output,
        PersistedSessionSnapshot snapshot,
        NextTurnSelection? selection,
        SessionMessageDescriptor[]? messages)
    {
        output.WriteLine($"Session: {snapshot.SessionId}");
        output.WriteLine($"Mode: {ToWireValue(snapshot.Mode)}");
        output.WriteLine($"Status: {ToWireValue(snapshot.Status)}");
        output.WriteLine($"Created: {snapshot.CreatedAt:O}");
        output.WriteLine($"Updated: {snapshot.UpdatedAt:O}");
        output.WriteLine($"Last GSN: {snapshot.LastGsn.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine(selection is null
            ? "Selection: unavailable"
            : $"Selection: {selection.DefaultSelection.ProviderId}/{selection.DefaultSelection.ModelId} " +
              $"(version {selection.SelectionVersion.ToString(CultureInfo.InvariantCulture)})");
        output.WriteLine(snapshot.LatestRun is null
            ? "Latest Run: none"
            : $"Latest Run: {snapshot.LatestRun.RunId} ({ToWireValue(snapshot.LatestRun.Status)})");

        if (messages is null)
        {
            return;
        }

        output.WriteLine("Messages:");
        foreach (var message in messages)
        {
            output.WriteLine(
                $"  {message.Sequence.ToString(CultureInfo.InvariantCulture)}\t{message.Role}\t" +
                $"agent={message.AgentId}\tinvocation={message.InvocationId}\t" +
                $"created={message.CreatedAt:O}\tmessage={message.MessageId}");
        }
    }

    private static void WriteSelection(
        System.Text.Json.Utf8JsonWriter writer,
        NextTurnSelection? selection)
    {
        writer.WritePropertyName("selection");
        if (selection is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("selectionVersion", selection.SelectionVersion);
        writer.WriteString("mode", ToWireValue(selection.Mode));
        writer.WriteString("providerId", selection.DefaultSelection.ProviderId);
        writer.WriteString("modelId", selection.DefaultSelection.ModelId);
        writer.WriteString("toolCatalogVersion", selection.ToolCatalogVersion);
        writer.WriteEndObject();
    }

    private static void WriteLatestRun(
        System.Text.Json.Utf8JsonWriter writer,
        RunSnapshot? latestRun)
    {
        writer.WritePropertyName("latestRun");
        if (latestRun is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("runId", latestRun.RunId);
        writer.WriteString("status", ToWireValue(latestRun.Status));
        writer.WriteEndObject();
    }

    private static void WriteMessages(
        System.Text.Json.Utf8JsonWriter writer,
        SessionMessageDescriptor[] messages)
    {
        writer.WriteStartArray("messages");
        foreach (var message in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("messageId", message.MessageId);
            writer.WriteNumber("sequence", message.Sequence);
            writer.WriteString("invocationId", message.InvocationId);
            writer.WriteString("agentId", message.AgentId);
            writer.WriteString("role", message.Role);
            writer.WriteString("createdAt", message.CreatedAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(
        System.Text.Json.Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static string ToWireValue<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();
}
