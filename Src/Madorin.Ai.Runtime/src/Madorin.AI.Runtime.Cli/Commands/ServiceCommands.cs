using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ServiceCommands
{
    public static Command CreateServe(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var command = new Command("serve", "Start the persistent Runtime server.")
            .AddOptions(
                CommandOptions.Create<string?>("--config-dir", "Override the configuration directory."),
                CommandOptions.Create<string?>("--log-dir", "Override the log directory."),
                CommandOptions.Create<string?>("--instance", "Set the Runtime instance identifier."),
                CommandOptions.Create<string?>("--pipe-prefix", "Override the IPC pipe prefix."),
                CommandOptions.Create<int?>("--max-runs", "Set the maximum concurrent Run count."));
        command.SetAction(async parseResult =>
        {
            var workspace = parseResult.GetValue(workspaceOption);
            if (string.IsNullOrWhiteSpace(workspace))
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "serve",
                    "--workspace is required.");
                return ExitCodes.InvalidArguments;
            }

            var workspaceRoot = Path.GetFullPath(workspace);
            var dataDirectory = parseResult.GetValue(dataDirectoryOption)
                ?? Path.Combine(workspaceRoot, ".madorin");

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            using var terminateRegistration = OperatingSystem.IsWindows()
                ? null
                : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    shutdown.Cancel();
                });

            string handshakeSecret;
            try
            {
                handshakeSecret = await ReadHandshakeSecretAsync(input, shutdown.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                WriteError(parseResult, jsonOption, output, "serve", ex.Message);
                Console.CancelKeyPress -= cancelHandler;
                return ExitCodes.InvalidArguments;
            }

            var options = new RuntimeServerOptions(workspaceRoot)
            {
                DataDirectory = Path.GetFullPath(dataDirectory),
                ConfigDirectory = Path.GetFullPath(
                    parseResult.GetValue<string?>("--config-dir") ?? Path.Combine(dataDirectory, "config")),
                LogDirectory = Path.GetFullPath(
                    parseResult.GetValue<string?>("--log-dir") ?? Path.Combine(dataDirectory, "logs")),
                InstanceId = parseResult.GetValue<string?>("--instance") ?? string.Empty,
                PipePrefix = parseResult.GetValue<string?>("--pipe-prefix") ?? "madorin.ai.runtime",
                MaxConcurrentRuns = parseResult.GetValue<int?>("--max-runs") ?? 4,
                HandshakeSecret = handshakeSecret,
                ProviderResolver = null
            };

            try
            {
                await using var server = await RuntimeServer.StartAsync(options, shutdown.Token);
                await server.RunAsync(shutdown.Token);
                WriteSuccess(parseResult, jsonOption, output, "serve");
                return ExitCodes.Success;
            }
            catch (InvalidOperationException ex)
            {
                WriteError(parseResult, jsonOption, output, "serve", ex.Message);
                return ExitCodes.WorkspaceError;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        });
        return command;
    }

    private static async Task<string> ReadHandshakeSecretAsync(
        TextReader input,
        CancellationToken cancellationToken)
    {
        var secret = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "A non-empty handshake secret is required on the controlled stdin handle.");
        }

        return secret.Trim();
    }

    public static Command CreateRun(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        ICliTerminal terminal,
        TextWriter output,
        string? configDirectory = null,
        Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>>?
            providerResolverFactory = null,
        string? memoryUserHome = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(output);

        var providerOption = CommandOptions.Create<string?>("--provider", "Select a Provider.");
        var modelOption = CommandOptions.Create<string?>("--model", "Select a model.");
        var agentOption = CommandOptions.Create<string?>("--agent", "Select an Agent.");
        var modeOption = CommandOptions.Create<string?>("--mode", "Select expert, meeting, or work mode.");
        var sessionOption = CommandOptions.Create<string?>("--session", "Continue an existing Session.");
        var inputOption = CommandOptions.Create<string?>("--input", "Run once with the supplied input.");

        var command = new Command("run", "Run one task or enter interactive mode.")
            .AddOptions(
                providerOption,
                modelOption,
                agentOption,
                modeOption,
                sessionOption,
                inputOption,
                CommandOptions.Create<string?>("--input-file", "Read input from a file."),
                CommandOptions.Create<string?>("--output-file", "Write output to a file."),
                CommandOptions.Create<string?>("--output-format", "Select text, json, or jsonl output."),
                CommandOptions.Create<bool>("--no-stream", "Wait for the complete result."),
                CommandOptions.Create<int?>("--timeout", "Set the timeout in seconds."));

        command.SetAction(async parseResult =>
        {
            StandaloneRuntimeContext context;
            try
            {
                context = StandaloneRuntimeContext.Create(
                    parseResult.GetValue(workspaceOption),
                    parseResult.GetValue(dataDirectoryOption),
                    configDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                WriteError(parseResult, jsonOption, output, "run", ex.Message);
                return ExitCodes.InvalidArguments;
            }

            var loader = new StandaloneConfigLoader(context.ConfigDirectory);
            StandaloneConfig config;
            IReadOnlyList<AgentConfigDocument> agentDocuments;
            try
            {
                var loadedConfig = await loader.LoadAsync().ConfigureAwait(false);
                if (loadedConfig is null && CliOutput.IsJson(parseResult, jsonOption))
                {
                    WriteError(
                        parseResult,
                        jsonOption,
                        output,
                        "run",
                        $"Configuration not found: {loader.ConfigPath}");
                    return ExitCodes.InvalidArguments;
                }

                config = loadedConfig ?? await ConfigWizard.RunInitAsync(
                        context.ConfigDirectory,
                        terminal)
                    .ConfigureAwait(false);
                agentDocuments = await loader.LoadAgentDocumentsAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsConfigurationException(ex))
            {
                WriteError(parseResult, jsonOption, output, "run", ex.Message);
                return ExitCodes.InvalidArguments;
            }

            var validationErrors = StandaloneConfigValidator.Validate(
                config,
                loader.ConfigPath,
                agentDocuments);
            if (validationErrors.Count > 0)
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "run",
                    RedactSecrets(string.Join(Environment.NewLine, validationErrors), config));
                return ExitCodes.InvalidArguments;
            }

            var agents = agentDocuments.Select(static document => document.Agent).ToList();

            if (!TryResolveSelection(
                    config,
                    agents,
                    parseResult.GetValue(providerOption),
                    parseResult.GetValue(modelOption),
                    parseResult.GetValue(agentOption),
                    out var selection))
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "run",
                    "The requested Provider, model, or Agent does not exist in the standalone configuration.");
                return ExitCodes.InvalidArguments;
            }

            if (!TryResolveMode(parseResult.GetValue(modeOption), out var mode))
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "run",
                    "Mode must be expert, meeting, work, or standalone.");
                return ExitCodes.InvalidArguments;
            }

            if (mode is not RuntimeMode.Expert)
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "run",
                    "Local Runtime currently supports Expert mode only; meeting and work modes are not implemented.");
                return ExitCodes.InvalidArguments;
            }

            var timeout = parseResult.GetValue<int?>("--timeout");
            using var cts = timeout is > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeout.Value))
                : new CancellationTokenSource();

            LocalRuntime runtime;
            try
            {
                var providerResolver = providerResolverFactory is null
                    ? StandaloneProviderFactory.Create(config)
                    : providerResolverFactory(config);
                var resolvedMemoryUserHome = memoryUserHome
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ArgumentException.ThrowIfNullOrWhiteSpace(resolvedMemoryUserHome);
                var memoryFiles = new MemoryFileService(
                    Path.GetFullPath(resolvedMemoryUserHome),
                    context.WorkspaceRoot);
                runtime = await LocalRuntime.StartAsync(
                        new LocalRuntimeOptions(
                            context.WorkspaceRoot,
                            context.DataDirectory,
                            context.LogDirectory,
                            context.RuntimeInstanceId,
                            providerResolver)
                        {
                            MemoryFiles = memoryFiles
                        },
                        cts.Token)
                    .ConfigureAwait(false);

                await using (runtime.ConfigureAwait(false))
                {
                    var suppliedInput = parseResult.GetValue(inputOption);
                    var sessionId = parseResult.GetValue(sessionOption);
                    if (suppliedInput is not null)
                    {
                        var events = sessionId is null
                            ? runtime.RunAsync(BuildNewRunRequest(suppliedInput, mode, selection), cts.Token)
                            : runtime.RunAsync(
                                BuildExistingRunRequest(sessionId, suppliedInput, mode, selection),
                                cts.Token);
                        var result = await ExecuteOneRunAsync(
                                events,
                                output,
                                config,
                                cts.Token)
                            .ConfigureAwait(false);
                        return result.ExitCode;
                    }

                    if (CliOutput.IsJson(parseResult, jsonOption))
                    {
                        CliOutput.WriteNotImplemented(
                            parseResult,
                            jsonOption,
                            output,
                            "run interactive",
                            "run interactive: NotImplemented in --json mode");
                        return ExitCodes.InvalidArguments;
                    }

                    return await RunReplAsync(
                            terminal,
                            output,
                            runtime,
                            memoryFiles,
                            sessionId,
                            mode,
                            selection,
                            config,
                            cts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException ex)
            {
                WriteError(
                    parseResult,
                    jsonOption,
                    output,
                    "run",
                    RedactSecrets(ex.Message, config));
                return ExitCodes.WorkspaceError;
            }
        });

        return command;
    }

    private static async Task<int> RunReplAsync(
        ICliTerminal terminal,
        TextWriter output,
        LocalRuntime runtime,
        MemoryFileService memoryFiles,
        string? sessionId,
        RuntimeMode mode,
        StandaloneSelection selection,
        StandaloneConfig config,
        CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                terminal.Write($"madorin [{selection.Provider.Name}/{selection.Model}] > ");

                var userInput = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
                if (userInput is null || userInput.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    return ExitCodes.Success;
                }

                if (string.IsNullOrWhiteSpace(userInput))
                {
                    continue;
                }

                if (await TryHandleMemoryReplCommandAsync(
                        userInput,
                        terminal,
                        output,
                        memoryFiles,
                        ct)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                var events = sessionId is null
                    ? runtime.RunAsync(BuildNewRunRequest(userInput, mode, selection), ct)
                    : runtime.RunAsync(
                        BuildExistingRunRequest(sessionId, userInput, mode, selection),
                        ct);
                var result = await ExecuteOneRunAsync(events, output, config, ct)
                    .ConfigureAwait(false);
                if (result.ExitCode != ExitCodes.Success)
                {
                    return result.ExitCode;
                }

                sessionId = result.SessionId ?? sessionId;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await output.WriteLineAsync("Run cancelled.").ConfigureAwait(false);
            return ExitCodes.UserInterrupted;
        }
    }

    private static async Task<bool> TryHandleMemoryReplCommandAsync(
        string command,
        ICliTerminal terminal,
        TextWriter output,
        MemoryFileService memoryFiles,
        CancellationToken ct)
    {
        var parts = command.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        try
        {
            if (parts[0].Equals("/memory", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length > 2
                    || !TryParseMemoryScope(
                        parts.Length == 1 ? "effective" : parts[1],
                        allowEffective: true,
                        out var scope))
                {
                    output.WriteLine("Usage: /memory [global|project|effective]");
                    return true;
                }

                if (scope == MemoryScope.Effective)
                {
                    output.WriteLine($"Global path: {memoryFiles.GlobalPath}");
                    output.WriteLine($"Project path: {memoryFiles.ProjectPath}");
                }
                else
                {
                    output.WriteLine($"Path: {GetMemoryPath(memoryFiles, scope)}");
                }

                WriteMemoryContent(
                    output,
                    await memoryFiles.ReadAsync(scope, ct).ConfigureAwait(false));
                return true;
            }

            if (!parts[0].Equals("/remember", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (parts.Length != 2
                || !TryParseMemoryScope(parts[1], allowEffective: false, out var appendScope))
            {
                output.WriteLine("Usage: /remember <global|project>");
                return true;
            }

            terminal.Write("Memory item: ");
            var item = await terminal.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(item))
            {
                output.WriteLine("Memory was not added.");
                return true;
            }

            await memoryFiles.AppendAsync(appendScope, item, ct).ConfigureAwait(false);
            output.WriteLine($"Updated memory: {GetMemoryPath(memoryFiles, appendScope)}");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            output.WriteLine($"Memory command failed: {ex.Message}");
            return true;
        }
    }

    private static bool TryParseMemoryScope(
        string value,
        bool allowEffective,
        out MemoryScope scope)
    {
        if (value.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Global;
            return true;
        }

        if (value.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Project;
            return true;
        }

        if (allowEffective
            && value.Equals("effective", StringComparison.OrdinalIgnoreCase))
        {
            scope = MemoryScope.Effective;
            return true;
        }

        scope = default;
        return false;
    }

    private static string GetMemoryPath(
        MemoryFileService memoryFiles,
        MemoryScope scope) => scope switch
        {
            MemoryScope.Global => memoryFiles.GlobalPath,
            MemoryScope.Project => memoryFiles.ProjectPath
                ?? throw new InvalidOperationException("Project memory requires a workspace root."),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };

    private static void WriteMemoryContent(TextWriter output, string? content)
    {
        if (content is null)
        {
            output.WriteLine("(not found)");
            return;
        }

        if (content.Length == 0)
        {
            output.WriteLine("(empty)");
            return;
        }

        output.Write(content);
        if (!content.EndsWith('\n'))
        {
            output.WriteLine();
        }
    }

    private static async Task<LocalRunResult> ExecuteOneRunAsync(
        IAsyncEnumerable<RuntimeEventEnvelope> events,
        TextWriter output,
        StandaloneConfig config,
        CancellationToken ct)
    {
        string? sessionId = null;
        try
        {
            await foreach (var envelope in events.WithCancellation(ct).ConfigureAwait(false))
            {
                switch (envelope.MessageType)
                {
                    case MessageTypes.RunAccepted:
                        var accepted = envelope.Payload.Deserialize(
                            RuntimeJsonContext.Default.RunAcceptedEvent);
                        sessionId = accepted?.SessionId ?? sessionId;
                        break;

                    case MessageTypes.TextDelta:
                        var delta = envelope.Payload.Deserialize(RuntimeJsonContext.Default.TextDeltaEvent);
                        if (delta is not null)
                        {
                            await output.WriteAsync(delta.Delta).ConfigureAwait(false);
                            await output.FlushAsync(ct).ConfigureAwait(false);
                        }

                        break;

                    case MessageTypes.RunCompleted:
                        await output.WriteLineAsync().ConfigureAwait(false);
                        return new LocalRunResult(ExitCodes.Success, sessionId);

                    case MessageTypes.RunFailed:
                        await output.WriteLineAsync().ConfigureAwait(false);
                        var failed = envelope.Payload.Deserialize(RuntimeJsonContext.Default.RunFailedEvent);
                        await output.WriteLineAsync(
                                $"Run failed: {RedactSecrets(failed?.Error?.Message ?? "unknown error", config)}")
                            .ConfigureAwait(false);
                        return new LocalRunResult(ExitCodes.GeneralError, sessionId);

                    case MessageTypes.RunCancelled:
                        await output.WriteLineAsync().ConfigureAwait(false);
                        await output.WriteLineAsync("Run cancelled.").ConfigureAwait(false);
                        return new LocalRunResult(ExitCodes.UserInterrupted, sessionId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await output.WriteLineAsync("Run cancelled.").ConfigureAwait(false);
            return new LocalRunResult(ExitCodes.UserInterrupted, sessionId);
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync(
                    $"Run failed: {RedactSecrets(ex.Message, config)}")
                .ConfigureAwait(false);
            return new LocalRunResult(ExitCodes.GeneralError, sessionId);
        }

        return new LocalRunResult(ExitCodes.GeneralError, sessionId);
    }

    private static NewSessionRunRequest BuildNewRunRequest(
        string userInput,
        RuntimeMode mode,
        StandaloneSelection selection)
    {
        var nextTurn = BuildNextTurnSelection(mode, selection);
        return new NewSessionRunRequest(
            SessionIdempotencyKey: Guid.NewGuid().ToString("N"),
            RunIdempotencyKey: Guid.NewGuid().ToString("N"),
            mode,
            nextTurn,
            [new TextContentBlock(userInput)]);
    }

    private static ExistingSessionRunRequest BuildExistingRunRequest(
        string sessionId,
        string userInput,
        RuntimeMode mode,
        StandaloneSelection selection) =>
        new(
            sessionId,
            Guid.NewGuid().ToString("N"),
            BuildNextTurnSelection(mode, selection),
            [new TextContentBlock(userInput)]);

    private static NextTurnSelection BuildNextTurnSelection(
        RuntimeMode mode,
        StandaloneSelection selection)
    {
        var agentRef = new AgentRef(
            selection.Agent.Id,
            PromptTemplateVersion: "1.0",
            selection.Agent.SystemPrompt,
            ProviderId: selection.Provider.Name,
            ModelId: selection.Model);
        var defaultSel = new DefaultSelection(selection.Provider.Name, selection.Model);
        var nextTurn = new NextTurnSelection(
            SelectionVersion: 1,
            mode,
            defaultSel,
            new ExpertModeOptions(agentRef));
        return nextTurn;
    }

    private static bool TryResolveSelection(
        StandaloneConfig config,
        IReadOnlyList<AgentConfig> agents,
        string? providerOverride,
        string? modelOverride,
        string? agentOverride,
        out StandaloneSelection selection)
    {
        var agentId = agentOverride ?? config.DefaultAgent;
        var agent = agents.FirstOrDefault(candidate =>
            candidate.Id.Equals(agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            selection = default!;
            return false;
        }

        var providerName = providerOverride ?? agent.Provider ?? config.DefaultProvider;
        var provider = config.Providers.FirstOrDefault(candidate =>
            candidate.Name.Equals(providerName, StringComparison.Ordinal));
        if (provider is null)
        {
            selection = default!;
            return false;
        }

        var model = modelOverride ?? agent.Model ?? config.DefaultModel;
        if (!provider.Models.Contains(model, StringComparer.Ordinal))
        {
            selection = default!;
            return false;
        }

        selection = new StandaloneSelection(provider, model, agent);
        return true;
    }

    private static bool TryResolveMode(string? value, out RuntimeMode mode)
    {
        switch (value?.ToUpperInvariant())
        {
            case null:
            case "":
            case "EXPERT":
            case "STANDALONE":
                mode = RuntimeMode.Expert;
                return true;
            case "MEETING":
                mode = RuntimeMode.Meeting;
                return true;
            case "WORK":
                mode = RuntimeMode.Work;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private sealed record StandaloneSelection(
        ProviderEntry Provider,
        string Model,
        AgentConfig Agent);

    private sealed record LocalRunResult(int ExitCode, string? SessionId);

    private static bool IsConfigurationException(Exception exception) =>
        exception is ArgumentException
            or ConfigBusyException
            or FileNotFoundException
            or InvalidOperationException
            or JsonException;

    private static string RedactSecrets(string message, StandaloneConfig? config)
    {
        if (config is null)
        {
            return message;
        }

        foreach (var apiKey in config.Providers
                     .Select(static provider => provider.ApiKey)
                     .Where(static apiKey => !string.IsNullOrEmpty(apiKey))
                     .Distinct(StringComparer.Ordinal))
        {
            message = message.Replace(apiKey, "***", StringComparison.Ordinal);
        }

        return message;
    }

    private static void WriteSuccess(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command)
    {
        if (CliOutput.IsJson(parseResult, jsonOption))
        {
            CliOutput.WriteJson(output, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("command", command);
                writer.WriteString("status", "ok");
                writer.WriteEndObject();
            });
        }
    }

    private static void WriteError(
        ParseResult parseResult,
        Option<bool> jsonOption,
        TextWriter output,
        string command,
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
            writer.WriteString("command", command);
            writer.WriteString("status", "error");
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
    }
}
