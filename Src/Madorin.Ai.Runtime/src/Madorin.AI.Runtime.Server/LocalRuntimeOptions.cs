using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.Server;

public sealed record LocalRuntimeOptions(
    string WorkspaceRoot,
    string DataDirectory,
    string LogDirectory,
    string RuntimeInstanceId,
    Func<NextTurnSelection, IRuntimeProviderAdapter> ProviderResolver)
{
    /// <summary>Prebuilt memory service shared by standalone Runtime composition and REPL commands.</summary>
    public MemoryFileService? MemoryFiles { get; init; }

    public string? MemoryUserHome { get; init; }

    public RuntimeLimits RuntimeLimits { get; init; } = new(
        MaxToolRounds: 8,
        MaxToolCallsPerRound: 8,
        MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);

    public long MaxGlobalBlobBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public TimeSpan WorkspaceLockTimeout { get; init; } = TimeSpan.Zero;
}
