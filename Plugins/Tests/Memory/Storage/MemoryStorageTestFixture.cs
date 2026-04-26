using Cortana.Plugins.Memory.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Netor.Cortana.Plugin;

namespace Memory.Test.Storage;

internal sealed class MemoryStorageTestFixture : IDisposable
{
    private readonly string _directory;

    public MemoryStorageTestFixture()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cortana-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var settings = new PluginSettings(
            _directory,
            workspaceDirectory: "workspace-test",
            pluginDirectory: _directory,
            wsPort: 0,
            chatWsEndpoint: string.Empty,
            conversationFeedEndpoint: string.Empty,
            conversationFeedProtocol: string.Empty,
            conversationFeedVersion: string.Empty);

        Database = new SqliteMemoryDatabase(settings, NullLogger<SqliteMemoryDatabase>.Instance);
        ObservationRecords = new ObservationRecordsTable(Database);
        MemoryFragments = new MemoryFragmentsTable(Database);
        MemoryAbstractions = new MemoryAbstractionsTable(Database);
        MemoryLinks = new MemoryLinksTable(Database);
        MemoryEvents = new MemoryEventsTable(Database);
        RecallLogs = new RecallLogsTable(Database);
        MemoryMutations = new MemoryMutationsTable(Database);
        MemorySettings = new MemorySettingsTable(Database);
        ProcessingStates = new MemoryProcessingStatesTable(Database);
        Store = new MemoryStore(
            Database,
            ObservationRecords,
            MemoryFragments,
            MemoryAbstractions,
            MemoryLinks,
            MemoryEvents,
            RecallLogs,
            MemoryMutations,
            MemorySettings,
            ProcessingStates,
            NullLogger<MemoryStore>.Instance);

        Store.EnsureInitialized();
    }

    public IMemoryDatabase Database { get; }

    public ObservationRecordsTable ObservationRecords { get; }

    public MemoryFragmentsTable MemoryFragments { get; }

    public MemoryAbstractionsTable MemoryAbstractions { get; }

    public MemoryLinksTable MemoryLinks { get; }

    public MemoryEventsTable MemoryEvents { get; }

    public RecallLogsTable RecallLogs { get; }

    public MemoryMutationsTable MemoryMutations { get; }

    public MemorySettingsTable MemorySettings { get; }

    public MemoryProcessingStatesTable ProcessingStates { get; }

    public MemoryStore Store { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
