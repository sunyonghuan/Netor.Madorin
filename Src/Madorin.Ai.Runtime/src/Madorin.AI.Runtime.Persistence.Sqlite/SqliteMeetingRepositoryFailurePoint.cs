namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Identifies a committed Meeting durability boundary used by fault injection.</summary>
public enum SqliteMeetingRepositoryFailurePoint
{
    AfterRoundCreationCommit,
    AfterInvocationCompletionCommit,
    AfterSelectorDecisionCommit
}
