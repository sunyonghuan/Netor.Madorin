using System.Text.Json;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Runs an approved PowerShell script exclusively through standard input.</summary>
public sealed class BuiltinPowerShellToolExecutor : IToolExecutor
{
    private static readonly string[] PowerShellExecutables = OperatingSystem.IsWindows()
        ? ["pwsh", "powershell"]
        : ["pwsh"];

    public bool CanExecute(string toolId) => string.Equals(
        toolId,
        BuiltinToolRegistry.PowerShellRunToolId,
        StringComparison.Ordinal);

    public ValueTask<IPreparedToolExecution> PrepareAsync(
        ToolInvocation invocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        if (!CanExecute(invocation.ToolId))
        {
            throw new InvalidOperationException("The PowerShell executor cannot execute this tool.");
        }

        var permission = invocation.PermissionContext
            ?? throw new UnauthorizedAccessException("A tool permission grant is required.");
        if (!permission.AllowPowerShell)
        {
            throw new UnauthorizedAccessException("PowerShell is disabled by the effective Grant.");
        }

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

        var script = BuiltinProcessToolExecutor.GetRequiredString(arguments, "script");
        BuiltinProcessRunner.ValidateStandardInput(script);
        var executable = BuiltinProcessRunner.ResolveAndPinFirstAvailable(PowerShellExecutables);
        try
        {
            var policy = new BuiltinFsPathPolicy(permission);
            var workingDirectory = policy.PinReadDirectory(
                BuiltinProcessToolExecutor.GetOptionalString(arguments, "workingDirectory")
                    ?? permission.WorkspaceRoot);
            var environment = BuiltinProcessRunner.BuildEnvironment(
                arguments,
                permission.AllowedEnvironmentVariables);
            if (OperatingSystem.IsWindows()
                && string.Equals(
                    Path.GetFileName(executable.Path),
                    "powershell.exe",
                    StringComparison.OrdinalIgnoreCase)
                && !environment.ContainsKey("SystemRoot"))
            {
                var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrWhiteSpace(systemRoot))
                {
                    throw new IOException("The Windows system directory could not be resolved.");
                }

                environment = new Dictionary<string, string>(
                    environment,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["SystemRoot"] = systemRoot
                };
            }

            return ValueTask.FromResult<IPreparedToolExecution>(
                new BuiltinProcessToolExecutor.PreparedProcessExecution(
                    invocation,
                    executable,
                    workingDirectory,
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-"],
                    environment,
                    script,
                    "process:powershell"));
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
}
