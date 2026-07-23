namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Identifies a durability boundary used by conversation-store fault injection.</summary>
public enum ConversationStoreFailurePoint
{
    BeforeFileAppend,
    AfterRecordPrefix,
    AfterRecordBytes,
    AfterLineTerminator,
    AfterFileFlush,
    BeforeIndexWrite,
    AfterIndexWrite
}
