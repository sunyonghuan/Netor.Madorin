using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

internal sealed class BuiltinFsPathPolicy
{
    private static readonly char[] Separators = ['/', '\\'];

    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly string[] _readRoots;
    private readonly string[] _writeRoots;
    private readonly string _workspaceRoot;

    public BuiltinFsPathPolicy(ToolPermissionContext permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.WorkspaceRoot);
        if (permission.ExpiresAt is not null && permission.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new UnauthorizedAccessException("The tool permission grant has expired.");
        }

        RejectRawPath(permission.WorkspaceRoot);
        _workspaceRoot = Path.GetFullPath(permission.WorkspaceRoot);
        RejectReservedPath(_workspaceRoot);
        EnsureNoLinks(_workspaceRoot);
        _readRoots = NormalizeRoots(permission.AllowedReadRoots, nameof(permission.AllowedReadRoots));
        _writeRoots = NormalizeRoots(permission.AllowedWriteRoots, nameof(permission.AllowedWriteRoots));
    }

    public string ResolveReadPath(string path) => Resolve(path, _readRoots, "read");

    public string ResolveWritePath(string path) => Resolve(path, _writeRoots, "write");

    public PinnedPath PinReadFile(string path, bool allowDeleteSharing = false)
    {
        var resolvedPath = ResolveReadPath(path);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File '{path}' was not found.", path);
        }

        return new PinnedPath(
            resolvedPath,
            resolvedPath,
            BuiltinFsHandle.Open(
                resolvedPath,
                isDirectory: false,
                allowDeleteSharing));
    }

    public PinnedPath PinReadDirectory(string path)
    {
        var resolvedPath = ResolveReadPath(path);
        if (!Directory.Exists(resolvedPath))
        {
            throw new DirectoryNotFoundException($"Directory '{path}' was not found.");
        }

        return new PinnedPath(
            resolvedPath,
            resolvedPath,
            BuiltinFsHandle.Open(resolvedPath, isDirectory: true));
    }

    public PinnedPath PinWriteParent(string path)
    {
        var resolvedPath = ResolveWritePath(path);
        var parent = Path.GetDirectoryName(resolvedPath)
            ?? throw new InvalidDataException("The path has no parent directory.");
        EnsureNoLinks(parent);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                $"Parent directory for '{path}' was not found.");
        }

        return new PinnedPath(
            resolvedPath,
            parent,
            BuiltinFsHandle.Open(parent, isDirectory: true));
    }

    public string GetTargetSummary(string path)
    {
        var relative = Path.GetRelativePath(_workspaceRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        return string.Equals(relative, ".", StringComparison.Ordinal)
            ? "workspace:."
            : $"workspace:{relative}";
    }

    private string Resolve(string path, string[] allowedRoots, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectRawPath(path);

        var lexicalPath = Path.GetFullPath(path, _workspaceRoot);
        RejectReservedPath(lexicalPath);
        if (!IsWithin(_workspaceRoot, lexicalPath))
        {
            throw new UnauthorizedAccessException("The path is outside the workspace root.");
        }

        EnsureNoLinks(lexicalPath);

        if (!allowedRoots.Any(root => IsWithin(root, lexicalPath)))
        {
            throw new UnauthorizedAccessException(
                $"The path is outside the allowed {operation} roots.");
        }

        return lexicalPath;
    }

    private string[] NormalizeRoots(string[]? roots, string parameterName)
    {
        if (roots is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var normalized = new string[roots.Length];
        for (var index = 0; index < roots.Length; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(roots[index], parameterName);
            RejectRawPath(roots[index]);
            var root = Path.GetFullPath(roots[index], _workspaceRoot);
            RejectReservedPath(root);
            EnsureNoLinks(root);
            if (!IsWithin(_workspaceRoot, root))
            {
                throw new UnauthorizedAccessException(
                    $"Allowed root '{roots[index]}' is outside the workspace root.");
            }

            normalized[index] = root;
        }

        return normalized;
    }

    private static void RejectRawPath(string path)
    {
        var segments = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("Parent path segments are not allowed.");
        }

        if (segments.Any(segment => string.Equals(
                segment,
                ".madorin",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("The .madorin directory is reserved.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("//./", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("//?/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("/??/", StringComparison.OrdinalIgnoreCase)
            || IsUnixSpecialPath(normalized))
        {
            throw new UnauthorizedAccessException("Device and platform special paths are not allowed.");
        }

        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("UNC paths are not allowed.");
        }

        var firstColon = path.IndexOf(':', StringComparison.Ordinal);
        var isDriveColon = firstColon == 1
            && char.IsAsciiLetter(path[0])
            && Path.IsPathFullyQualified(path);
        if (firstColon >= 0
            && (!isDriveColon || path.IndexOf(':', firstColon + 1) >= 0))
        {
            throw new UnauthorizedAccessException("Alternate data streams are not allowed.");
        }
    }

    private static bool IsUnixSpecialPath(string path)
    {
        return IsPathOrChild(path, "/dev")
            || IsPathOrChild(path, "/proc")
            || IsPathOrChild(path, "/sys");
    }

    private static bool IsPathOrChild(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith($"{root}/", StringComparison.Ordinal);

    private static void RejectReservedPath(string path)
    {
        if (path.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(
                segment,
                ".madorin",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException("The .madorin directory is reserved.");
        }
    }

    private bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return string.Equals(relative, ".", _comparison)
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", _comparison)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", _comparison)
                && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", _comparison));
    }

    private static void EnsureNoLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The path does not have a root.", nameof(path));
        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var segment in relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info is null)
            {
                break;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.LinkTarget is not null)
            {
                throw new UnauthorizedAccessException(
                    $"Symbolic links and filesystem reparse points are not allowed: '{current}'.");
            }
        }
    }
}
