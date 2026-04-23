using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.Persistence;

/// <summary>
/// 消息内容的落库 POCO 表示（AOT 友好）。
/// 直接映射 Microsoft.Extensions.AI 中的 <c>TextContent</c> / <c>FunctionCallContent</c> / <c>FunctionResultContent</c> / <c>DataContent</c>。
/// 所有字段均为可选：按 <see cref="Kind"/> 分支读取所需字段。
/// </summary>
/// <remarks>
/// 为什么不直接序列化 AIContent？Microsoft.Extensions.AI 的多态 JSON 依赖运行时派生类型发现，
/// 在 Native AOT / 强 trimming 下可能被削掉。用自定义 POCO + 源生成 JsonSerializerContext 可以
/// 完全避免反射，保留工具调用与结果的关键字段（CallId、Name、Arguments、Result、Exception）。
/// </remarks>
public sealed class PersistedContent
{
    /// <summary>内容类型：text / functionCall / functionResult / data。</summary>
    public string Kind { get; set; } = "text";

    /// <summary>文本内容（Kind=text 或作为通用可读文本）。</summary>
    public string? Text { get; set; }

    /// <summary>工具调用 ID（Kind=functionCall / functionResult）。</summary>
    public string? CallId { get; set; }

    /// <summary>工具名（Kind=functionCall）。</summary>
    public string? Name { get; set; }

    /// <summary>
    /// 工具调用参数的 JSON（Kind=functionCall）。
    /// 存储已序列化的 JSON 字符串（而非原始字典），避免 AOT 反射。
    /// </summary>
    public string? ArgumentsJson { get; set; }

    /// <summary>
    /// 工具结果的 JSON（Kind=functionResult）。
    /// 若工具返回字符串，直接存储字符串值；否则存储 JSON 序列化结果。
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>工具执行异常消息（Kind=functionResult 且有异常时）。</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>数据内容的 MIME 类型（Kind=data）。</summary>
    public string? MediaType { get; set; }

    /// <summary>数据内容的 URI 或相对路径（Kind=data，仅保存引用，不内联字节）。</summary>
    public string? Uri { get; set; }
}

/// <summary>
/// AOT 安全的源生成 JSON 上下文，覆盖结构化消息内容持久化。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(System.Collections.Generic.List<PersistedContent>))]
[JsonSerializable(typeof(PersistedContent))]
internal sealed partial class PersistedContentJsonContext : JsonSerializerContext
{
}
