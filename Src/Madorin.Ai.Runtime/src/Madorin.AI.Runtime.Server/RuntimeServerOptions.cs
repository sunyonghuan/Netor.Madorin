namespace Madorin.AI.Runtime.Server;

public sealed record RuntimeServerOptions(
    string WorkspaceRoot,
    string DataDirectory,
    string ConfigDirectory,
    string LogDirectory,
    string InstanceId,
    string PipePrefix,
    int MaxConcurrentRuns);
