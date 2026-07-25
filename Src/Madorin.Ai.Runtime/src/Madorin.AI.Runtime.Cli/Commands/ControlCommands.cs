using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ControlCommands
{
    private const string CommandName = "ctl";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var command = new Command(CommandName, "Control a running Runtime instance.");
        command.Subcommands.Add(CreateStatus(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            error));
        command.Subcommands.Add(CreateSessions(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            error));
        command.Subcommands.Add(CreateRuns(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            error));
        command.Subcommands.Add(CreateCancel(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            error));
        command.Subcommands.Add(CreateCredential(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            output,
            error));
        return command;
    }

    private static Command CreateStatus(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        TextWriter error)
    {
        var instanceOption = CreateInstanceOption();
        var command = new Command("status", "Show Runtime status.")
            .AddOptions(instanceOption);
        command.SetAction(parseResult => ExecuteConnectedAsync(
            parseResult,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            instanceOption,
            output,
            error,
            (client, status, cancellationToken) =>
            {
                ControlCommandOutput.WriteStatus(parseResult, jsonOption, output, status);
                return Task.FromResult(ExitCodes.Success);
            }));
        return command;
    }

    private static Command CreateSessions(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        TextWriter error)
    {
        var instanceOption = CreateInstanceOption();
        var command = new Command("sessions", "Show active Sessions.")
            .AddOptions(instanceOption);
        command.SetAction(parseResult => ExecuteConnectedAsync(
            parseResult,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            instanceOption,
            output,
            error,
            async (client, status, cancellationToken) =>
            {
                var result = await client.ListSessionsAsync(
                    new SessionListParameters(),
                    cancellationToken).ConfigureAwait(false);
                ControlCommandOutput.WriteSessions(parseResult, jsonOption, output, result);
                return ExitCodes.Success;
            }));
        return command;
    }

    private static Command CreateRuns(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        TextWriter error)
    {
        var instanceOption = CreateInstanceOption();
        var sessionOption = CommandOptions.Create<string?>(
            "--session",
            "Only show Runs for the specified Session.");
        var command = new Command("runs", "Show active Runs.")
            .AddOptions(instanceOption, sessionOption);
        command.SetAction(parseResult =>
        {
            var sessionId = parseResult.GetValue(sessionOption);
            if (sessionId is not null && string.IsNullOrWhiteSpace(sessionId))
            {
                return WriteArgumentErrorAsync(
                    parseResult,
                    jsonOption,
                    output,
                    error,
                    "ctl runs",
                    "--session must not be empty.");
            }

            return ExecuteConnectedAsync(
                parseResult,
                jsonOption,
                workspaceOption,
                dataDirectoryOption,
                instanceOption,
                output,
                error,
                async (client, status, cancellationToken) =>
                {
                    var result = await client.ListRunsAsync(
                        new RunListParameters(sessionId),
                        cancellationToken).ConfigureAwait(false);
                    ControlCommandOutput.WriteRuns(parseResult, jsonOption, output, result);
                    return ExitCodes.Success;
                });
        });
        return command;
    }

    private static Command CreateCancel(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        TextWriter error)
    {
        var runIdArgument = new Argument<string>("runId")
        {
            Description = "The active Run identifier."
        };
        var instanceOption = CreateInstanceOption();
        var reasonOption = CommandOptions.Create<string?>(
            "--reason",
            "Record a cancellation reason in the Runtime audit trail.");
        var command = new Command("cancel", "Cancel an active Run.")
            .AddOptions(instanceOption, reasonOption);
        command.Arguments.Add(runIdArgument);
        command.SetAction(parseResult =>
        {
            var runId = parseResult.GetValue(runIdArgument);
            var reason = parseResult.GetValue(reasonOption);
            if (string.IsNullOrWhiteSpace(runId)
                || (reason is not null && string.IsNullOrWhiteSpace(reason)))
            {
                return WriteArgumentErrorAsync(
                    parseResult,
                    jsonOption,
                    output,
                    error,
                    "ctl cancel",
                    "runId and --reason, when supplied, must not be empty.");
            }

            return ExecuteConnectedAsync(
                parseResult,
                jsonOption,
                workspaceOption,
                dataDirectoryOption,
                instanceOption,
                output,
                error,
                async (client, status, cancellationToken) =>
                {
                    var accepted = await client.CancelRunAsync(
                        runId,
                        reason,
                        cancellationToken).ConfigureAwait(false);
                    ControlCommandOutput.WriteCancel(
                        parseResult,
                        jsonOption,
                        output,
                        error,
                        runId,
                        accepted);
                    return accepted ? ExitCodes.Success : ExitCodes.GeneralError;
                });
        });
        return command;
    }

    private static Command CreateCredential(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output,
        TextWriter error)
    {
        var credential = new Command("credential", "Manage live Provider credentials.");
        var instanceOption = CreateInstanceOption();
        var runOption = CommandOptions.Create<string?>("--run", "Select the waiting Run.");
        var providerOption = CommandOptions.Create<string?>("--provider", "Select a Provider.");
        var profileOption = CommandOptions.Create<string?>("--profile", "Select a Provider profile.");
        var apiKeyEnvironmentOption = CommandOptions.Create<string?>(
            "--api-key-env",
            "Read the API key from an environment variable.");
        var expiresAtOption = CommandOptions.Create<string?>(
            "--expires-at",
            "Set an ISO 8601 expiration time with an explicit time-zone offset.");
        var update = new Command("update", "Push updated credentials.")
            .AddOptions(
                instanceOption,
                runOption,
                providerOption,
                profileOption,
                apiKeyEnvironmentOption,
                expiresAtOption);
        update.SetAction(parseResult => ExecuteCredentialUpdateAsync(
            parseResult,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            instanceOption,
            runOption,
            providerOption,
            profileOption,
            apiKeyEnvironmentOption,
            expiresAtOption,
            output,
            error));
        credential.Subcommands.Add(update);
        return credential;
    }

    private static Option<string?> CreateInstanceOption() =>
        CommandOptions.Create<string?>(
            "--instance",
            "Require the discovered Runtime instance identifier to match.");

    private static async Task<int> ExecuteCredentialUpdateAsync(
        ParseResult parseResult,
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        Option<string?> instanceOption,
        Option<string?> runOption,
        Option<string?> providerOption,
        Option<string?> profileOption,
        Option<string?> apiKeyEnvironmentOption,
        Option<string?> expiresAtOption,
        TextWriter output,
        TextWriter error)
    {
        var runId = parseResult.GetValue(runOption);
        var providerId = parseResult.GetValue(providerOption);
        var profileId = parseResult.GetValue(profileOption);
        var environmentVariable = parseResult.GetValue(apiKeyEnvironmentOption);
        var expiresAtValue = parseResult.GetValue(expiresAtOption);
        if (string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(environmentVariable)
            || (profileId is not null && string.IsNullOrWhiteSpace(profileId)))
        {
            return await WriteArgumentErrorAsync(
                parseResult,
                jsonOption,
                output,
                error,
                "ctl credential update",
                "--run, --provider, and --api-key-env are required; --profile must not be empty.")
                .ConfigureAwait(false);
        }

        DateTimeOffset? expiresAt = null;
        if (expiresAtValue is not null
            && !TryParseOffsetTimestamp(expiresAtValue, out expiresAt))
        {
            return await WriteArgumentErrorAsync(
                parseResult,
                jsonOption,
                output,
                error,
                "ctl credential update",
                "--expires-at must be an ISO 8601 timestamp with an explicit time-zone offset.")
                .ConfigureAwait(false);
        }

        var credential = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(credential))
        {
            return await WriteArgumentErrorAsync(
                parseResult,
                jsonOption,
                output,
                error,
                "ctl credential update",
                "The API key environment variable is missing or empty.",
                code: "CredentialEnvironmentVariableMissing")
                .ConfigureAwait(false);
        }

        return await ExecuteConnectedAsync(
            parseResult,
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            instanceOption,
            output,
            error,
            async (client, status, cancellationToken) =>
            {
                await client.UpdateCredentialsAsync(
                    new CredentialsUpdateParameters(
                        runId,
                        providerId,
                        credential,
                        profileId,
                        expiresAt),
                    cancellationToken).ConfigureAwait(false);
                ControlCommandOutput.WriteCredentialUpdate(
                    parseResult,
                    jsonOption,
                    output,
                    runId,
                    providerId,
                    profileId,
                    expiresAt);
                return ExitCodes.Success;
            }).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteConnectedAsync(
        ParseResult parseResult,
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        Option<string?> instanceOption,
        TextWriter output,
        TextWriter error,
        Func<RuntimeClient, RuntimeStatusResult, CancellationToken, Task<int>> operation)
    {
        var secret = Environment.GetEnvironmentVariable(
            ControlRuntimeConnection.SecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secret))
        {
            ControlCommandOutput.WriteError(
                parseResult,
                jsonOption,
                output,
                error,
                "AuthenticationRequired",
                $"{ControlRuntimeConnection.SecretEnvironmentVariable} is required.",
                isRetryable: false);
            return ExitCodes.AuthenticationFailed;
        }

        try
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            await using var connection = await ControlRuntimeConnection.OpenAsync(
                parseResult.GetValue(workspaceOption),
                parseResult.GetValue(dataDirectoryOption),
                parseResult.GetValue(instanceOption),
                secret,
                timeout.Token).ConfigureAwait(false);
            return await operation(
                connection.Client,
                connection.Status,
                timeout.Token).ConfigureAwait(false);
        }
        catch (RuntimeClientAuthenticationException ex) when (
            ControlRuntimeConnection.IsInstanceIdentityMismatch(ex))
        {
            ControlCommandOutput.WriteError(
                parseResult,
                jsonOption,
                output,
                error,
                "RuntimeInstanceMismatch",
                "The discovered Runtime endpoint did not match the recorded instance identity.",
                isRetryable: false);
            return ExitCodes.ConnectionFailed;
        }
        catch (RuntimeClientAuthenticationException)
        {
            ControlCommandOutput.WriteError(
                parseResult,
                jsonOption,
                output,
                error,
                "AuthenticationFailed",
                "Runtime authentication failed.",
                isRetryable: false);
            return ExitCodes.AuthenticationFailed;
        }
        catch (Exception ex) when (ex is RuntimeClientConnectionException
            or RuntimeClientProtocolException
            or RuntimeClientVersionIncompatibleException
            or RuntimeClientHealthException
            or InvalidDataException
            or IOException
            or JsonException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            ControlCommandOutput.WriteError(
                parseResult,
                jsonOption,
                output,
                error,
                "RuntimeConnectionFailed",
                GetConnectionErrorMessage(ex),
                isRetryable: ex is RuntimeClientException { IsRetryable: true });
            return ExitCodes.ConnectionFailed;
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            ControlCommandOutput.WriteError(
                parseResult,
                jsonOption,
                output,
                error,
                "InvalidArguments",
                ex.Message,
                isRetryable: false);
            return ExitCodes.InvalidArguments;
        }
        catch (InvalidOperationException ex)
        {
            ControlCommandOutput.WriteError(
                parseResult,
                jsonOption,
                output,
                error,
                "RuntimeRejected",
                ex.Message,
                isRetryable: false);
            return ExitCodes.GeneralError;
        }
    }

    private static string GetConnectionErrorMessage(Exception exception) => exception switch
    {
        OperationCanceledException => "The Runtime control operation timed out.",
        RuntimeClientException => exception.Message,
        InvalidDataException or JsonException => exception.Message,
        IOException or UnauthorizedAccessException =>
            "The Runtime instance metadata or control channel could not be read.",
        _ => "The Runtime control connection failed."
    };

    private static bool TryParseOffsetTimestamp(
        string value,
        out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dateTime)
            || dateTime.Kind == DateTimeKind.Unspecified
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        timestamp = parsed;
        return true;
    }

    private static Task<int> WriteArgumentErrorAsync(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        TextWriter error,
        string command,
        string message,
        string code = "InvalidArguments")
    {
        ControlCommandOutput.WriteError(
            parseResult,
            jsonOption,
            output,
            error,
            code,
            $"{command}: {message}",
            isRetryable: false);
        return Task.FromResult(ExitCodes.InvalidArguments);
    }
}
