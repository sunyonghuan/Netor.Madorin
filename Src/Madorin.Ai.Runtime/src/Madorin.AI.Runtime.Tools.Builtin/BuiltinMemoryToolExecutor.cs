using System.Text.Json;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed class BuiltinMemoryToolExecutor(
    MemoryFileService memoryFileService,
    MemoryInvocationSnapshotStore invocationSnapshots) : IToolExecutor
{
    private readonly MemoryFileService _memoryFileService = memoryFileService
        ?? throw new ArgumentNullException(nameof(memoryFileService));
    private readonly MemoryInvocationSnapshotStore _invocationSnapshots = invocationSnapshots
        ?? throw new ArgumentNullException(nameof(invocationSnapshots));

    public bool CanExecute(string toolId) =>
        string.Equals(toolId, BuiltinToolRegistry.MemoryReadToolId, StringComparison.Ordinal)
        || string.Equals(toolId, BuiltinToolRegistry.MemoryAppendToolId, StringComparison.Ordinal);

    public async Task<ToolResult> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CanExecute(invocation.ToolId))
        {
            return Failure(invocation, "The memory executor cannot execute this tool.");
        }

        if (string.Equals(
                invocation.ToolId,
                BuiltinToolRegistry.MemoryAppendToolId,
                StringComparison.Ordinal)
            && !HasValidApproval(invocation))
        {
            return Failure(invocation, "An explicit user approval grant is required.");
        }

        try
        {
            using var arguments = JsonDocument.Parse(invocation.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Tool arguments must be a JSON object.");
            }

            return invocation.ToolId switch
            {
                BuiltinToolRegistry.MemoryReadToolId =>
                    Read(invocation, arguments.RootElement, ct),
                BuiltinToolRegistry.MemoryAppendToolId =>
                    await AppendAsync(invocation, arguments.RootElement, ct).ConfigureAwait(false),
                _ => Failure(invocation, "The memory tool is not implemented.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
            or ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or TimeoutException)
        {
            return Failure(invocation, ex.Message);
        }
    }

    private ToolResult Read(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = ReadScope(arguments, allowEffective: true);
        if (invocation.InvocationId is null
            || !_invocationSnapshots.TryGet(invocation.InvocationId, out var context))
        {
            throw new InvalidOperationException(
                "The Invocation memory snapshot is unavailable or has expired.");
        }

        var snapshot = context.Get(scope);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (snapshot is null)
            {
                writer.WriteNull("content");
                writer.WriteNull("hash");
            }
            else
            {
                writer.WriteString("content", snapshot.Content);
                writer.WriteString("hash", snapshot.Hash);
            }

            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private async Task<ToolResult> AppendAsync(
        ToolInvocation invocation,
        JsonElement arguments,
        CancellationToken ct)
    {
        var scope = ReadScope(arguments, allowEffective: false);
        var item = GetRequiredString(arguments, "item");
        var snapshot = await _memoryFileService.AppendAsync(scope, item, ct)
            .ConfigureAwait(false);
        var output = ToolJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("appended", true);
            writer.WriteString("hash", snapshot.Hash);
            writer.WriteString("appliesFrom", "nextInvocation");
            writer.WriteEndObject();
        });
        return new ToolResult(invocation.CallId, invocation.ToolId, true, output);
    }

    private static MemoryScope ReadScope(JsonElement arguments, bool allowEffective)
    {
        var value = GetRequiredString(arguments, "scope");
        if (!Enum.TryParse<MemoryScope>(value, ignoreCase: true, out var scope)
            || (!allowEffective && scope == MemoryScope.Effective))
        {
            throw new ArgumentException($"Memory scope '{value}' is not supported.");
        }

        return scope;
    }

    private static string GetRequiredString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");
        }

        return property.GetString()!;
    }

    private static bool HasValidApproval(ToolInvocation invocation) =>
        invocation.PermissionContext is { } permission
        && string.Equals(permission.RunId, invocation.RunId, StringComparison.Ordinal)
        && (permission.ExpiresAt is null || permission.ExpiresAt > DateTimeOffset.UtcNow);

    private static ToolResult Failure(ToolInvocation invocation, string error) =>
        new(invocation.CallId, invocation.ToolId, false, Error: error);
}
