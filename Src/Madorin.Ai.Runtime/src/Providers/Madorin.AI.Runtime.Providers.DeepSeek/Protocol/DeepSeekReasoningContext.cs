namespace Madorin.AI.Runtime.Providers.DeepSeek.Protocol;

/// <summary>
/// 线程/任务本地存储：保存当前请求的 DeepSeek reasoning 内容，
/// 供 <see cref="DeepSeekOverrideHandler"/> 在 HTTP 层补写 reasoning_content 字段。
/// </summary>
internal static class DeepSeekReasoningContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>当前异步上下文的 reasoning 内容（可为 null）。</summary>
    public static string? CurrentReasoning => _current.Value;

    /// <summary>设置当前上下文的 reasoning 内容。</summary>
    public static void SetReasoning(string? reasoning)
        => _current.Value = reasoning;

    /// <summary>清除当前上下文的 reasoning 内容。</summary>
    public static void Clear()
        => _current.Value = null;
}
