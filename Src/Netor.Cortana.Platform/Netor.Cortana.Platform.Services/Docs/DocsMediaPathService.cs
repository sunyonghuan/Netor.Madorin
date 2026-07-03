using Microsoft.Extensions.Options;
using Netor.Cortana.Platform.Core.Options;

namespace Netor.Cortana.Platform.Services.Docs;

public sealed class DocsMediaPathService(IOptions<DocsMediaOptions> options)
{
    private readonly DocsMediaOptions _options = options.Value;

    public string RequestPath => NormalizeRequestPath(_options.RequestPath);

    public long MaxImageBytes => _options.MaxImageBytes > 0
        ? _options.MaxImageBytes
        : 5 * 1024 * 1024;

    public string EnsureRootPath(string contentRootPath)
    {
        var rootPath = GetRootPath(contentRootPath);
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    public string GetRootPath(string contentRootPath)
    {
        var configuredRoot = _options.RootPath;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(contentRootPath, configuredRoot));
        }

        var parent = Directory.GetParent(contentRootPath)?.FullName ?? contentRootPath;
        return Path.GetFullPath(Path.Combine(parent, "Shared", "docs-media"));
    }

    public string GetArticleDirectory(string contentRootPath, string categorySlug, string articleSlug)
    {
        var rootPath = GetRootPath(contentRootPath);
        var directory = Path.GetFullPath(Path.Combine(rootPath, categorySlug, articleSlug));
        if (!IsPathInsideDirectory(directory, rootPath))
        {
            throw new InvalidOperationException("Docs media directory is outside the configured root.");
        }

        return directory;
    }

    public string BuildArticleImageUrl(string categorySlug, string articleSlug, string fileName)
        => $"{RequestPath}/{categorySlug}/{articleSlug}/{Uri.EscapeDataString(fileName)}";

    public string BuildArticleImagePrefix(string categorySlug, string articleSlug)
        => $"{RequestPath}/{categorySlug}/{articleSlug}/";

    private static string NormalizeRequestPath(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return "/docs-media";
        }

        var normalized = requestPath.Trim().Replace('\\', '/').TrimEnd('/');
        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : $"/{normalized}";
    }

    private static bool IsPathInsideDirectory(string fullPath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, fullPath);
        return relativePath == "." ||
            (!relativePath.StartsWith("..", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relativePath));
    }
}
