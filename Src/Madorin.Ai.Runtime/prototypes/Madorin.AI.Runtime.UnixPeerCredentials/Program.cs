using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.UnixPeerCredentials;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--child", var socketPath])
        {
            return await RunChildAsync(socketPath).ConfigureAwait(false);
        }

        if (args is ["--transport-server", var serverSocketPath])
        {
            return await RunTransportServerProbeAsync(serverSocketPath).ConfigureAwait(false);
        }

        if (args is ["--transport-client", var clientSocketPath])
        {
            return await RunTransportClientProbeAsync(clientSocketPath).ConfigureAwait(false);
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SKIP platform=windows reason=unix-peer-credentials-unavailable");
            return 0;
        }

        return await RunParentAsync().ConfigureAwait(false);
    }

    private static async Task<int> RunTransportServerProbeAsync(string socketPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var transport = await NamedPipeTransport.CreateServerAsync(
                socketPath,
                timeout.Token).ConfigureAwait(false);
            Console.Error.WriteLine("FAIL scenario=wrong-user-server-accepted");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.WriteLine(
                $"PASS platform={GetPlatformName()} scenario=wrong-user-server-rejected "
                + $"reason={exception.Message}");
            return 0;
        }
    }

    private static async Task<int> RunTransportClientProbeAsync(string socketPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var transport = await NamedPipeTransport.ConnectAsync(
                socketPath,
                timeout.Token).ConfigureAwait(false);
            Console.Error.WriteLine("FAIL scenario=wrong-user-client-accepted");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.WriteLine(
                $"PASS platform={GetPlatformName()} scenario=wrong-user-client-rejected "
                + $"reason={exception.Message}");
            return 0;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static async Task<int> RunParentAsync()
    {
        var socketPath = Path.Combine(
            Path.GetTempPath(),
            $"madorin-peer-credentials-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Process? child = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(socketPath, ownerOnly);
            if (File.GetUnixFileMode(socketPath) != ownerOnly)
            {
                throw new UnauthorizedAccessException("The Unix socket is not owner-only.");
            }

            listener.Listen(1);
            child = StartChild(socketPath);
            using var accepted = await listener.AcceptAsync().ConfigureAwait(false);
            var credentials = UnixPeerCredentials.Read(accepted);
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            var reportedPid = int.Parse(
                await reader.ReadLineAsync().ConfigureAwait(false)
                    ?? throw new EndOfStreamException("The child did not report its PID."));
            if (reportedPid != child.Id || credentials.ProcessId != child.Id)
            {
                throw new InvalidDataException(
                    $"Peer PID mismatch: credential={credentials.ProcessId}, child={child.Id}, reported={reportedPid}.");
            }

            var currentUserId = UnixPeerCredentials.GetCurrentUserId();
            if (credentials.UserId != currentUserId)
            {
                throw new InvalidDataException(
                    $"Peer UID mismatch: credential={credentials.UserId}, current={currentUserId}.");
            }

            await writer.WriteLineAsync("ok").ConfigureAwait(false);
            await child.WaitForExitAsync().ConfigureAwait(false);
            if (child.ExitCode != 0)
            {
                throw new InvalidOperationException($"The child exited with code {child.ExitCode}.");
            }

            Console.WriteLine(
                $"PASS platform={GetPlatformName()} peerPid={credentials.ProcessId} uid={credentials.UserId} "
                + $"gid={credentials.GroupId} socketMode=600");
            return 0;
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
            }

            child?.Dispose();
            TryDelete(socketPath);
        }
    }

    private static async Task<int> RunChildAsync(string socketPath)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);
        return string.Equals(
            await reader.ReadLineAsync().ConfigureAwait(false),
            "ok",
            StringComparison.Ordinal) ? 0 : 1;
    }

    private static Process StartChild(string socketPath)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process path is unavailable.");
        var assemblyPath = Path.Combine(
            AppContext.BaseDirectory,
            "Madorin.AI.Runtime.UnixPeerCredentials.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException("The prototype assembly path is unavailable.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--child");
        startInfo.ArgumentList.Add(socketPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The peer credential child process could not start.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static string GetPlatformName() =>
        OperatingSystem.IsLinux() ? "linux" :
        OperatingSystem.IsMacOS() ? "macos" :
        "unsupported";
}
