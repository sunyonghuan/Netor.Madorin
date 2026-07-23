using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Madorin.AI.Runtime.UnixPeerCredentials;

internal readonly record struct PeerCredentials(int ProcessId, uint UserId, uint GroupId);

internal static partial class UnixPeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerPid = 2;

    public static PeerCredentials Read(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (OperatingSystem.IsLinux())
        {
            return ReadLinux(socket.SafeHandle);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ReadMacOS(socket.SafeHandle);
        }

        throw new PlatformNotSupportedException("The peer credential prototype supports Linux and macOS only.");
    }

    public static uint GetCurrentUserId()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The peer credential prototype supports Linux and macOS only.");
        }

        return GetEffectiveUserId();
    }

    private static PeerCredentials ReadLinux(SafeSocketHandle socket)
    {
        var length = (uint)Marshal.SizeOf<LinuxUCred>();
        if (GetLinuxSocketOption(socket, SolSocket, SoPeerCred, out var value, ref length) != 0)
        {
            throw CreateSocketException("getsockopt(SO_PEERCRED)");
        }

        return new PeerCredentials(value.ProcessId, value.UserId, value.GroupId);
    }

    private static PeerCredentials ReadMacOS(SafeSocketHandle socket)
    {
        if (GetPeerId(socket, out var userId, out var groupId) != 0)
        {
            throw CreateSocketException("getpeereid");
        }

        var length = sizeof(int);
        if (GetMacSocketOption(socket, SolLocal, LocalPeerPid, out var processId, ref length) != 0)
        {
            throw CreateSocketException("getsockopt(LOCAL_PEERPID)");
        }

        return new PeerCredentials(processId, userId, groupId);
    }

    private static IOException CreateSocketException(string operation) =>
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
