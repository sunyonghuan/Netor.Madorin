namespace Netor.Cortana.AI.WorkMode.Prompts;

/// <summary>
/// 组合提示词提供者，按优先级链式查找。
/// 默认优先级：本地文件 → 嵌入资源。
/// </summary>
public sealed class CompositePromptProvider : IPromptProvider
{
    private readonly IReadOnlyList<IPromptProvider> _providers;

    /// <summary>
    /// 创建组合提示词提供者。
    /// </summary>
    /// <param name="providers">提示词提供者列表（按优先级从高到低排列）。</param>
    public CompositePromptProvider(IReadOnlyList<IPromptProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    /// <summary>
    /// 创建默认组合提供者（本地文件优先，嵌入资源兜底）。
    /// </summary>
    public static CompositePromptProvider CreateDefault(
        LocalFilePromptProvider fileProvider,
        EmbeddedResourcePromptProvider embeddedResourceProvider)
    {
        return new CompositePromptProvider(new IPromptProvider[] { fileProvider, embeddedResourceProvider });
    }

    public async Task<string?> GetPromptAsync(string name, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            var result = await provider.GetPromptAsync(name, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}
