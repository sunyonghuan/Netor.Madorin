using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

internal static partial class WindowsNamedPipePeerProcess
{
    public static int GetServerProcessId(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw CreateIOException("Unable to resolve the named pipe server process.");
        }

        return checked((int)processId);
    }

    public static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw CreateIOException("Unable to resolve the named pipe client process.");
        }

        return checked((int)processId);
    }

    private static IOException CreateIOException(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
