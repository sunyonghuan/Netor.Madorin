namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Controls Meeting repository durability fault injection.</summary>
public sealed record SqliteMeetingRepositoryOptions
{
    public Func<SqliteMeetingRepositoryFailurePoint, CancellationToken, ValueTask>? FailureInjector { get; init; }
}
