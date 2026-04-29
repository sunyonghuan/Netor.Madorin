// =============================================================================
// 文件：ProxyModelNameMapper.cs
// 功能：Proxy 对外模型名映射工具
// 说明：提供模型名称的对外暴露与内部还原的双向转换功能，
//       统一添加 Cortana- 前缀以避免与官方/本地模型名称冲突。
// =============================================================================

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Proxy 对外模型名映射工具。
/// </summary>
/// <remarks>
/// <para>
/// 该类负责在对外暴露的模型名称和内部实际使用的模型名称之间进行双向转换。
/// </para>
/// <para>
/// <b>设计目的：</b>
/// <list type="bullet">
///   <item><description>统一添加 Cortana- 前缀，避免与官方 API 或本地模型名称冲突</description></item>
///   <item><description>对外提供一致的命名空间，便于用户识别代理模型</description></item>
///   <item><description>对内保持原始模型名，确保正确调用后端服务</description></item>
/// </list>
/// </para>
/// <para>
/// <b>使用示例：</b>
/// <code>
/// // 对外暴露：gpt-4 → Cortana-gpt-4
/// string exposed = ProxyModelNameMapper.ToExposedModelName("gpt-4");
///
/// // 内部还原：Cortana-gpt-4 → gpt-4
/// string internal = ProxyModelNameMapper.ToInternalModelName("Cortana-gpt-4");
/// </code>
/// </para>
/// </remarks>
internal static class ProxyModelNameMapper
{
    /// <summary>
    /// 对外暴露的模型名称前缀。
    /// </summary>
    /// <remarks>
    /// 所有通过 Proxy 暴露的模型名称都会添加此前缀，
    /// 用于区分代理模型和原始模型，避免命名冲突。
    /// </remarks>
    /// <example>Cortana-gpt-4, Cortana-claude-3-opus</example>
    public const string ExposedModelPrefix = "Cortana-";

    /// <summary>
    /// 将内部模型名称转换为对外暴露的模型名称。
    /// </summary>
    /// <param name="modelName">内部使用的模型名称（如 gpt-4、claude-3-opus）</param>
    /// <returns>
    /// 带有 Cortana- 前缀的对外模型名称。
    /// 如果输入为空或空白，返回 "Cortana-default"。
    /// 如果输入已带有前缀（不区分大小写），则原样返回。
    /// </returns>
    /// <remarks>
    /// <para>转换规则：</para>
    /// <list type="bullet">
    ///   <item><description>空/空白 → "Cortana-default"</description></item>
    ///   <item><description>已有前缀 → 保持不变</description></item>
    ///   <item><description>其他情况 → 添加前缀</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// ToExposedModelName("gpt-4")           → "Cortana-gpt-4"
    /// ToExposedModelName("Cortana-gpt-4")   → "Cortana-gpt-4" (已带前缀，不变)
    /// ToExposedModelName("")                → "Cortana-default"
    /// ToExposedModelName("   ")            → "Cortana-default"
    /// </code>
    /// </example>
    public static string ToExposedModelName(string modelName)
    {
        // 处理空值或空白字符串，返回默认模型名
        if (string.IsNullOrWhiteSpace(modelName)) return ExposedModelPrefix + "default";

        // 去除首尾空白字符
        var normalized = modelName.Trim();

        // 如果已经带有前缀（不区分大小写），直接返回
        return normalized.StartsWith(ExposedModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : ExposedModelPrefix + normalized;
    }

    /// <summary>
    /// 将对外暴露的模型名称还原为内部使用的模型名称。
    /// </summary>
    /// <param name="modelName">对外暴露的模型名称（如 Cortana-gpt-4）</param>
    /// <returns>
    /// 去除 Cortana- 前缀后的内部模型名称。
    /// 如果输入为空或空白，返回空字符串。
    /// 如果输入不带有前缀，则原样返回。
    /// </returns>
    /// <remarks>
    /// <para>转换规则：</para>
    /// <list type="bullet">
    ///   <item><description>空/空白 → 空字符串</description></item>
    ///   <item><description>带有前缀 → 移除前缀</description></item>
    ///   <item><description>不带头缀 → 保持不变</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// ToInternalModelName("Cortana-gpt-4")  → "gpt-4"
    /// ToInternalModelName("cortana-gpt-4")  → "gpt-4" (不区分大小写)
    /// ToInternalModelName("gpt-4")          → "gpt-4" (无前缀，不变)
    /// ToInternalModelName("")               → ""
    /// </code>
    /// </example>
    public static string ToInternalModelName(string modelName)
    {
        // 处理空值或空白字符串，返回空字符串
        if (string.IsNullOrWhiteSpace(modelName)) return string.Empty;

        // 去除首尾空白字符
        var normalized = modelName.Trim();

        // 如果带有前缀（不区分大小写），移除前缀；否则原样返回
        return normalized.StartsWith(ExposedModelPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[ExposedModelPrefix.Length..]  // 使用范围操作符截取前缀之后的部分
            : normalized;
    }
}