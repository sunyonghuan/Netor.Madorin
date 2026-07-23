using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed partial class BuiltinFsToolExecutor
{
    private const int MaxSearchFiles = 10_000;
    private const int MaxSearchResults = 100;
    private const long MaxTransferFileSizeBytes = 64L * 1024 * 1024;

    private FsPreparedExecution PrepareCore(ToolInvocation invocation)
    {
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The file executor cannot execute this tool.");
        }

        var permission = invocation.PermissionContext
            ?? throw new UnauthorizedAccessException("A tool permission grant is required.");
        if (!string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The tool permission grant belongs to another Run.");
        }

        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool arguments must be a JSON object.");
        }

        var arguments = document.RootElement.Clone();
        var policy = new BuiltinFsPathPolicy(permission);
        return invocation.ToolId switch
        {
            BuiltinToolRegistry.FileListToolId => PrepareList(invocation, arguments, policy),
            BuiltinToolRegistry.FileStatusToolId => PrepareStatus(invocation, arguments, policy),
            BuiltinToolRegistry.FileReadToolId => PrepareRead(invocation, arguments, policy),
            BuiltinToolRegistry.FileWriteToolId =>
                PrepareWrite(invocation, arguments, permission, policy),
            BuiltinToolRegistry.DirectoryCreateToolId =>
                PrepareDirectoryCreate(invocation, arguments, policy),
            BuiltinToolRegistry.FileCopyToolId =>
                PrepareCopy(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileMoveToolId =>
                PrepareMove(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileDeleteToolId =>
                PrepareDelete(invocation, arguments, permission, policy),
            BuiltinToolRegistry.FileSearchToolId => PrepareSearch(invocation, arguments, policy),
            BuiltinToolRegistry.FilePatchToolId =>
                PreparePatch(invocation, arguments, permission, policy),
            _ => throw new InvalidOperationException("The file tool is not implemented.")
        };
    }

    private static FsPreparedExecution PrepareList(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var directory = policy.PinReadDirectory(GetOptionalString(arguments, "path") ?? ".");
        return new FsPreparedExecution(
            policy.GetTargetSummary(directory.Path),
            ct => Task.FromResult(List(invocation, directory, policy, ct)),
            [directory]);
    }

    private static FsPreparedExecution PrepareStatus(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var requestedPath = GetRequiredString(arguments, "path");
        var path = policy.ResolveReadPath(requestedPath);
        PinnedPath? target = File.Exists(path)
            ? policy.PinReadFile(requestedPath)
            : Directory.Exists(path)
                ? policy.PinReadDirectory(requestedPath)
                : null;
        return new FsPreparedExecution(
            policy.GetTargetSummary(path),
            ct => Task.FromResult(Status(invocation, path, target, ct)),
            target is null ? [] : [target]);
    }

    private static FsPreparedExecution PrepareRead(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var file = policy.PinReadFile(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(file.Path),
            ct => ReadAsync(invocation, file, ct),
            [file]);
    }

    private static FsPreparedExecution PrepareWrite(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        var parent = policy.PinWriteParent(GetRequiredString(arguments, "path"));
        if ((File.Exists(parent.Path) || Directory.Exists(parent.Path))
            && !permission.AllowOverwrite)
        {
            parent.Dispose();
            throw new UnauthorizedAccessException("Overwriting this path is not permitted.");
        }

        return new FsPreparedExecution(
            policy.GetTargetSummary(parent.Path),
            ct => WriteAsync(invocation, arguments, permission, policy, parent, ct),
            [parent]);
    }

    private static FsPreparedExecution PrepareDirectoryCreate(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var parent = policy.PinWriteParent(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(parent.Path),
            ct => Task.FromResult(CreateDirectory(invocation, parent, policy, ct)),
            [parent]);
    }

    private static FsPreparedExecution PrepareCopy(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        var source = policy.PinReadFile(GetRequiredString(arguments, "source"));
        try
        {
            var destination = policy.PinWriteParent(GetRequiredString(arguments, "destination"));
            if ((File.Exists(destination.Path) || Directory.Exists(destination.Path))
                && !permission.AllowOverwrite)
            {
                destination.Dispose();
                throw new UnauthorizedAccessException(
                    "Overwriting the copy destination is not permitted.");
            }

            return new FsPreparedExecution(
                $"{policy.GetTargetSummary(source.Path)}->{policy.GetTargetSummary(destination.Path)}",
                ct => CopyAsync(
                    invocation,
                    permission,
                    policy,
                    source,
                    destination,
                    ct),
                [source, destination]);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static FsPreparedExecution PrepareMove(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowMove)
        {
            throw new UnauthorizedAccessException("Moving paths is not permitted.");
        }

        var source = policy.PinWriteParent(GetRequiredString(arguments, "source"));
        if (!File.Exists(source.Path) && !Directory.Exists(source.Path))
        {
            source.Dispose();
            throw new FileNotFoundException("The move source was not found.", source.Path);
        }

        try
        {
            var destination = policy.PinWriteParent(GetRequiredString(arguments, "destination"));
            return new FsPreparedExecution(
                $"{policy.GetTargetSummary(source.Path)}->{policy.GetTargetSummary(destination.Path)}",
                ct => Task.FromResult(Move(invocation, permission, policy, source, destination, ct)),
                [source, destination]);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static FsPreparedExecution PrepareDelete(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowDelete)
        {
            throw new UnauthorizedAccessException("Deleting paths is not permitted.");
        }

        var parent = policy.PinWriteParent(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(parent.Path),
            ct => Task.FromResult(Delete(invocation, policy, parent, ct)),
            [parent]);
    }

    private static FsPreparedExecution PrepareSearch(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy)
    {
        var directory = policy.PinReadDirectory(GetRequiredString(arguments, "path"));
        return new FsPreparedExecution(
            policy.GetTargetSummary(directory.Path),
            ct => SearchAsync(invocation, arguments, directory, policy, ct),
            [directory]);
    }

    private static FsPreparedExecution PreparePatch(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy)
    {
        if (!permission.AllowOverwrite)
        {
            throw new UnauthorizedAccessException("Patching an existing file requires overwrite permission.");
        }

        var file = policy.PinReadFile(
            GetRequiredString(arguments, "path"),
            allowDeleteSharing: true);
        try
        {
            var parent = policy.PinWriteParent(file.Path);
            return new FsPreparedExecution(
                policy.GetTargetSummary(file.Path),
                ct => PatchAsync(invocation, arguments, policy, file, parent, ct),
                [file, parent]);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static ToolResult List(
        ToolInvocation invocation,
        PinnedPath directory,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        var entries = new List<FileSystemInfo>();
        foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _ = policy.ResolveReadPath(entry.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            entries.Add(entry);
            if (entries.Count > MaxListEntries)
            {
                throw new InvalidDataException(
                    $"Directory listing exceeds the {MaxListEntries}-entry limit.");
            }
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteString("kind", entry is DirectoryInfo ? "directory" : "file");
                if (entry is FileInfo file)
                {
                    writer.WriteNumber("byteLength", file.Length);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        EnsureResultSize(output, "Directory listing");
        return Success(invocation, output);
    }

    private static ToolResult Status(
        ToolInvocation invocation,
        string path,
        PinnedPath? target,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exists = target is not null;
        var isDirectory = exists && Directory.Exists(path);
        long? byteLength = exists && !isDirectory
            ? RandomAccess.GetLength(target!.Handle)
            : null;
        var lastWriteTime = exists
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            : (DateTimeOffset?)null;
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("exists", exists);
            if (exists)
            {
                writer.WriteString("kind", isDirectory ? "directory" : "file");
            }
            else
            {
                writer.WriteNull("kind");
            }

            if (byteLength is { } length)
            {
                writer.WriteNumber("byteLength", length);
            }
            else
            {
                writer.WriteNull("byteLength");
            }

            if (lastWriteTime is { } timestamp)
            {
                writer.WriteString("lastWriteTime", timestamp);
            }
            else
            {
                writer.WriteNull("lastWriteTime");
            }

            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<ToolResult> ReadAsync(
        ToolInvocation invocation,
        PinnedPath file,
        CancellationToken ct)
    {
        var bytes = await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct).ConfigureAwait(false);
        var content = Utf8.GetString(bytes);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("content", content);
            writer.WriteString("encoding", "utf-8");
            writer.WriteNumber("byteLength", bytes.Length);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<ToolResult> WriteAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy,
        PinnedPath parent,
        CancellationToken ct)
    {
        var bytes = Utf8.GetBytes(GetRequiredString(arguments, "content"));
        if (bytes.Length > MaxFileSizeBytes)
        {
            throw new InvalidDataException("The content exceeds the 1 MB write limit.");
        }

        await AtomicWriteAsync(
            parent,
            policy,
            bytes,
            permission.AllowOverwrite,
            ct).ConfigureAwait(false);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("written", true);
            writer.WriteNumber("byteLength", bytes.Length);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static ToolResult CreateDirectory(
        ToolInvocation invocation,
        PinnedPath parent,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(parent.Path))
        {
            throw new IOException("A file already exists at the requested directory path.");
        }

        var created = !Directory.Exists(parent.Path);
        if (created)
        {
            Directory.CreateDirectory(parent.Path);
        }

        _ = policy.ResolveWritePath(parent.Path);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("created", created);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<ToolResult> CopyAsync(
        ToolInvocation invocation,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy,
        PinnedPath source,
        PinnedPath destination,
        CancellationToken ct)
    {
        var length = RandomAccess.GetLength(source.Handle);
        if (length > MaxTransferFileSizeBytes)
        {
            throw new InvalidDataException("The file exceeds the 64 MB copy limit.");
        }

        var tempPath = CreateTempPath(destination);
        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                long offset = 0;
                while (offset < length)
                {
                    var count = (int)Math.Min(buffer.Length, length - offset);
                    var read = await RandomAccess.ReadAsync(
                        source.Handle,
                        buffer.AsMemory(0, count),
                        offset,
                        ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The source file changed during copy.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    offset += read;
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _ = policy.ResolveWritePath(destination.Path);
            File.Move(tempPath, destination.Path, overwrite: permission.AllowOverwrite);
        }
        finally
        {
            DeleteTempFile(tempPath);
        }

        return Success(invocation, "{\"copied\":true}");
    }

    private static ToolResult Move(
        ToolInvocation invocation,
        ToolPermissionContext permission,
        BuiltinFsPathPolicy policy,
        PinnedPath source,
        PinnedPath destination,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = policy.ResolveWritePath(source.Path);
        _ = policy.ResolveWritePath(destination.Path);
        if (Directory.Exists(source.Path))
        {
            if (File.Exists(destination.Path) || Directory.Exists(destination.Path))
            {
                throw new IOException("A directory move cannot overwrite an existing destination.");
            }

            Directory.Move(source.Path, destination.Path);
        }
        else if (File.Exists(source.Path))
        {
            if (Directory.Exists(destination.Path))
            {
                throw new IOException("The move destination is a directory.");
            }

            File.Move(source.Path, destination.Path, overwrite: permission.AllowOverwrite);
        }
        else
        {
            throw new FileNotFoundException("The move source was not found.", source.Path);
        }

        return Success(invocation, "{\"moved\":true}");
    }

    private static ToolResult Delete(
        ToolInvocation invocation,
        BuiltinFsPathPolicy policy,
        PinnedPath parent,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = policy.ResolveWritePath(parent.Path);
        var deleted = false;
        if (File.Exists(parent.Path))
        {
            File.Delete(parent.Path);
            deleted = true;
        }
        else if (Directory.Exists(parent.Path))
        {
            Directory.Delete(parent.Path, recursive: false);
            deleted = true;
        }

        return Success(invocation, $"{{\"deleted\":{deleted.ToString().ToLowerInvariant()}}}");
    }

    private static async Task<ToolResult> SearchAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        PinnedPath directory,
        BuiltinFsPathPolicy policy,
        CancellationToken ct)
    {
        var query = GetRequiredString(arguments, "query");
        if (query.Length == 0)
        {
            throw new ArgumentException("Tool argument 'query' cannot be empty.");
        }

        var maxResults = GetOptionalInt32(arguments, "maxResults") ?? MaxSearchResults;
        if (maxResults is < 1 or > MaxSearchResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"Search results must be between 1 and {MaxSearchResults}.");
        }

        var matches = new List<SearchMatch>();
        var scanned = 0;
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 32,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };
        foreach (var candidate in Directory.EnumerateFiles(directory.Path, "*", enumerationOptions))
        {
            ct.ThrowIfCancellationRequested();
            if (++scanned > MaxSearchFiles)
            {
                throw new InvalidDataException(
                    $"Search exceeds the {MaxSearchFiles}-file scan limit.");
            }

            PinnedPath file;
            try
            {
                file = policy.PinReadFile(candidate);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException
                or InvalidDataException)
            {
                continue;
            }

            using (file)
            {
                if (RandomAccess.GetLength(file.Handle) > MaxFileSizeBytes)
                {
                    continue;
                }

                string content;
                try
                {
                    content = Utf8.GetString(
                        await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct)
                            .ConfigureAwait(false));
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                var lines = content.Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    var line = lines[index].TrimEnd('\r');
                    var column = line.IndexOf(query, StringComparison.Ordinal);
                    if (column < 0)
                    {
                        continue;
                    }

                    var relative = policy.GetTargetSummary(file.Path)["workspace:".Length..];
                    matches.Add(new SearchMatch(
                        relative,
                        index + 1,
                        column + 1,
                        line.Length <= 512 ? line : line[..512]));
                    if (matches.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            if (matches.Count >= maxResults)
            {
                break;
            }
        }

        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("matches");
            foreach (var match in matches)
            {
                writer.WriteStartObject();
                writer.WriteString("path", match.Path);
                writer.WriteNumber("line", match.Line);
                writer.WriteNumber("column", match.Column);
                writer.WriteString("preview", match.Preview);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        EnsureResultSize(output, "Search result");
        return Success(invocation, output);
    }

    private static async Task<ToolResult> PatchAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        BuiltinFsPathPolicy policy,
        PinnedPath file,
        PinnedPath parent,
        CancellationToken ct)
    {
        var expectedHash = GetRequiredString(arguments, "expectedHash");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Tool argument 'expectedHash' must be a SHA-256 hex value.");
        }

        var bytes = await ReadAllAsync(file.Handle, MaxFileSizeBytes, ct).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The expected SHA-256 does not match the current file ({actualHash}).");
        }

        var content = Utf8.GetString(bytes);
        var startLine = GetRequiredInt32(arguments, "startLine");
        var endLine = GetRequiredInt32(arguments, "endLine");
        if (startLine < 1 || endLine < startLine)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                "The patch line range is invalid.");
        }

        var (startOffset, endOffset) = GetLineContentRange(content, startLine, endLine);
        var replacement = GetRequiredString(arguments, "replacement");
        var patched = string.Concat(content.AsSpan(0, startOffset), replacement, content.AsSpan(endOffset));
        var patchedBytes = Utf8.GetBytes(patched);
        if (patchedBytes.Length > MaxFileSizeBytes)
        {
            throw new InvalidDataException("The patched file exceeds the 1 MB write limit.");
        }

        // Windows cannot replace an open destination. The pinned parent still fixes the
        // authorized directory while the validated file handle is released for replacement.
        file.Dispose();
        await AtomicWriteAsync(parent, policy, patchedBytes, overwrite: true, ct)
            .ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(patchedBytes));
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("patched", true);
            writer.WriteString("hash", hash);
            writer.WriteEndObject();
        });
        return Success(invocation, output);
    }

    private static async Task<byte[]> ReadAllAsync(
        SafeFileHandle handle,
        int maxBytes,
        CancellationToken ct)
    {
        var length = RandomAccess.GetLength(handle);
        if (length > maxBytes)
        {
            throw new InvalidDataException(
                $"The file exceeds the {maxBytes / (1024 * 1024)} MB read limit.");
        }

        var bytes = new byte[checked((int)length)];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                bytes.AsMemory(total),
                total,
                ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The file changed while it was being read.");
            }

            total += read;
        }

        return bytes;
    }

    private static async Task AtomicWriteAsync(
        PinnedPath parent,
        BuiltinFsPathPolicy policy,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        CancellationToken ct)
    {
        var tempPath = CreateTempPath(parent);
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            _ = policy.ResolveWritePath(parent.Path);
            File.Move(tempPath, parent.Path, overwrite);
        }
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    private static string CreateTempPath(PinnedPath parent) => Path.Join(
        parent.PinnedTargetPath,
        $".{Path.GetFileName(parent.Path)}.{Guid.NewGuid():N}.tmp");

    private static void DeleteTempFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static (int StartOffset, int EndOffset) GetLineContentRange(
        string content,
        int startLine,
        int endLine)
    {
        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        if (endLine > lineStarts.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endLine),
                $"The file contains only {lineStarts.Count} lines.");
        }

        var startOffset = lineStarts[startLine - 1];
        var newlineOffset = content.IndexOf('\n', lineStarts[endLine - 1]);
        var endOffset = newlineOffset < 0 ? content.Length : newlineOffset;
        if (endOffset > startOffset && content[endOffset - 1] == '\r')
        {
            endOffset--;
        }

        return (startOffset, endOffset);
    }

    private static int GetRequiredInt32(JsonElement arguments, string propertyName) =>
        GetOptionalInt32(arguments, propertyName)
        ?? throw new ArgumentException($"Tool argument '{propertyName}' must be an integer.");

    private static int? GetOptionalInt32(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be an integer.");
        }

        return value;
    }

    private static void EnsureResultSize(string output, string description)
    {
        if (Utf8.GetByteCount(output) > MaxFileSizeBytes)
        {
            throw new InvalidDataException($"{description} exceeds the 1 MB result limit.");
        }
    }

    private static ToolResult Success(ToolInvocation invocation, string output) =>
        new(invocation.CallId, invocation.ToolId, true, output);

    private sealed class FsPreparedExecution(
        string targetSummary,
        Func<CancellationToken, Task<ToolResult>> execute,
        IDisposable[] resources) : IPreparedToolExecution
    {
        private readonly Func<CancellationToken, Task<ToolResult>> _execute = execute;
        private readonly IDisposable[] _resources = resources;
        private int _disposed;

        public string TargetSummary { get; } = targetSummary;

        public Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _execute(ct);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                for (var index = _resources.Length - 1; index >= 0; index--)
                {
                    _resources[index].Dispose();
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record SearchMatch(string Path, int Line, int Column, string Preview);
}
