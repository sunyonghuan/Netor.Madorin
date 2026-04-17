using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Office.Security;

/// <summary>
/// 文档路径安全守卫实现。
/// 根据配置的白名单目录限制可访问的文件路径，防止路径穿越攻击。
/// </summary>
public sealed class DocumentPathGuard : IDocumentPathGuard
{
    private readonly ILogger<DocumentPathGuard> _logger;

    /// <summary>规范化后的允许目录列表，均以目录分隔符结尾。</summary>
    private readonly string[] _allowedRoots;

    public DocumentPathGuard(OfficePluginOptions options, ILogger<DocumentPathGuard> logger)
    {
        _logger = logger;
        _allowedRoots = options.AllowedDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
    }

    /// <inheritdoc/>
    public string? ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "路径规范化失败: {Path}", path);
            return null;
        }

        // 白名单为空时不限制（开发模式）
        if (_allowedRoots.Length == 0)
            return fullPath;

        foreach (var root in _allowedRoots)
        {
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return fullPath;
        }

        _logger.LogWarning("路径 {Path} 不在允许目录内", fullPath);
        return null;
    }
}
