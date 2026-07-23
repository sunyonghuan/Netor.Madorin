using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

internal readonly record struct UnixPeerIdentity(int ProcessId, uint UserId, uint GroupId);

internal static partial class UnixPeerProcess
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerPid = 2;

    public static UnixPeerIdentity GetPeerIdentity(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxIdentity(socket.SafeHandle);
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetMacOsIdentity(socket.SafeHandle);
        }

        throw new PlatformNotSupportedException(
            "Unix peer credentials are supported only on Linux and macOS.");
    }

    public static uint GetCurrentUserId()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Unix peer credentials are supported only on Linux and macOS.");
        }

        return GetEffectiveUserId();
    }

    private static UnixPeerIdentity GetLinuxIdentity(SafeSocketHandle socket)
    {
        var length = (uint)Marshal.SizeOf<LinuxUCred>();
        if (GetLinuxSocketOption(socket, SolSocket, SoPeerCred, out var value, ref length) != 0)
        {
            throw CreateException("getsockopt(SO_PEERCRED)");
        }

        return new UnixPeerIdentity(value.ProcessId, value.UserId, value.GroupId);
    }

    private static UnixPeerIdentity GetMacOsIdentity(SafeSocketHandle socket)
    {
        if (GetPeerId(socket, out var userId, out var groupId) != 0)
        {
            throw CreateException("getpeereid");
        }

        var length = sizeof(int);
        if (GetMacSocketOption(socket, SolLocal, LocalPeerPid, out var processId, ref length) != 0)
        {
            throw CreateException("getsockopt(LOCAL_PEERPID)");
        }

        return new UnixPeerIdentity(processId, userId, groupId);
    }

    private static IOException CreateException(string operation) =>
        new($"{operation} failed.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxUCred
    {
        public int ProcessId;
        public uint UserId;
        public uint GroupId;
    }

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetLinuxSocketOption(
        SafeSocketHandle socket,
        int level,
        int option,
        out LinuxUCred value,
        ref uint length);

    [LibraryImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    private static partial int GetPeerId(
        SafeSocketHandle socket,
        out uint userId,
        out uint groupId);

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetMacSocketOption(
        SafeSocketHandle socket,
        int level,
        int option,
        out int value,
        ref int length);

    [LibraryImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static partial uint GetEffectiveUserId();
}
