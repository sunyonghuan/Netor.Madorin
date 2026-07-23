using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Reads bounded content pages with deterministic metadata and digest generation.</summary>
public sealed class BuiltinContentToolExecutor : IToolExecutor
{
    private const int DefaultPageLength = 64 * 1024;
    private const int MaxBinaryPageLength = 48 * 1024;
    private const long MaxContentFileSize = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BigEndian = new(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    public bool CanExecute(string toolId) => string.Equals(
        toolId,
        BuiltinToolRegistry.ContentReadToolId,
        StringComparison.Ordinal);

    public ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The content executor cannot execute this tool.");
        }

        var permission = invocation.PermissionContext
            ?? throw new UnauthorizedAccessException("A tool permission grant is required.");
        if (!string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The tool permission grant belongs to another Run.");
        }

        using var document = JsonDocument.Parse(invocation.ArgumentsJson);
        var arguments = document.RootElement;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tool arguments must be a JSON object.");
        }

        var requestedPath = GetRequiredString(arguments, "path");
        var offset = GetOptionalInt64(arguments, "offset") ?? 0;
        var length = GetOptionalInt32(arguments, "length") ?? DefaultPageLength;
        if (offset < 0 || length is < 1 or > DefaultPageLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invocation),
                "Content offset or length is outside the allowed range.");
        }

        var policy = new BuiltinFsPathPolicy(permission);
        var file = policy.PinReadFile(requestedPath);
        return ValueTask.FromResult<IPreparedToolExecution>(
            new PreparedContentExecution(
                invocation,
                file,
                offset,
                length,
                policy.GetTargetSummary(file.Path)));
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        try
        {
            await using var prepared = await PrepareAsync(invocation, ct).ConfigureAwait(false);
            return await prepared.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            return new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                false,
                Error: ex.Message);
        }
    }

    private static async Task<ToolResult> ReadPageAsync(
        ToolInvocation invocation,
        PinnedPath file,
        long offset,
        int requestedLength,
        CancellationToken ct)
    {
        var fileLength = RandomAccess.GetLength(file.Handle);
        if (fileLength > MaxContentFileSize)
        {
            throw new InvalidDataException("The content file exceeds the 64 MB limit.");
        }

        if (offset > fileLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "The content offset is beyond the end of the file.");
        }

        var prefix = await ReadAtMostAsync(file.Handle, 0, 4096, ct).ConfigureAwait(false);
        var format = DetectFormat(file.Path, prefix);
        if (format.BomLength > 0 && offset > 0 && offset < format.BomLength)
        {
            throw new ArgumentException("The content offset falls inside the encoding preamble.");
        }

        var effectiveOffset = offset == 0 ? format.BomLength : offset;
        if (format.Encoding is UnicodeEncoding
            && (effectiveOffset - format.BomLength) % 2 != 0)
        {
            throw new ArgumentException("UTF-16 content offsets must align to a code unit.");
        }

        var pageLength = format.IsBinary
            ? Math.Min(requestedLength, MaxBinaryPageLength)
            : requestedLength;
        var page = await ReadAtMostAsync(
            file.Handle,
            effectiveOffset,
            pageLength,
            ct).ConfigureAwait(false);
        string content;
        int consumed;
        if (format.IsBinary)
        {
            content = Convert.ToBase64String(page);
            consumed = page.Length;
        }
        else
        {
            (content, consumed) = DecodePage(format.Encoding!, page);
        }

        var nextOffsetValue = effectiveOffset + consumed;
        long? nextOffset = nextOffsetValue < fileLength ? nextOffsetValue : null;
        var sha256 = await ComputeHashAsync(file.Handle, fileLength, ct).ConfigureAwait(false);
        var summary = format.IsBinary
            ? $"Binary content, {fileLength} bytes."
            : CreateSummary(content);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("content", content);
            writer.WriteString("encoding", format.Name);
            writer.WriteString("mediaType", format.MediaType);
            writer.WriteString("sha256", sha256);
            writer.WriteNumber("offset", offset);
            if (nextOffset is { } value)
            {
                writer.WriteNumber("nextOffset", value);
            }
            else
            {
                writer.WriteNull("nextOffset");
            }

            writer.WriteString("summary", summary);
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private static ContentFormat DetectFormat(string path, ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 3
            && prefix[0] == 0xEF
            && prefix[1] == 0xBB
            && prefix[2] == 0xBF)
        {
            return new ContentFormat("utf-8", "text/plain", Utf8, 3, IsBinary: false);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return new ContentFormat(
                "utf-16le",
                "text/plain",
                Utf16LittleEndian,
                2,
                IsBinary: false);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return new ContentFormat(
                "utf-16be",
                "text/plain",
                Utf16BigEndian,
                2,
                IsBinary: false);
        }

        var mediaType = GetMediaType(path, prefix);
        var knownText = mediaType.StartsWith("text/", StringComparison.Ordinal)
            || mediaType is "application/json" or "application/xml";
        var isBinary = !knownText && LooksBinary(prefix);
        return isBinary
            ? new ContentFormat("base64", mediaType, Encoding: null, 0, IsBinary: true)
            : new ContentFormat("utf-8", mediaType, Utf8, 0, IsBinary: false);
    }

    private static string GetMediaType(string path, ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 8
            && prefix[0] == 0x89
            && prefix[1] == 0x50
            && prefix[2] == 0x4E
            && prefix[3] == 0x47
            && prefix[4] == 0x0D
            && prefix[5] == 0x0A
            && prefix[6] == 0x1A
            && prefix[7] == 0x0A)
        {
            return "image/png";
        }

        if (prefix.Length >= 3
            && prefix[0] == 0xFF
            && prefix[1] == 0xD8
            && prefix[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (prefix.StartsWith("%PDF"u8))
        {
            return "application/pdf";
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".csx" => "text/x-csharp",
            ".txt" or ".md" or ".log" => "text/plain",
            ".json" => "application/json",
            ".xml" or ".csproj" or ".props" or ".targets" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" => "text/javascript",
            ".csv" => "text/csv",
            ".yaml" or ".yml" => "text/yaml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static bool LooksBinary(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
        {
            return false;
        }

        var controlBytes = 0;
        foreach (var value in prefix)
        {
            if (value == 0)
            {
                return true;
            }

            if (value < 0x09 || value is > 0x0D and < 0x20)
            {
                controlBytes++;
            }
        }

        return controlBytes > prefix.Length / 20;
    }

    private static (string Content, int Consumed) DecodePage(
        Encoding encoding,
        byte[] page)
    {
        var minimum = encoding is UnicodeEncoding ? page.Length % 2 : 0;
        var maxTrim = encoding is UTF8Encoding ? Math.Min(3, page.Length) : minimum;
        for (var trim = minimum; trim <= maxTrim; trim += encoding is UnicodeEncoding ? 2 : 1)
        {
            try
            {
                var consumed = page.Length - trim;
                return (encoding.GetString(page, 0, consumed), consumed);
            }
            catch (DecoderFallbackException) when (trim < maxTrim)
            {
            }
        }

        throw new InvalidDataException("The requested content page is not valid text.");
    }

    private static async Task<byte[]> ReadAtMostAsync(
        SafeFileHandle handle,
        long offset,
        int length,
        CancellationToken ct)
    {
        var available = Math.Max(0, RandomAccess.GetLength(handle) - offset);
        var bytes = new byte[checked((int)Math.Min(length, available))];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                bytes.AsMemory(total),
                offset + total,
                ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == bytes.Length ? bytes : bytes[..total];
    }

    private static async Task<string> ComputeHashAsync(
        SafeFileHandle handle,
        long length,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                var count = (int)Math.Min(buffer.Length, length - offset);
                var read = await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(0, count),
                    offset,
                    ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The content changed while it was hashed.");
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string CreateSummary(string content)
    {
        var builder = new StringBuilder(Math.Min(content.Length, 512));
        var pendingSpace = false;
        foreach (var value in content)
        {
            if (char.IsWhiteSpace(value))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && builder.Length < 512)
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            if (builder.Length >= 512)
            {
                break;
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static string GetRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");
        }

        return property.GetString()
            ?? throw new ArgumentException($"Tool argument '{propertyName}' cannot be null.");
    }

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

    private static long? GetOptionalInt64(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be an integer.");
        }

        return value;
    }

    private sealed class PreparedContentExecution(
        ToolInvocation invocation,
        PinnedPath file,
        long offset,
        int length,
        string targetSummary) : IPreparedToolExecution
    {
        private int _disposed;

        public string TargetSummary { get; } = targetSummary;

        public Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return ReadPageAsync(invocation, file, offset, length, ct);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                file.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record ContentFormat(
        string Name,
        string MediaType,
        Encoding? Encoding,
        int BomLength,
        bool IsBinary);
}
