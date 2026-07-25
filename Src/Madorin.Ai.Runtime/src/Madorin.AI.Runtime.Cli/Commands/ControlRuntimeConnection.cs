using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Cli.Commands;

internal sealed class ControlRuntimeConnection : IAsyncDisposable
{
    public const string PipePrefix = "madorin.ai.runtime";
    public const string SecretEnvironmentVariable = "MADORIN_AI_RUNTIME_SECRET";

    private ControlRuntimeConnection(RuntimeClient client, RuntimeStatusResult status)
    {
        Client = client;
        Status = status;
    }

    public RuntimeClient Client { get; }

    public RuntimeStatusResult Status { get; }

    public static async Task<ControlRuntimeConnection> OpenAsync(
        string? workspaceValue,
        string? dataDirectoryValue,
        string? expectedInstanceId,
        string secret,
        CancellationToken cancellationToken)
    {
        var workspace = ResolveWorkspace(workspaceValue);
        var dataDirectory = ResolveDataDirectory(workspace, dataDirectoryValue);
        var instance = await ReadRuntimeInstanceAsync(dataDirectory, cancellationToken)
            .ConfigureAwait(false);
        ValidateDiscoveredInstance(instance, expectedInstanceId);
        ValidateRuntimeProcess(instance.Pid);

        var client = new RuntimeClient(new RuntimeClientOptions(
            instance.PipeName,
            $"madorin-cli-{Guid.NewGuid():N}",
            ConnectTimeout: TimeSpan.FromSeconds(5),
            EventPipeName: instance.EventPipeName,
            ExpectedRuntimeInstanceId: instance.InstanceId,
            HandshakeSecret: secret.Trim(),
            ExpectedServerProcessId: instance.Pid,
            EnableBackgroundHeartbeat: false,
            WorkspaceDirectory: workspace,
            DataDirectory: dataDirectory,
            RuntimeInstanceId: instance.InstanceId,
            PipePrefix: PipePrefix,
            StartPolicy: RuntimeStartPolicy.AttachExisting));
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
            ValidateConnectedInstance(client, instance, workspace, status);
            return new ControlRuntimeConnection(client, status);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();

    public static bool IsInstanceIdentityMismatch(Exception exception)
    {
        for (var current = exception.InnerException;
             current is not null;
             current = current.InnerException)
        {
            if (current is PlatformNotSupportedException)
            {
                return true;
            }

            if (current is InvalidDataException
                && (current.Message.StartsWith(
                        "The named pipe server PID",
                        StringComparison.Ordinal)
                    || current.Message.StartsWith(
                        "The Runtime instance identifier",
                        StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveWorkspace(string? workspace) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(workspace)
            ? Environment.CurrentDirectory
            : workspace);

    private static string ResolveDataDirectory(string workspace, string? dataDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(workspace, ".madorin")
            : dataDirectory);

    private static async Task<RuntimeInstanceInfo> ReadRuntimeInstanceAsync(
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var pidPath = Path.Combine(dataDirectory, "runtime.pid");
        if (!File.Exists(pidPath))
        {
            throw new InvalidDataException(
                $"No running Runtime instance was discovered in '{dataDirectory}'.");
        }

        var json = await File.ReadAllBytesAsync(pidPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.RuntimeInstanceInfo)
            ?? throw new InvalidDataException("The Runtime instance file is empty.");
    }

    private static void ValidateDiscoveredInstance(
        RuntimeInstanceInfo instance,
        string? expectedInstanceId)
    {
        if (string.IsNullOrWhiteSpace(instance.InstanceId)
            || string.IsNullOrWhiteSpace(instance.PipeName)
            || string.IsNullOrWhiteSpace(instance.EventPipeName)
            || instance.Pid <= 0)
        {
            throw new InvalidDataException("The Runtime instance file is invalid.");
        }

        if (expectedInstanceId is not null
            && !string.Equals(instance.InstanceId, expectedInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The discovered Runtime instance did not match --instance.");
        }

        var expectedPipeName = BuildPipeName(instance.InstanceId);
        if (!string.Equals(instance.PipeName, expectedPipeName, StringComparison.Ordinal)
            || !string.Equals(
                instance.EventPipeName,
                $"{expectedPipeName}.events",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The discovered Runtime instance does not use the supported Pipe brand.");
        }
    }

    private static string BuildPipeName(string instanceId) =>
        $"{PipePrefix}.{instanceId[..Math.Min(8, instanceId.Length)]}";

    private static void ValidateRuntimeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                throw new InvalidDataException("The discovered Runtime process is no longer running.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or Win32Exception)
        {
            throw new InvalidDataException(
                "The discovered Runtime process is no longer running.",
                ex);
        }
    }

    private static void ValidateConnectedInstance(
        RuntimeClient client,
        RuntimeInstanceInfo instance,
        string workspace,
        RuntimeStatusResult status)
    {
        var binding = client.InstanceBinding;
        if (!string.Equals(binding.RuntimeInstanceId, instance.InstanceId, StringComparison.Ordinal)
            || binding.RuntimeProcessId != instance.Pid
            || !string.Equals(binding.ControlPipeName, instance.PipeName, StringComparison.Ordinal)
            || !string.Equals(binding.EventPipeName, instance.EventPipeName, StringComparison.Ordinal)
            || !string.Equals(status.RuntimeInstanceId, instance.InstanceId, StringComparison.Ordinal)
            || status.ProcessId != instance.Pid
            || status.StartedAt != instance.StartedAt
            || !string.Equals(
                Path.GetFullPath(status.Workspace),
                workspace,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The connected Runtime did not match the discovered instance metadata.");
        }
    }
}
