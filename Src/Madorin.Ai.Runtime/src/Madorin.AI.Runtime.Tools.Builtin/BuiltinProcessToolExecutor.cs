using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Executes explicitly allowlisted programs without invoking a command shell.</summary>
public sealed class BuiltinProcessToolExecutor : IToolExecutor
{
    public bool CanExecute(string toolId) => string.Equals(
        toolId,
        BuiltinToolRegistry.ProcessRunToolId,
        StringComparison.Ordinal);

    public ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The process executor cannot execute this tool.");
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

        var fileName = GetRequiredString(arguments, "fileName");
        var executable = BuiltinProcessRunner.ResolveAndPinExecutable(
            fileName,
            permission.AllowedExecutables);
        try
        {
            var policy = new BuiltinFsPathPolicy(permission);
            var workingDirectory = policy.PinReadDirectory(
                GetOptionalString(arguments, "workingDirectory") ?? permission.WorkspaceRoot);
            var processArguments = BuiltinProcessRunner.ParseArguments(arguments);
            var environment = BuiltinProcessRunner.BuildEnvironment(
                arguments,
                permission.AllowedEnvironmentVariables);
            var standardInput = BuiltinProcessRunner.ParseStandardInput(arguments);
            return ValueTask.FromResult<IPreparedToolExecution>(
                new PreparedProcessExecution(
                    invocation,
                    executable,
                    workingDirectory,
                    processArguments,
                    environment,
                    standardInput,
                    $"process:{Path.GetFileName(executable.Path)}"));
        }
        catch
        {
            executable.Dispose();
            throw;
        }
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

    internal static string GetRequiredString(JsonElement arguments, string propertyName) =>
        GetOptionalString(arguments, propertyName)
        ?? throw new ArgumentException($"Tool argument '{propertyName}' must be a string.");

    internal static string? GetOptionalString(JsonElement arguments, string propertyName)
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

    internal sealed class PreparedProcessExecution(
        ToolInvocation invocation,
        BuiltinProcessRunner.PinnedExecutable executable,
        PinnedPath workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? standardInput,
        string targetSummary) : IPreparedToolExecution
    {
        private int _disposed;

        public string TargetSummary { get; } = targetSummary;

        public async Task<ToolResult> ExecuteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var output = await BuiltinProcessRunner.RunAsync(
                executable.Path,
                arguments,
                workingDirectory.Path,
                environment,
                standardInput,
                ct).ConfigureAwait(false);
            var json = ToolJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("exitCode", output.ExitCode);
                writer.WriteString("stdout", output.StandardOutput);
                writer.WriteString("stderr", output.StandardError);
                writer.WriteBoolean("stdoutTruncated", output.StandardOutputTruncated);
                writer.WriteBoolean("stderrTruncated", output.StandardErrorTruncated);
                writer.WriteEndObject();
            });
            return new ToolResult(invocation.CallId, invocation.ToolId, true, json);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                workingDirectory.Dispose();
                executable.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
