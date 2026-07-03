using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.WorkMode.Prompts;

/// <summary>
/// 从本地文件系统加载提示词（.md 文件）。
/// 文件命名规则：提示词名称中的 "." 替换为 "/"，如 "work_mode.general_manager" → "work_mode/general_manager.md"。
/// </summary>
public sealed class LocalFilePromptProvider : IPromptProvider
{
    private readonly IReadOnlyList<string> _promptDirectories;

    public LocalFilePromptProvider(IAppPaths appPaths, string? applicationBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        var appPromptsDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(applicationBaseDirectory)
                ? AppContext.BaseDirectory
                : applicationBaseDirectory,
            "prompts");

        _promptDirectories = new[] { appPromptsDirectory, appPaths.PromptsDirectory }
            .Select(static path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string?> GetPromptAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // 将 "work_mode.general_manager" 转换为 "work_mode/general_manager.md"
        var relativePath = name.Replace('.', Path.DirectorySeparatorChar) + ".md";
        foreach (var directory in _promptDirectories)
        {
            var fullPath = Path.Combine(directory, relativePath);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                return await File.ReadAllTextAsync(fullPath, cancellationToken);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }
}
