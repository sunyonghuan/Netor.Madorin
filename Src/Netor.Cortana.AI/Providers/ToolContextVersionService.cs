using System.Threading;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// Tracks changes to the dynamic tool context used when building AI agents.
/// </summary>
public sealed class ToolContextVersionService
{
    private long _version;

    /// <summary>
    /// Gets the current tool context version.
    /// </summary>
    public long Current => Interlocked.Read(ref _version);

    /// <summary>
    /// Increments the current tool context version and returns the new value.
    /// </summary>
    public long Bump() => Interlocked.Increment(ref _version);
}
