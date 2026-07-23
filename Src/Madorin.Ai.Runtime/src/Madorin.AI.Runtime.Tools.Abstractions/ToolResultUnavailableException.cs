namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Indicates that a sent call may have completed but its result is not currently observable.</summary>
public sealed class ToolResultUnavailableException : IOException
{
    public ToolResultUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
