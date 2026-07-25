using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
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
    private readonly SessionMaintenanceAuditLog _maintenanceAudit;
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
        _maintenanceAudit = new SessionMaintenanceAuditLog(options.DataDirectory);
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

    /// <summary>Gets a point-in-time status snapshot for the direct CLI host.</summary>
    public LocalRuntimeStatus GetStatus()
    {
        ThrowIfDisposed();
        return new LocalRuntimeStatus(
            RuntimeInstanceId,
            WorkspaceRoot,
            _runGate.CurrentCount == 0 ? 1 : 0,
            GC.GetTotalMemory(forceFullCollection: false),
            _toolCatalogStore.CaptureSnapshot().Tools.Count);
    }

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

    /// <summary>Gets one persisted Session snapshot.</summary>
    public async Task<PersistedSessionSnapshot?> GetSessionSnapshotAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        return await repository.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Lists persisted Sessions using cursor pagination.</summary>
    public async Task<SessionListResult> ListSessionsAsync(
        SessionListParameters parameters,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (parameters.Cursor is not null && string.IsNullOrWhiteSpace(parameters.Cursor))
        {
            throw new ArgumentException("The Session cursor must not be empty.", nameof(parameters));
        }

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var descriptors = await repository.ListSessionsAsync(
                new SessionListQuery(
                    parameters.Mode,
                    parameters.Status,
                    parameters.Since,
                    parameters.Search,
                    parameters.Limit + 1,
                    parameters.Cursor),
                ct)
            .ConfigureAwait(false);

        string? nextCursor = null;
        IReadOnlyList<SessionDescriptor> page = descriptors;
        if (descriptors.Count > parameters.Limit)
        {
            page = descriptors.Take(parameters.Limit).ToArray();
            var last = page[^1];
            nextCursor = string.Format(
                CultureInfo.InvariantCulture,
                "{0:O}|{1}",
                last.UpdatedAt,
                last.SessionId);
        }

        return new SessionListResult(
            page.Select(static descriptor => new SessionListItem(
                    descriptor.SessionId,
                    descriptor.Mode,
                    descriptor.Status,
                    descriptor.UpdatedAt,
                    descriptor.Title))
                .ToArray(),
            nextCursor);
    }

    /// <summary>Lists persisted message metadata for one Session.</summary>
    public async Task<SessionMessagesListResult> ListSessionMessagesAsync(
        SessionMessagesListParameters parameters,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.SessionId);
        if (parameters.PageSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        if (parameters.Cursor is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var entries = await repository.ListMessageIndexAsync(
                parameters.SessionId,
                parameters.Cursor,
                parameters.PageSize + 1,
                ct)
            .ConfigureAwait(false);
        var hasMore = entries.Count > parameters.PageSize;
        var messages = entries.Take(parameters.PageSize)
            .Select(static entry => new SessionMessageDescriptor(
                entry.MessageId,
                entry.Sequence,
                entry.InvocationId,
                entry.AgentId,
                entry.Role,
                entry.CreatedAt))
            .ToArray();
        return new SessionMessagesListResult(
            messages,
            hasMore ? messages[^1].Sequence : null);
    }

    /// <summary>Reads the canonical message history for Session export.</summary>
    public async Task<IReadOnlyList<ConversationRecordV1>> ReadSessionHistoryAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var store = new ConversationStore(_dataDirectory);
        return await store.ReadAllAsync(
                sessionId,
                new ConversationReadOptions(ConversationBlobReadMode.Stream),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Gets canonical history and effective context projection statistics.</summary>
    public async Task<SessionContextStatus?> GetSessionContextStatusAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (await GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var store = new ConversationStore(_dataDirectory);
        var source = await store.ReadCompactionSourceAsync(sessionId, ct).ConfigureAwait(false);
        var cache = await store.ReadCompactCacheAsync(sessionId, ct).ConfigureAwait(false);
        var cacheCurrent = cache is not null
            && cache.SourceHistorySha256.Equals(source.HistorySha256, StringComparison.OrdinalIgnoreCase);
        var canonicalEstimatedTokens = cacheCurrent
            ? cache!.BeforeEstimatedTokens
            : EstimateHistoryTokens(source.Records);

        return new SessionContextStatus(
            sessionId,
            source.Records.Count,
            canonicalEstimatedTokens,
            cacheCurrent ? cache!.ProjectionMessages.Length : source.Records.Count,
            cacheCurrent ? cache!.AfterEstimatedTokens : canonicalEstimatedTokens,
            cacheCurrent ? cache!.Strategy : "full",
            cacheCurrent,
            cacheCurrent ? cache!.CreatedAt : null);
    }

    /// <summary>Sets a persisted Session status without changing its conversation data.</summary>
    public async Task<bool> TrySetSessionStatusAsync(
        string sessionId,
        SessionStatus status,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        var operation = status is SessionStatus.Archived ? "archive" : "unarchive";
        var auditId = await AppendMaintenanceAuditAsync(
                sessionId,
                operation,
                "prepared",
                outcome: null,
                $"targetStatus={status}",
                ct)
            .ConfigureAwait(false);
        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var updated = await repository.TrySetSessionStatusAsync(sessionId, status, ct)
            .ConfigureAwait(false);
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                operation,
                "completed",
                updated ? "ok" : "not-found",
                $"targetStatus={status}",
                CancellationToken.None)
            .ConfigureAwait(false);
        return updated;
    }

    /// <summary>Deletes a Session after establishing a same-volume recovery point.</summary>
    public async Task<SessionDeletionResult?> DeleteSessionAsync(
        string sessionId,
        bool includeBlobs,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var detail = $"includeBlobs={includeBlobs.ToString().ToLowerInvariant()}";
        var auditId = await AppendMaintenanceAuditAsync(
                sessionId,
                "delete",
                "prepared",
                outcome: null,
                detail,
                ct)
            .ConfigureAwait(false);
        var result = await DeleteSessionCoreAsync(sessionId, includeBlobs, ct)
            .ConfigureAwait(false);
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                "delete",
                "completed",
                result is null ? "not-found" : "ok",
                detail,
                CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<SessionDeletionResult?> DeleteSessionCoreAsync(
        string sessionId,
        bool includeBlobs,
        CancellationToken ct)
    {

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        if (await repository.GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var toolBlobReferences = includeBlobs
            ? await repository.GetToolResultBlobReferencesAsync(sessionId, ct)
                .ConfigureAwait(false)
            : new SessionBlobReferences([], []);

        var store = new ConversationStore(_dataDirectory);
        ConversationDeleteRecoveryPoint? recoveryPoint = null;
        var databaseCommitted = false;
        try
        {
            recoveryPoint = await store.PrepareSessionDeletionAsync(
                    sessionId,
                    includeBlobs,
                    toolBlobReferences.TargetBlobIds,
                    toolBlobReferences.ProtectedBlobIds,
                    ct)
                .ConfigureAwait(false);
            if (!await repository.TryDeleteSessionAsync(sessionId, ct).ConfigureAwait(false))
            {
                await store.RestoreSessionDeletionAsync(
                        recoveryPoint,
                        $"Session '{sessionId}' no longer exists.")
                    .ConfigureAwait(false);
                return null;
            }

            databaseCommitted = true;
            await store.CommitSessionDeletionAsync(recoveryPoint, CancellationToken.None)
                .ConfigureAwait(false);
            _sessionSelections.TryRemove(sessionId, out _);
            return new SessionDeletionResult(
                sessionId,
                includeBlobs,
                recoveryPoint.QuarantinedBlobCount,
                recoveryPoint.RecoveryDirectory);
        }
        catch (Exception ex) when (!databaseCommitted && recoveryPoint is not null)
        {
            try
            {
                await store.RestoreSessionDeletionAsync(recoveryPoint, ex.Message)
                    .ConfigureAwait(false);
            }
            catch (Exception restoreException)
            {
                throw new InvalidDataException(
                    $"Session deletion failed and recovery point '{recoveryPoint.RecoveryDirectory}' could not be restored.",
                    new AggregateException(ex, restoreException));
            }

            throw;
        }
    }

    /// <summary>Builds or plans a rebuildable ContextProjection cache for a Session.</summary>
    public async Task<SessionCompactionResult?> CompactSessionAsync(
        string sessionId,
        SessionCompactionOptions options,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateCompactionOptions(options);

        if (options.DryRun)
        {
            return await CompactSessionCoreAsync(sessionId, options, ct).ConfigureAwait(false);
        }

        var detail = $"strategy={GetCompactionStrategyName(options.Strategy)};force={options.Force.ToString().ToLowerInvariant()}";
        var auditId = await AppendMaintenanceAuditAsync(
                sessionId,
                "compact",
                "prepared",
                outcome: null,
                detail,
                ct)
            .ConfigureAwait(false);
        var result = await CompactSessionCoreAsync(sessionId, options, ct).ConfigureAwait(false);
        var outcome = result switch
        {
            null => "not-found",
            { CacheHit: true } => "cache-hit",
            _ => "ok"
        };
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                "compact",
                "completed",
                outcome,
                detail,
                CancellationToken.None)
            .ConfigureAwait(false);
        return result;
    }

    private async Task<SessionCompactionResult?> CompactSessionCoreAsync(
        string sessionId,
        SessionCompactionOptions options,
        CancellationToken ct)
    {

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(connection);
            var snapshot = await repository.GetSessionSnapshotAsync(sessionId, ct)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return null;
            }

            var selection = await GetSessionSelectionAsync(snapshot, ct).ConfigureAwait(false);
            var providerId = options.ProviderId ?? selection.DefaultSelection.ProviderId;
            var modelId = options.ModelId ?? selection.DefaultSelection.ModelId;
            var effectiveSelection = selection with
            {
                DefaultSelection = new DefaultSelection(providerId, modelId)
            };
            var provider = ResolveProvider(effectiveSelection);
            ValidateCompactionModel(provider, modelId);
            int? keepLastTokens = options.Strategy is SessionCompactionStrategy.SlidingWindow
                ? options.KeepLastTokens ?? ResolveDefaultCompactionWindow(provider, modelId)
                : null;
            var strategyName = GetCompactionStrategyName(options.Strategy);
            var store = new ConversationStore(_dataDirectory);
            var source = await store.ReadCompactionSourceAsync(sessionId, ct)
                .ConfigureAwait(false);

            if (!options.DryRun && !options.Force)
            {
                var cached = await store.ReadCompactCacheAsync(sessionId, ct)
                    .ConfigureAwait(false);
                if (IsMatchingCompactCache(
                        cached,
                        source.HistorySha256,
                        strategyName,
                        providerId,
                        modelId,
                        keepLastTokens))
                {
                    return CreateCachedCompactionResult(cached!);
                }
            }

            var estimateInvocationId = "compact-estimate-" + Guid.NewGuid().ToString("N");
            ValueTask<ProviderTokenEstimate> EstimateAsync(
                RuntimeProviderMessage[] messages,
                CancellationToken token) =>
                provider.EstimateTokensAsync(
                    new RuntimeProviderRequest(
                        estimateInvocationId,
                        "session-compactor",
                        providerId,
                        modelId,
                        messages,
                        Tools: null,
                        CancellationToken: token),
                    token);
            var fullProjection = await ContextProjectionBuilder.BuildAsync(
                    source.Records,
                    int.MaxValue,
                    EstimateAsync,
                    ct)
                .ConfigureAwait(false);

            if (options.DryRun)
            {
                var plannedMessageCount = options.Strategy is SessionCompactionStrategy.Summary
                    ? 1
                    : options.Strategy is SessionCompactionStrategy.Full
                        ? fullProjection.Messages.Length
                        : (await ContextProjectionBuilder.BuildAsync(
                                source.Records,
                                keepLastTokens!.Value,
                                EstimateAsync,
                                ct)
                            .ConfigureAwait(false)).Messages.Length;
                return new SessionCompactionResult(
                    sessionId,
                    options.Strategy,
                    providerId,
                    modelId,
                    keepLastTokens,
                    source.Records.Count,
                    plannedMessageCount,
                    Math.Max(0, source.Records.Count - plannedMessageCount),
                    fullProjection.EstimatedTokens,
                    AfterEstimatedTokens: null,
                    fullProjection.EstimateSource,
                    DryRun: true,
                    CacheHit: false,
                    CacheWritten: false);
            }

            RuntimeProviderMessage[] projectionMessages;
            int droppedMessageCount;
            int afterEstimatedTokens;
            string estimateSource;
            if (options.Strategy is SessionCompactionStrategy.Summary)
            {
                projectionMessages =
                [await GenerateSummaryMessageAsync(
                    provider,
                    providerId,
                    modelId,
                    fullProjection.Messages,
                    ct).ConfigureAwait(false)];
                var afterEstimate = await EstimateAsync(projectionMessages, ct).ConfigureAwait(false);
                afterEstimatedTokens = afterEstimate.InputTokens
                    ?? ProviderTokenEstimator.Estimate(new RuntimeProviderRequest(
                        estimateInvocationId,
                        "session-compactor",
                        providerId,
                        modelId,
                        projectionMessages)).InputTokens
                    ?? 0;
                estimateSource = afterEstimate.InputTokens is null
                    ? "runtime.utf8-bytes/4"
                    : $"provider.{afterEstimate.Accuracy.ToString().ToLowerInvariant()}";
                droppedMessageCount = source.Records.Count;
            }
            else
            {
                var projection = options.Strategy is SessionCompactionStrategy.Full
                    ? fullProjection
                    : await ContextProjectionBuilder.BuildAsync(
                            source.Records,
                            keepLastTokens!.Value,
                            EstimateAsync,
                            ct)
                        .ConfigureAwait(false);
                projectionMessages = projection.Messages;
                droppedMessageCount = projection.DroppedMessageCount;
                afterEstimatedTokens = projection.EstimatedTokens;
                estimateSource = projection.EstimateSource;
            }

            var compactCache = new ConversationCompactCacheV1(
                ConversationCompactCacheV1.CurrentSchema,
                sessionId,
                source.HistorySha256,
                strategyName,
                providerId,
                modelId,
                keepLastTokens,
                source.Records.Count,
                droppedMessageCount,
                fullProjection.EstimatedTokens,
                afterEstimatedTokens,
                estimateSource,
                projectionMessages.Select(CreateCompactMessage).ToArray(),
                DateTimeOffset.UtcNow);
            await store.WriteCompactCacheAsync(sessionId, compactCache, ct)
                .ConfigureAwait(false);
            return CreateGeneratedCompactionResult(options.Strategy, compactCache);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>Gets the current persisted Selection for a Session.</summary>
    public async Task<NextTurnSelection?> GetSessionSelectionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var snapshot = await GetSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);
        return snapshot is null
            ? null
            : await GetSessionSelectionAsync(snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Updates the next-turn Selection using optimistic concurrency.</summary>
    public async Task<NextTurnSelection> UpdateSessionSelectionAsync(
        string sessionId,
        int expectedSelectionVersion,
        NextTurnSelection selection,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(selection);
        if (expectedSelectionVersion < 0
            || selection.SelectionVersion <= expectedSelectionVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSelectionVersion));
        }

        await using var connection = await OpenDatabaseConnectionAsync(ct).ConfigureAwait(false);
        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetSessionSnapshotAsync(sessionId, ct)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        if (snapshot.SelectionVersion is null || snapshot.SelectionJson is null)
        {
            throw new InvalidOperationException(
                $"Session '{sessionId}' has no persisted Selection.");
        }

        if (selection.Mode != snapshot.Mode)
        {
            throw new InvalidOperationException("A Session's mode cannot be changed.");
        }

        var persistedSelection = RuntimeRunMetadata.RemovePromptContent(selection);
        var selectionJson = JsonSerializer.Serialize(
            persistedSelection,
            RuntimeJsonContext.Default.NextTurnSelection);
        var agentSnapshots = RuntimeRunMetadata.CreateAgentSnapshots(selection);
        bool saved;
        if (selection.ModeOptions is MeetingModeOptions meetingOptions)
        {
            var meetingInputs = meetingOptions.Participants
                .Select(static participant => new MeetingParticipantInput(
                    participant.ParticipantId,
                    participant.Agent.AgentId,
                    JsonSerializer.Serialize(
                        participant.Agent,
                        RuntimeJsonContext.Default.AgentRef),
                    participant.DisplayName,
                    participant.JoinOrder,
                    participant.Status == ParticipantStatus.Removed
                        ? "Removed"
                        : participant.Status.ToString().ToLowerInvariant()))
                .ToArray();
            var policyJson = JsonSerializer.Serialize(
                meetingOptions.EffectivePolicy,
                RuntimeJsonContext.Default.MeetingPolicy);
            saved = await repository.TrySaveSessionSelectionWithMeetingAsync(
                    sessionId,
                    expectedSelectionVersion,
                    selection.SelectionVersion,
                    selectionJson,
                    agentSnapshots,
                    meetingInputs,
                    policyJson,
                    RuntimeRunMetadata.ComputePromptHash(policyJson),
                    ct)
                .ConfigureAwait(false);
        }
        else
        {
            saved = await repository.TrySaveSessionSelectionAsync(
                    sessionId,
                    expectedSelectionVersion,
                    selection.SelectionVersion,
                    selectionJson,
                    agentSnapshots,
                    ct)
                .ConfigureAwait(false);
        }

        if (!saved)
        {
            throw new InvalidOperationException(
                $"Selection version {expectedSelectionVersion} is no longer current.");
        }

        _sessionSelections[sessionId] = selection;
        return selection;
    }

    /// <summary>Captures the immutable tool catalog visible to the next Run.</summary>
    public ToolCatalogSnapshot GetToolCatalogSnapshot()
    {
        ThrowIfDisposed();
        return _toolCatalogStore.CaptureSnapshot();
    }

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
        _maintenanceAudit.Dispose();
        _runGate.Dispose();
    }

    private async IAsyncEnumerable<RuntimeEventEnvelope> RunNewSessionAsync(
        NewSessionRunRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
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
                    providerFactory: providerId => ResolveProvider(
                        request.Selection,
                        providerId),
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
            ValidateRequest(executionRequest);
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
                    providerFactory: providerId => ResolveProvider(
                        effectiveSelection,
                        providerId),
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

    private IRuntimeProviderAdapter ResolveProvider(
        NextTurnSelection selection,
        string? providerId = null)
    {
        var effectiveSelection = string.IsNullOrWhiteSpace(providerId)
            || providerId.Equals(
                selection.DefaultSelection.ProviderId,
                StringComparison.Ordinal)
            ? selection
            : selection with
            {
                DefaultSelection = selection.DefaultSelection with
                {
                    ProviderId = providerId
                }
            };
        var provider = _providerResolver(effectiveSelection)
            ?? throw new InvalidOperationException("The Provider resolver returned null.");
        lock (_providerSync)
        {
            _providers.Add(provider);
        }

        return provider;
    }

    private static void ValidateCompactionOptions(SessionCompactionOptions options)
    {
        if (!Enum.IsDefined(options.Strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ProviderId is not null && string.IsNullOrWhiteSpace(options.ProviderId))
        {
            throw new ArgumentException("The compaction Provider must not be empty.", nameof(options));
        }

        if (options.ModelId is not null && string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new ArgumentException("The compaction model must not be empty.", nameof(options));
        }

        if (options.KeepLastTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.KeepLastTokens is not null
            && options.Strategy is not SessionCompactionStrategy.SlidingWindow)
        {
            throw new ArgumentException(
                "KeepLastTokens can only be used with the sliding-window strategy.",
                nameof(options));
        }
    }

    private static void ValidateCompactionModel(
        IRuntimeProviderAdapter provider,
        string modelId)
    {
        if (provider.Models.Count == 0
            || provider.Models.Any(model =>
                string.Equals(model.Id, modelId, StringComparison.Ordinal)
                || string.Equals(model.ProviderModelId, modelId, StringComparison.Ordinal)))
        {
            return;
        }

        throw new KeyNotFoundException(
            $"Model '{modelId}' is not available from Provider '{provider.ProviderId}'.");
    }

    private static int ResolveDefaultCompactionWindow(
        IRuntimeProviderAdapter provider,
        string modelId)
    {
        var model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal)
            || string.Equals(candidate.ProviderModelId, modelId, StringComparison.Ordinal));
        return model?.ContextWindow is > 0
            ? Math.Max(1, (int)(model.ContextWindow.Value * 0.9))
            : SessionCompactionOptions.DefaultKeepLastTokens;
    }

    private static int EstimateHistoryTokens(IReadOnlyList<ConversationRecordV1> records)
    {
        long utf8Bytes = 0;
        foreach (var record in records)
        {
            utf8Bytes += Encoding.UTF8.GetByteCount(record.Role);
            utf8Bytes += Encoding.UTF8.GetByteCount(record.Content.GetRawText());
        }

        return (int)Math.Min(int.MaxValue, (utf8Bytes + 3) / 4);
    }

    private static bool IsMatchingCompactCache(
        ConversationCompactCacheV1? cache,
        string sourceHistorySha256,
        string strategy,
        string providerId,
        string modelId,
        int? keepLastTokens) =>
        cache is not null
        && string.Equals(
            cache.SourceHistorySha256,
            sourceHistorySha256,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(cache.Strategy, strategy, StringComparison.Ordinal)
        && string.Equals(cache.ProviderId, providerId, StringComparison.Ordinal)
        && string.Equals(cache.ModelId, modelId, StringComparison.Ordinal)
        && cache.KeepLastTokens == keepLastTokens;

    private static SessionCompactionResult CreateCachedCompactionResult(
        ConversationCompactCacheV1 cache) =>
        new(
            cache.SessionId,
            ParseCompactionStrategy(cache.Strategy),
            cache.ProviderId,
            cache.ModelId,
            cache.KeepLastTokens,
            cache.SourceMessageCount,
            cache.ProjectionMessages.Length,
            cache.DroppedMessageCount,
            cache.BeforeEstimatedTokens,
            cache.AfterEstimatedTokens,
            cache.EstimateSource,
            DryRun: false,
            CacheHit: true,
            CacheWritten: false);

    private static SessionCompactionResult CreateGeneratedCompactionResult(
        SessionCompactionStrategy strategy,
        ConversationCompactCacheV1 cache) =>
        new(
            cache.SessionId,
            strategy,
            cache.ProviderId,
            cache.ModelId,
            cache.KeepLastTokens,
            cache.SourceMessageCount,
            cache.ProjectionMessages.Length,
            cache.DroppedMessageCount,
            cache.BeforeEstimatedTokens,
            cache.AfterEstimatedTokens,
            cache.EstimateSource,
            DryRun: false,
            CacheHit: false,
            CacheWritten: true);

    private static ConversationCompactMessageV1 CreateCompactMessage(
        RuntimeProviderMessage message) =>
        new(
            message.Role,
            JsonSerializer.SerializeToElement(
                message.Content,
                RuntimeJsonContext.Default.ContentBlockArray));

    private static async Task<RuntimeProviderMessage> GenerateSummaryMessageAsync(
        IRuntimeProviderAdapter provider,
        string providerId,
        string modelId,
        RuntimeProviderMessage[] history,
        CancellationToken ct)
    {
        var invocationId = "compact-summary-" + Guid.NewGuid().ToString("N");
        var instruction = new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(
                "Summarize this conversation for future context. Preserve decisions, constraints, " +
                "unresolved work, identifiers, and tool outcomes. Do not add facts. Return only the summary.")]);
        var request = new RuntimeProviderRequest(
            invocationId,
            "session-compactor",
            providerId,
            modelId,
            [instruction, .. history],
            Tools: null,
            CancellationToken: ct);
        var summary = new StringBuilder();
        var completed = false;
        await foreach (var providerEvent in provider.CompleteStreamingAsync(request, ct)
            .ConfigureAwait(false))
        {
            switch (providerEvent)
            {
                case TextDeltaProviderEvent text:
                    summary.Append(text.Delta);
                    break;
                case InvocationCompletedProviderEvent:
                    completed = true;
                    break;
                case InvocationFailedProviderEvent failed:
                    throw new InvalidOperationException(
                        $"Summary Provider failed ({failed.Error.Code}): {failed.Error.Message}");
                case ToolCallDeltaProviderEvent or ToolCallCompleteProviderEvent:
                    throw new InvalidDataException(
                        "The summary Provider returned a tool call for a tool-free request.");
                case ReasoningDeltaProviderEvent
                    or UsageUpdatedProviderEvent
                    or ProjectionAdjustedProviderEvent:
                    break;
                default:
                    throw new InvalidDataException(
                        $"The summary Provider returned unsupported event '{providerEvent.GetType().Name}'.");
            }
        }

        if (!completed || string.IsNullOrWhiteSpace(summary.ToString()))
        {
            throw new InvalidDataException(
                "The summary Provider did not return a completed non-empty summary.");
        }

        return new RuntimeProviderMessage(
            RuntimeProviderRoles.System,
            [new TextContentBlock(summary.ToString())]);
    }

    private static string GetCompactionStrategyName(SessionCompactionStrategy strategy) =>
        strategy switch
        {
            SessionCompactionStrategy.Full => "full",
            SessionCompactionStrategy.SlidingWindow => "sliding-window",
            SessionCompactionStrategy.Summary => "summary",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };

    private static SessionCompactionStrategy ParseCompactionStrategy(string strategy) =>
        strategy switch
        {
            "full" => SessionCompactionStrategy.Full,
            "sliding-window" => SessionCompactionStrategy.SlidingWindow,
            "summary" => SessionCompactionStrategy.Summary,
            _ => throw new InvalidDataException(
                $"The compact cache contains unknown strategy '{strategy}'.")
        };

    private async Task<string> AppendMaintenanceAuditAsync(
        string sessionId,
        string operation,
        string phase,
        string? outcome,
        string detail,
        CancellationToken ct)
    {
        var auditId = Guid.NewGuid().ToString("N");
        await AppendMaintenanceAuditAsync(
                auditId,
                sessionId,
                operation,
                phase,
                outcome,
                detail,
                ct)
            .ConfigureAwait(false);
        return auditId;
    }

    private async Task AppendMaintenanceAuditAsync(
        string auditId,
        string sessionId,
        string operation,
        string phase,
        string? outcome,
        string detail,
        CancellationToken ct) =>
        await _maintenanceAudit.AppendAsync(
                new SessionMaintenanceAuditRecord(
                    SessionMaintenanceAuditLog.CurrentSchema,
                    auditId,
                    RuntimeInstanceId,
                    sessionId,
                    operation,
                    phase,
                    outcome,
                    detail,
                    DateTimeOffset.UtcNow),
                ct)
            .ConfigureAwait(false);

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

    private static void ValidateRequest(NewSessionRunRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunIdempotencyKey);
        if (request.Selection.Mode != request.Mode)
        {
            throw new ArgumentException(
                "Selection mode must match the requested Runtime mode.",
                nameof(request));
        }

        var hasMatchingOptions = request.Mode switch
        {
            RuntimeMode.Expert => request.Selection.ModeOptions is ExpertModeOptions,
            RuntimeMode.Meeting => request.Selection.ModeOptions is MeetingModeOptions,
            RuntimeMode.Work => request.Selection.ModeOptions is WorkModeOptions,
            _ => false
        };
        if (!hasMatchingOptions)
        {
            throw new ArgumentException(
                "Selection options must match the requested Runtime mode.",
                nameof(request));
        }

        if (request.InitialInput.Length == 0)
        {
            throw new ArgumentException("Runtime input is required.", nameof(request));
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
