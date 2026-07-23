using System.Security.Cryptography;
using System.Text;

namespace Madorin.AI.Runtime.Server;

/// <summary>Owns the process-wide write lease for one normalized workspace.</summary>
public sealed class WorkspaceWriteLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private WorkspaceWriteLock(FileStream stream, string lockPath)
    {
        _stream = stream;
        LockPath = lockPath;
    }

    /// <summary>Gets the system-temporary lock file used by this lease.</summary>
    public string LockPath { get; }

    /// <summary>Acquires the write lease for a workspace.</summary>
    public static async Task<WorkspaceWriteLock> AcquireAsync(
        string workspaceRoot,
        string runtimeInstanceId,
        TimeSpan? waitTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        var timeout = waitTimeout ?? TimeSpan.Zero;
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        var normalizedWorkspace = NormalizeWorkspace(workspaceRoot);
        var workspaceKey = ComputeWorkspaceKey(normalizedWorkspace);
        var lockDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin.ai.runtime",
            "workspace-locks");
        Directory.CreateDirectory(lockDirectory);
        SetUnixMode(
            lockDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = Path.Combine(lockDirectory, workspaceKey + ".lock");
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? holder = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                });
                SetUnixMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await WriteHolderMetadataAsync(stream, runtimeInstanceId, ct).ConfigureAwait(false);
                return new WorkspaceWriteLock(stream, lockPath);
            }
            catch (IOException)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                holder = await TryReadHolderAsync(lockPath, ct).ConfigureAwait(false);
                if (timeout == TimeSpan.Zero || DateTimeOffset.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"The workspace already has an active Runtime instance. " +
                        $"Lock: '{lockPath}'. Holder: {holder ?? "unavailable"}.");
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    continue;
                }

                await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(50)
                            ? remaining
                            : TimeSpan.FromMilliseconds(50),
                        ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    internal static string NormalizeWorkspace(string workspaceRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    internal static string ComputeWorkspaceKey(string normalizedWorkspace) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedWorkspace)));

    private static async Task WriteHolderMetadataAsync(
        FileStream stream,
        string runtimeInstanceId,
        CancellationToken ct)
    {
        var metadata = $"runtimeInstanceId={runtimeInstanceId}\n" +
            $"pid={Environment.ProcessId}\n" +
            $"startedAt={DateTimeOffset.UtcNow:O}\n";
        var bytes = Encoding.UTF8.GetBytes(metadata);
        stream.SetLength(0);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string?> TryReadHolderAsync(string lockPath, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
