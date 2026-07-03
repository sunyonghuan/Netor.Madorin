using Microsoft.Extensions.Options;
using Netor.Cortana.Platform.Core.Options;
using Netor.Cortana.Platform.Services.Packages;

namespace Netor.Cortana.Platform.Services.Files;

public sealed class LocalFileService(IOptions<FileStorageOptions> options, PackageStorageSettingsService settingsService)
{
    private readonly FileStorageOptions _options = options.Value;

    public string GetPackageRoot() => Path.Combine(GetStorageRootFullPath(), "packages");

    public string ResolvePackagePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Package path cannot be empty.", nameof(relativePath));
        }

        var storageRoot = GetStorageRootFullPath();
        var packageRoot = GetPackageRoot();
        var normalizedRelativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        string fullPath;
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            fullPath = Path.GetFullPath(normalizedRelativePath);
        }
        else if (TryGetPathFromPackagesSegment(normalizedRelativePath, out var packagesRelativePath))
        {
            fullPath = Path.GetFullPath(Path.Combine(storageRoot, packagesRelativePath));
        }
        else
        {
            fullPath = Path.GetFullPath(Path.Combine(packageRoot, normalizedRelativePath));
        }

        if (!IsPathInsideDirectory(fullPath, packageRoot))
        {
            throw new InvalidOperationException("Package path is outside the configured package root.");
        }

        return fullPath;
    }

    public FileStream OpenPackageRead(string relativePath)
    {
        var fullPath = ResolvePackagePath(relativePath);
        return File.OpenRead(fullPath);
    }

    private string GetRootPath()
    {
        var rootPath = settingsService.GetOptions().RootPath;
        return string.IsNullOrWhiteSpace(rootPath) ? _options.RootPath : rootPath;
    }

    private string GetStorageRootFullPath()
    {
        var rootPath = GetRootPath();
        return Path.IsPathRooted(rootPath)
            ? Path.GetFullPath(rootPath)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, rootPath));
    }

    private static bool TryGetPathFromPackagesSegment(string relativePath, out string packagesRelativePath)
    {
        var segments = relativePath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var packageSegmentIndex = Array.FindIndex(segments, x => string.Equals(x, "packages", StringComparison.OrdinalIgnoreCase));
        if (packageSegmentIndex < 0)
        {
            packagesRelativePath = string.Empty;
            return false;
        }

        packagesRelativePath = Path.Combine(segments[packageSegmentIndex..]);
        return true;
    }

    private static bool IsPathInsideDirectory(string fullPath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, fullPath);
        return relativePath == "." ||
            (!relativePath.StartsWith("..", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relativePath));
    }
}
