using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

public sealed class LocalRuntime : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly string _databaseConnectionString;
    private readonly BlobStagingStore _blobStore;
    private readonly RuntimeExecutionCore _executionCore;
    private readonly ToolCatalogStore _toolCatalogStore = new(
        new BuiltinToolRegistry(),
        new JsonSchemaToolValidator());
    private readonly Func<NextTurnSelection, IRuntimeProviderAdapter> _providerResolver;
    private readonly HashSet<IRuntimeProviderAdapter> _providers =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _providerSync = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ConcurrentDictionary<string, NextTurnSelection> _sessionSelections =
        new(StringComparer.Ordinal);
    private readonly WorkspaceWriteLock _workspaceLock;
    private int _disposed;

    private LocalRuntime(
        LocalRuntimeOptions options,
        WorkspaceWriteLock workspaceLock,
        string databaseConnectionString,
        AgentContextComposer agentContextComposer,
        MemoryFileService memoryFiles)
    {
        _dataDirectory = options.DataDirectory;
        _databaseConnectionString = databaseConnectionString;
        _providerResolver = options.ProviderResolver;
        _workspaceLock = workspaceLock;
        _blobStore = new BlobStagingStore(
            options.DataDirectory,
            options.RuntimeLimits,
            options.MaxGlobalBlobBytes);
        _executionCore = new RuntimeExecutionCore(
            options.DataDirectory,
            databaseConnectionString,
            options.RuntimeInstanceId,
            options.WorkspaceRoot,
            agentContextComposer,
            memoryFiles,
            options.RuntimeLimits,
            _blobStore);
        RuntimeInstanceId = options.RuntimeInstanceId;
        WorkspaceRoot = options.WorkspaceRoot;
    }

    public string RuntimeInstanceId { get; }

    public string WorkspaceRoot { get; }

    public static async Task<LocalRuntime> StartAsync(
        LocalRuntimeOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeInstanceId);
        ArgumentNullException.ThrowIfNull(options.ProviderResolver);
        if (options.WorkspaceLockTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var normalized = options with
        {
            WorkspaceRoot = Path.GetFullPath(options.WorkspaceRoot),
            DataDirectory = Path.GetFullPath(options.DataDirectory),
            LogDirectory = Path.GetFullPath(options.LogDirectory)
        };
        if (normalized.MemoryFiles is { } suppliedMemory
            && (suppliedMemory.ProjectPath is null
                || !PathsEqual(
                    suppliedMemory.ProjectPath,
                    MemoryFileService.ResolveProjectPath(normalized.WorkspaceRoot))))
        {
            throw new ArgumentException(
                "The supplied memory service must be bound to the Runtime workspace.",
                nameof(options));
        }

        Directory.CreateDirectory(normalized.WorkspaceRoot);
        var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                normalized.WorkspaceRoot,
                normalized.RuntimeInstanceId,
                normalized.WorkspaceLockTimeout,
                ct)
            .ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(normalized.LogDirectory);
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                    normalized.DataDirectory,
                    ct)
                .ConfigureAwait(false);
            var memoryFiles = normalized.MemoryFiles;
            if (memoryFiles is null)
            {
                var userHome = normalized.MemoryUserHome
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
                memoryFiles = new MemoryFileService(
                    Path.GetFullPath(userHome),
                    normalized.WorkspaceRoot);
            }

            var contextComposer = new AgentContextComposer(
                memoryFiles);
            return new LocalRuntime(
                normalized,
                workspaceLock,
                connection.ConnectionString,
                contextComposer,
                memoryFiles);
        }
        catch
        {
            await workspaceLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        NewSessionRunRequest request,
        CancellationToken ct = default) => RunNewSessionAsync(request, ct);

    public IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        ExistingSessionRunRequest request,
        CancellationToken ct = default) => RunExistingSessionAsync(request, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<IRuntimeProviderAdapter> providers;
        lock (_providerSync)
        {
            providers = [.. _providers];
            _providers.Clear();
        }

        foreach (var provider in providers)
        {
            switch (provider)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        await _blobStore.DisposeAsync().ConfigureAwait(false);
        await _workspaceLock.DisposeAsync().ConfigureAwait(false);
        _runGate.Dispose();
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> RunNewSessionAsync(
        NewSessionRunRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateExpertRequest(request);
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var provider = ResolveProvider(request.Selection);
            var prepared = await PrepareNewSessionAsync(request, provider, ct)
                .ConfigureAwait(false);
            yield return prepared.Accepted;
            await foreach (var envelope in _executionCore.RunAsync(
                    prepared.RunId,
                    prepared.SessionId,
                    request,
                    provider,
                    _toolCatalogStore,
                    _toolCatalogStore.CaptureSnapshot(),
                    controlPeer: null,
                    credentialRefreshHandler: null,
                    ct: ct)
                .ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> RunExistingSessionAsync(
        ExistingSessionRunRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunIdempotencyKey);
        if (request.InputOverride is not { Length: > 0 })
        {
            throw new ArgumentException("Existing Session input is required.", nameof(request));
        }

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(connection);
            var snapshot = await repository.GetSessionSnapshotAsync(request.SessionId, ct)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Session '{request.SessionId}' was not found.");
            var savedSelection = await GetSessionSelectionAsync(snapshot, ct).ConfigureAwait(false);
            if (request.ExpectedSelectionVersion is { } expectedVersion
                && expectedVersion != savedSelection.SelectionVersion)
            {
                throw new InvalidOperationException(
                    $"Selection version {expectedVersion} does not match current version {savedSelection.SelectionVersion}.");
            }

            var effectiveSelection = request.TurnOverride ?? savedSelection;
            if (effectiveSelection.Mode != snapshot.Mode)
            {
                throw new InvalidOperationException(
                    $"Selection mode {effectiveSelection.Mode} does not match Session mode {snapshot.Mode}.");
            }

            var executionRequest = new NewSessionRunRequest(
                $"existing:{request.SessionId}",
                request.RunIdempotencyKey,
                snapshot.Mode,
                effectiveSelection,
                request.InputOverride);
            ValidateExpertRequest(executionRequest);
            if (request.TurnOverride is null
                && effectiveSelection.ModeOptions is ExpertModeOptions
                {
                    Agent.SystemPrompt.Length: 0
                })
            {
                throw new InvalidOperationException(
                    "A TurnOverride with the current Agent prompt is required to resume this Session.");
            }

            if (effectiveSelection.ModeOptions is ExpertModeOptions expertOptions)
            {
                ValidateResumedExpertPrompt(snapshot, expertOptions);
            }

            var provider = ResolveProvider(effectiveSelection);
            var prepared = await PrepareExistingSessionAsync(
                    repository,
                    request,
                    executionRequest,
                    provider,
                    ct)
                .ConfigureAwait(false);
            yield return prepared.Accepted;
            await foreach (var envelope in _executionCore.RunAsync(
                    prepared.RunId,
                    prepared.SessionId,
                    executionRequest,
                    provider,
                    _toolCatalogStore,
                    _toolCatalogStore.CaptureSnapshot(),
                    controlPeer: null,
                    credentialRefreshHandler: null,
                    ct: ct)
                .ConfigureAwait(false))
            {
                yield return envelope;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<PreparedRun> PrepareNewSessionAsync(
        NewSessionRunRequest request,
        IRuntimeProviderAdapter provider,
        CancellationToken ct)
    {
        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var requestHash = RuntimeRunMetadata.ComputeRequestHash(request);
        var sessionId = await repository.CreateSessionAsync(
                request.Mode,
                request.SessionIdempotencyKey,
                TimeSpan.FromDays(7),
                requestHash,
                ct)
            .ConfigureAwait(false);
        var persistedSelection = RuntimeRunMetadata.RemovePromptContent(request.Selection);
        var selectionJson = JsonSerializer.Serialize(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        var saved = await repository.TrySaveSessionSelectionAsync(
                sessionId,
                expectedSelectionVersion: null,
                request.Selection.SelectionVersion,
                selectionJson,
                RuntimeRunMetadata.CreateAgentSnapshots(request.Selection),
                ct)
            .ConfigureAwait(false);
        if (!saved)
        {
            throw new InvalidOperationException("The Session Selection could not be initialized.");
        }

        _sessionSelections[sessionId] = request.Selection;
        return await PrepareRunAsync(
                repository,
                sessionId,
                request.RunIdempotencyKey,
                requestHash,
                provider,
                ct)
            .ConfigureAwait(false);
    }

    private Task<PreparedRun> PrepareExistingSessionAsync(
        SqliteSessionRepository repository,
        ExistingSessionRunRequest request,
        NewSessionRunRequest executionRequest,
        IRuntimeProviderAdapter provider,
        CancellationToken ct) =>
        PrepareRunAsync(
            repository,
            request.SessionId,
            executionRequest.RunIdempotencyKey,
            RuntimeRunMetadata.ComputeRequestHash(request),
            provider,
            ct);

    private async Task<PreparedRun> PrepareRunAsync(
        SqliteSessionRepository repository,
        string sessionId,
        string runIdempotencyKey,
        string requestHash,
        IRuntimeProviderAdapter provider,
        CancellationToken ct)
    {
        _ = provider;
        var runId = await repository.CreateRunAsync(
                sessionId,
                runIdempotencyKey,
                TimeSpan.FromHours(24),
                requestHash,
                ct)
            .ConfigureAwait(false);
        if (await repository.GetRunStatusAsync(runId, ct).ConfigureAwait(false)
            is not RunStatus.Accepted)
        {
            throw new InvalidOperationException(
                $"Run idempotency key '{runIdempotencyKey}' already completed or is still active.");
        }

        await using var outboxConnection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        using var outbox = new SqliteEventOutbox(
            outboxConnection,
            Path.Combine(_dataDirectory, "replay"),
            SqliteEventOutbox.DefaultMaxReplayBytes);
        var payload = JsonSerializer.SerializeToElement(
            new RunAcceptedEvent(sessionId, runId),
            RuntimeJsonContext.Default.RunAcceptedEvent);
        var gsn = await outbox.AppendAsync(
                runId,
                runSequence: 0,
                MessageTypes.RunAccepted,
                payload.GetRawText(),
                ct)
            .ConfigureAwait(false);
        var accepted = new RuntimeEventEnvelope(
            RuntimeInstanceId,
            gsn,
            runId,
            RunSequence: 0,
            MessageTypes.RunAccepted,
            DateTimeOffset.UtcNow,
            payload);
        return new PreparedRun(sessionId, runId, accepted);
    }

    private async Task<NextTurnSelection> GetSessionSelectionAsync(
        PersistedSessionSnapshot snapshot,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_sessionSelections.TryGetValue(snapshot.SessionId, out var selection))
        {
            return selection;
        }

        if (string.IsNullOrWhiteSpace(snapshot.SelectionJson))
        {
            throw new InvalidOperationException(
                $"Session '{snapshot.SessionId}' has no persisted Selection.");
        }

        selection = JsonSerializer.Deserialize(
            snapshot.SelectionJson,
            RuntimeJsonContext.Default.NextTurnSelection)
            ?? throw new InvalidDataException(
                $"Session '{snapshot.SessionId}' has an invalid Selection snapshot.");
        return selection;
    }

    private IRuntimeProviderAdapter ResolveProvider(NextTurnSelection selection)
    {
        var provider = _providerResolver(selection)
            ?? throw new InvalidOperationException("The Provider resolver returned null.");
        lock (_providerSync)
        {
            _providers.Add(provider);
        }

        return provider;
    }

    private async Task<SqliteConnection> OpenDatabaseConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_databaseConnectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await SqliteSchema.EnsureCreatedAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateExpertRequest(NewSessionRunRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunIdempotencyKey);
        if (request.Mode is not RuntimeMode.Expert
            || request.Selection.Mode is not RuntimeMode.Expert
            || request.Selection.ModeOptions is not ExpertModeOptions)
        {
            throw new NotSupportedException(
                "Local Runtime currently supports Expert mode only; meeting and work modes are not implemented.");
        }

        if (request.InitialInput.Length == 0)
        {
            throw new ArgumentException("Expert mode requires input.", nameof(request));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            comparison);
    }


    private static void ValidateResumedExpertPrompt(
        PersistedSessionSnapshot snapshot,
        ExpertModeOptions expertOptions)
    {
        if (string.IsNullOrWhiteSpace(expertOptions.Agent.SystemPrompt))
        {
            throw new InvalidOperationException(
                "An empty Agent prompt cannot be used to resume an Expert Session.");
        }

        var agentSnapshot = snapshot.AgentSnapshots.FirstOrDefault(agent =>
            string.Equals(agent.AgentId, expertOptions.Agent.AgentId, StringComparison.Ordinal));
        if (agentSnapshot is null)
        {
            throw new InvalidOperationException(
                $"Agent '{expertOptions.Agent.AgentId}' is missing from the Session Agent snapshots.");
        }

        var promptHash = RuntimeRunMetadata.ComputePromptHash(expertOptions.Agent.SystemPrompt);
        if (!string.Equals(promptHash, agentSnapshot.PromptHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Agent prompt hash for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.");
        }

        if (!string.Equals(
                expertOptions.Agent.PromptTemplateVersion,
                agentSnapshot.PromptTemplateVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Agent prompt template version for '{expertOptions.Agent.AgentId}' does not match the Session snapshot.");
        }
    }

    private sealed record PreparedRun(
        string SessionId,
        string RunId,
        RuntimeEventEnvelope Accepted);
}
