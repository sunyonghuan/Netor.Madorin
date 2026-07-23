using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Opens no-follow filesystem handles and validates the final operating-system target.</summary>
internal static partial class BuiltinFsHandle
{
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileNameNormalized = 0x0;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;

    public static SafeFileHandle Open(
        string path,
        bool isDirectory,
        bool allowDeleteSharing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return OperatingSystem.IsWindows()
            ? OpenWindows(path, isDirectory, allowDeleteSharing)
            : OpenUnix(path, isDirectory, allowDeleteSharing);
    }

    private static SafeFileHandle OpenWindows(
        string path,
        bool isDirectory,
        bool allowDeleteSharing)
    {
        var handle = CreateFile(
            path,
            isDirectory ? 0 : GenericRead,
            (isDirectory ? FileShareRead | FileShareWrite : FileShareRead)
                | (allowDeleteSharing ? FileShareDelete : 0),
            nint.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint
                | (isDirectory ? FileFlagBackupSemantics : FileFlagOverlapped),
            nint.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"The filesystem target could not be opened: {new Win32Exception(error).Message}",
                new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileInfoByHandleClass.FileAttributeTagInfo,
                    out var tagInfo,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw CreateWindowsIOException("The filesystem target metadata could not be read.");
            }

            if ((tagInfo.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Symbolic links and filesystem reparse points are not allowed.");
            }

            var actualIsDirectory = (tagInfo.FileAttributes & FileAttributeDirectory) != 0;
            if (actualIsDirectory != isDirectory)
            {
                throw new InvalidDataException(
                    isDirectory
                        ? "The filesystem target is not a directory."
                        : "The filesystem target is not a regular file.");
            }

            var finalPath = GetFinalPath(handle);
            if (!PathsEqual(path, finalPath))
            {
                throw new UnauthorizedAccessException(
                    "The filesystem target changed while it was being prepared.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenUnix(
        string path,
        bool isDirectory,
        bool allowDeleteSharing)
    {
        var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            allowDeleteSharing ? FileShare.Read | FileShare.Delete : FileShare.Read,
            isDirectory ? FileOptions.None : FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Symbolic links are not allowed.");
            }

            var actualIsDirectory = (attributes & FileAttributes.Directory) != 0;
            if (actualIsDirectory != isDirectory)
            {
                throw new InvalidDataException(
                    isDirectory
                        ? "The filesystem target is not a directory."
                        : "The filesystem target is not a regular file.");
            }

            if (OperatingSystem.IsLinux())
            {
                var descriptor = handle.DangerousGetHandle().ToInt64();
                var link = new FileInfo($"/proc/self/fd/{descriptor}")
                    .ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new UnauthorizedAccessException(
                        "The opened filesystem target could not be verified.");
                if (!PathsEqual(path, link.FullName))
                {
                    throw new UnauthorizedAccessException(
                        "The filesystem target changed while it was being prepared.");
                }
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* bufferPointer = buffer)
        {
            length = GetFinalPathNameByHandle(
                handle,
                bufferPointer,
                (uint)buffer.Length,
                FileNameNormalized);
        }

        if (length == 0)
        {
            throw CreateWindowsIOException("The final filesystem path could not be read.");
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            fixed (char* bufferPointer = buffer)
            {
                length = GetFinalPathNameByHandle(
                    handle,
                    bufferPointer,
                    (uint)buffer.Length,
                    FileNameNormalized);
            }

            if (length == 0 || length >= buffer.Length)
            {
                throw CreateWindowsIOException("The final filesystem path could not be read.");
            }
        }

        var path = new string(buffer, 0, checked((int)length));
        const string extendedPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $@"\\{path[uncPrefix.Length..]}";
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static IOException CreateWindowsIOException(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(message, new Win32Exception(error));
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            comparison);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char* filePath,
        uint filePathLength,
        uint flags);

    private enum FileInfoByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}
