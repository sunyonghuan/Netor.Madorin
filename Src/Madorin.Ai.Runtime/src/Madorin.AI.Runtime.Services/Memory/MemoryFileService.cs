using System.Security.Cryptography;
using System.Text;

namespace Madorin.AI.Runtime.Services.Memory;

public sealed class MemoryFileService
{
    public const int MaximumFileSizeBytes = 32 * 1024;

    private static readonly TimeSpan WriteLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(10);
    private const string EmptyMemory = "# Memory\n";
    private const string GlobalMemoryTemplate = """
        # Memory

        - Add stable cross-project rules and preferences here.
        """;
    private const string ProjectMemoryTemplate = """
        # Memory

        - Add stable project-specific rules and facts here.
        """;

    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _globalPath;
    private readonly string? _projectPath;

    public MemoryFileService(string userHome, string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        if (workspaceRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        }

        _globalPath = ResolveGlobalPath(userHome);
        _projectPath = workspaceRoot is not null
            ? ResolveProjectPath(workspaceRoot)
            : null;
    }

    public string GlobalPath => _globalPath;

    public string? ProjectPath => _projectPath;

    public static string ResolveGlobalPath(string userHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        return Path.Combine(userHome, ".madorin", "memory.md");
    }

    public static string ResolveProjectPath(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, ".madorin", "memory.md");
    }

    public async Task<string?> ReadAsync(
        MemoryScope scope,
        CancellationToken ct = default)
    {
        if (scope != MemoryScope.Effective)
        {
            return await ReadFileAsync(GetPath(scope), ct).ConfigureAwait(false);
        }

        var global = await ReadFileAsync(_globalPath, ct).ConfigureAwait(false);
        if (_projectPath is null || PathsEqual(_globalPath, _projectPath))
        {
            return global;
        }

        var project = await ReadFileAsync(_projectPath, ct).ConfigureAwait(false);
        return (global, project) switch
        {
            (null, null) => null,
            (not null, null) => global,
            (null, not null) => project,
            _ => $"{global}\n---\n{project}"
        };
    }

    public async Task<MemoryFileSnapshot> AppendAsync(
        MemoryScope scope,
        string item,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        if (item.Contains('\r') || item.Contains('\n'))
        {
            throw new ArgumentException("A memory item must be a single line.", nameof(item));
        }

        var path = GetPath(scope);
        await using var writeLock = await AcquireWriteLockAsync(path, ct).ConfigureAwait(false);
        var current = await ReadFileAsync(path, ct).ConfigureAwait(false);
        var content = current switch
        {
            null or "" => $"{EmptyMemory}\n- {item.Trim()}\n",
            _ when current.EndsWith('\n') => $"{current}- {item.Trim()}\n",
            _ => $"{current}\n- {item.Trim()}\n"
        };

        await AtomicWriteAsync(path, content, ct).ConfigureAwait(false);
        return new MemoryFileSnapshot(content, ComputeHash(content));
    }

    public async Task ReplaceAsync(
        MemoryScope scope,
        string content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = GetPath(scope);
        await using var writeLock = await AcquireWriteLockAsync(path, ct).ConfigureAwait(false);
        await AtomicWriteAsync(path, content, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var path = GetPath(scope);
        await using var writeLock = await AcquireWriteLockAsync(path, ct).ConfigureAwait(false);
        await AtomicWriteAsync(path, EmptyMemory, ct).ConfigureAwait(false);
    }

    public async Task InitAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var path = GetPath(scope);
        await using var writeLock = await AcquireWriteLockAsync(path, ct).ConfigureAwait(false);
        if (File.Exists(path))
        {
            return;
        }

        var template = scope == MemoryScope.Global
            ? GlobalMemoryTemplate
            : ProjectMemoryTemplate;
        await AtomicWriteAsync(path, $"{template}\n", ct).ConfigureAwait(false);
    }

    public async Task<MemoryFileSnapshot?> ReadSnapshotAsync(
        MemoryScope scope,
        CancellationToken ct = default)
    {
        var content = scope == MemoryScope.Effective
            ? await ReadAsync(scope, ct).ConfigureAwait(false)
            : await ReadFileAsync(GetPath(scope), ct).ConfigureAwait(false);
        return content is null
            ? null
            : new MemoryFileSnapshot(content, ComputeHash(content));
    }

    public async Task<string?> GetHashAsync(
        MemoryScope scope,
        CancellationToken ct = default)
    {
        if (scope is MemoryScope.Effective)
        {
            var content = await ReadAsync(scope, ct).ConfigureAwait(false);
            return content is null ? null : ComputeHash(content);
        }

        return (await ReadSnapshotAsync(scope, ct).ConfigureAwait(false))?.Hash;
    }

    private string GetPath(MemoryScope scope)
    {
        return scope switch
        {
            MemoryScope.Global => _globalPath,
            MemoryScope.Project => _projectPath
                ?? throw new InvalidOperationException(
                    "Project memory requires a workspace root."),
            MemoryScope.Effective => throw new InvalidOperationException(
                "Effective memory is read-only."),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
    }

    private static async Task<string?> ReadFileAsync(
        string path,
        CancellationToken ct)
    {
        var bytes = await ReadFileBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Memory file '{path}' is not valid UTF-8.",
                ex);
        }
    }

    private static async Task<byte[]?> ReadFileBytesAsync(
        string path,
        CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[MaximumFileSizeBytes + 1];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = await stream.ReadAsync(
                    bytes.AsMemory(total, bytes.Length - total),
                    ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaximumFileSizeBytes)
            {
                throw new InvalidDataException(
                    $"Memory file '{path}' exceeds the 32768-byte limit.");
            }

            Array.Resize(ref bytes, total);
            return bytes;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task AtomicWriteAsync(
        string path,
        string content,
        CancellationToken ct)
    {
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException(
                $"Memory content exceeds the 32768-byte limit for '{path}'.");
        }

        ct.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Join(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<FileStream> AcquireWriteLockAsync(
        string path,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var lockPath = $"{path}.lock";
        var deadline = DateTimeOffset.UtcNow + WriteLockTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(WriteLockRetryDelay, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException(
                    $"Timed out waiting for the memory write lock '{lockPath}'.",
                    ex);
            }
        }
    }

    internal static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(content)));

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            comparison);
    }
}

public sealed record MemoryFileSnapshot(string Content, string Hash);

public enum MemoryScope
{
    Global,
    Project,
    Effective
}
