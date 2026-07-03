using System.Reflection;

namespace Netor.Cortana.AI.WorkMode.Prompts;

/// <summary>
/// 从程序集嵌入资源加载默认提示词。
/// </summary>
public sealed class EmbeddedResourcePromptProvider : IPromptProvider
{
    private const string DefaultResourcePrefix = "Netor.Cortana.AI.prompts";

    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;

    public EmbeddedResourcePromptProvider(Assembly assembly, string resourcePrefix = DefaultResourcePrefix)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _resourcePrefix = string.IsNullOrWhiteSpace(resourcePrefix)
            ? DefaultResourcePrefix
            : resourcePrefix.TrimEnd('.');
    }

    public async Task<string?> GetPromptAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var resourceName = $"{_resourcePrefix}.{name.Trim()}.md";
        await using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return GetBuiltInFallback(name);
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string? GetBuiltInFallback(string name)
    {
        return string.Equals(name, "work_mode.general_manager", StringComparison.Ordinal)
            ? "你是工作模式的总经理智能体。请先与用户确认需求，再制定计划并执行。"
            : null;
    }
}
