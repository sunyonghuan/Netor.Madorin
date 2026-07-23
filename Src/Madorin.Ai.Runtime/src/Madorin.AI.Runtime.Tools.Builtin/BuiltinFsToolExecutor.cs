using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed partial class BuiltinFsToolExecutor : IToolExecutor
{
    private const int MaxFileSizeBytes = 1024 * 1024;
    private const int MaxListEntries = 10_000;

    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public bool CanExecute(string toolId) =>
        string.Equals(toolId, BuiltinToolRegistry.FileListToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileStatusToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileReadToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileWriteToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.DirectoryCreateToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileCopyToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileMoveToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileDeleteToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FileSearchToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.FilePatchToolId, StringComparison.Ordinal);

    public ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IPreparedToolExecution>(PrepareCore(invocation));
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
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
            return Failure(invocation, ex.Message);
        }
    }

    private static string GetRequiredString(JsonElement arguments, string propertyName) =>
        GetOptionalString(arguments, propertyName)
        ?? throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");

    private static string? GetOptionalString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    private static ToolResult Failure(ToolInvocation invocation, string error) =>
        new(invocation.CallId, invocation.ToolId, false, Error: error);
}
