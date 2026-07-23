namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Reports the outcome of rebuilding message indexes from JSONL.</summary>
public sealed record ConversationIndexRebuildResult(
    int SessionsScanned,
    int SessionsRebuilt,
    int MessagesUpserted,
    string[] RecoverableItems,
    string[] UnrecoverableDiagnostics,
    string? BackupPath);
