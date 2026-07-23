using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Modes.Meeting;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Transport.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

internal sealed class RuntimeExecutionCore(
    string dataDirectory,
    string databaseConnectionString,
    string runtimeInstanceId,
    string workspaceRoot,
    AgentContextComposer agentContextComposer,
    MemoryFileService memoryFiles,
    RuntimeLimits runtimeLimits,
    IToolResultBlobStore resultBlobStore)
{
    private readonly AgentContextComposer _agentContextComposer = agentContextComposer
        ?? throw new ArgumentNullException(nameof(agentContextComposer));
    private readonly string _dataDirectory = Path.GetFullPath(dataDirectory);
    private readonly string _databaseConnectionString = databaseConnectionString;
    private readonly MemoryFileService _memoryFiles = memoryFiles
        ?? throw new ArgumentNullException(nameof(memoryFiles));
    private readonly MemoryInvocationSnapshotStore _memorySnapshots = new();
    private readonly RuntimeLimits _runtimeLimits = runtimeLimits
        ?? throw new ArgumentNullException(nameof(runtimeLimits));
    private readonly IToolResultBlobStore _resultBlobStore = resultBlobStore
        ?? throw new ArgumentNullException(nameof(resultBlobStore));
    private readonly string _runtimeInstanceId = runtimeInstanceId;
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public async IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        IRuntimeProviderAdapter provider,
        IToolCatalogStore toolCatalogStore,
        ToolCatalogSnapshot toolCatalogSnapshot,
        IDuplexRpcPeer? controlPeer = null,
        Func<CredentialsRefreshRequestedEvent, CancellationToken, Task<bool>>?
            credentialRefreshHandler = null,
        Func<bool>? isRunTimedOut = null,
        Func<string, IRuntimeProviderAdapter>? providerFactory = null,
        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? meetingHitlHandler = null,
        long meetingStartRunSequence = 1,
        MeetingHitlResponse? resumedMeetingHitlResponse = null,
        MeetingInvocationRecord? resumedMeetingInvocation = null,
        ConversationRecordV1? resumedCanonicalMessage = null,
        RuntimeError? resumedMeetingFailure = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(toolCatalogStore);
        ArgumentNullException.ThrowIfNull(toolCatalogSnapshot);

        await using var repositoryConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        var repository = new SqliteSessionRepository(repositoryConnection);
        await using var outboxConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        using var outbox = new SqliteEventOutbox(
            outboxConnection,
            Path.Combine(_dataDirectory, "replay"),
            SqliteEventOutbox.DefaultMaxReplayBytes);
        await using var toolConnection = await OpenDatabaseConnectionAsync(ct)
            .ConfigureAwait(false);
        using var toolStateStore = new SqliteToolIntentRepository(toolConnection);
        var authorizationService = new ToolAuthorizationService(
            toolStateStore,
            RuntimeToolPolicy.CreateRestricted(_workspaceRoot),
            TimeProvider.System);
        var reverseExecutor = controlPeer is null
            ? null
            : new ReverseRpcToolExecutor(controlPeer, _resultBlobStore);
        var executors = new List<IToolExecutor>
        {
            new BuiltinFsToolExecutor(),
            new BuiltinContentToolExecutor(),
            new BuiltinProcessToolExecutor(),
            new BuiltinPowerShellToolExecutor(),
            new BuiltinHttpToolExecutor(),
            new BuiltinMemoryToolExecutor(_memoryFiles, _memorySnapshots)
        };
        if (reverseExecutor is not null)
        {
            executors.Add(reverseExecutor);
        }

        using var toolGateway = new ToolGateway(
            toolCatalogStore,
            new JsonSchemaToolValidator(),
            authorizationService,
            executors,
            toolStateStore,
            outbox,
            resultBlobStore: _resultBlobStore,
            runtimeLimits: _runtimeLimits);
        var workRepo = new SqliteWorkRepository(repositoryConnection);
        var recoveryService = new RecoveryService(
            repository,
            toolStateStore,
            toolGateway,
            toolCatalogSnapshot,
            workRepo);
        await recoveryService.RecoverSentToolIntentsAsync(ct).ConfigureAwait(false);
        var toolConsentCoordinator = new ToolConsentCoordinator(
            toolGateway,
            authorizationService,
            toolCatalogSnapshot,
            reverseExecutor is null ? null : reverseExecutor.RequestPermissionAsync,
            reverseExecutor is null ? null : reverseExecutor.RequestApprovalAsync,
            toolStateStore);
        if (resumedMeetingHitlResponse is null && resumedMeetingInvocation is null)
        {
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Accepted,
                RunStatus.Preparing,
                ct).ConfigureAwait(false);
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Preparing,
                RunStatus.Running,
                ct).ConfigureAwait(false);
        }

        IModeOrchestrator orchestrator = request.Mode switch
        {
            RuntimeMode.Expert => new ExpertModeOrchestrator(
                provider,
                new ConversationStore(_dataDirectory),
                repository,
                outbox,
                _agentContextComposer,
                credentialRefreshHandler,
                toolGateway,
                authorizationService,
                toolCatalogSnapshot,
                _memorySnapshots,
                _runtimeLimits,
                reverseExecutor is null ? null : reverseExecutor.RequestPermissionAsync,
                reverseExecutor is null ? null : reverseExecutor.RequestApprovalAsync,
                toolStateStore,
                isRunTimedOut),

            RuntimeMode.Meeting => new MeetingModeOrchestrator(
                providerFactory: requestedProviderId =>
                {
                    if (providerFactory is not null)
                    {
                        return providerFactory(requestedProviderId);
                    }

                    if (string.Equals(
                            requestedProviderId,
                            provider.ProviderId,
                            StringComparison.Ordinal))
                    {
                        return provider;
                    }

                    throw new InvalidOperationException(
                        $"Provider '{requestedProviderId}' is not available. "
                        + $"Only '{provider.ProviderId}' is configured in the current runtime session.");
                },
                new ConversationStore(_dataDirectory),
                repository,
                new SqliteMeetingRepository(repositoryConnection),
                outbox,
                _agentContextComposer,
                emitAccepted: false,
                startRunSequence: meetingStartRunSequence,
                hitlHandler: meetingHitlHandler,
                toolGateway: toolGateway,
                toolCatalogSnapshot: toolCatalogSnapshot,
                runtimeLimits: _runtimeLimits,
                toolConsentCoordinator: toolConsentCoordinator,
                resumedHitlResponse: resumedMeetingHitlResponse,
                resumedInvocation: resumedMeetingInvocation,
                resumedCanonicalMessage: resumedCanonicalMessage,
                resumedFailure: resumedMeetingFailure),
            RuntimeMode.Work => new WorkModeOrchestrator(
                providerFactory: requestedProviderId =>
                {
                    if (providerFactory is not null)
                    {
                        return providerFactory(requestedProviderId);
                    }

                    if (string.Equals(
                            requestedProviderId,
                            provider.ProviderId,
                            StringComparison.Ordinal))
                    {
                        return provider;
                    }

                    throw new InvalidOperationException(
                        $"Provider '{requestedProviderId}' is not available. "
                        + $"Only '{provider.ProviderId}' is configured in the current runtime session.");
                },
                new ConversationStore(_dataDirectory),
                repository,
                outbox,
                _agentContextComposer,
                toolStateStore: toolStateStore,
                toolGateway: toolGateway,
                toolCatalogSnapshot: toolCatalogSnapshot,
                runtimeLimits: _runtimeLimits,
                toolConsentCoordinator: toolConsentCoordinator,
                workRepo: workRepo,
                connection: repositoryConnection),

            _ => throw new NotSupportedException(
                $"Runtime mode '{request.Mode}' is not supported.")
        };

        await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                request,
                _runtimeInstanceId,
                ct)
            .ConfigureAwait(false))
        {
            yield return envelope;
        }
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
}
