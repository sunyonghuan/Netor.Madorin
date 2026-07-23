namespace Madorin.AI.Runtime.Cli.Config;

public sealed class ConfigBusyException : IOException
{
    public ConfigBusyException(string lockPath, TimeSpan timeout, Exception? innerException)
        : base(
            $"Configuration is busy because lock '{lockPath}' could not be acquired within {timeout.TotalSeconds:0} seconds.",
            innerException)
    {
        LockPath = lockPath;
        Timeout = timeout;
    }

    public string LockPath { get; }

    public TimeSpan Timeout { get; }
}
