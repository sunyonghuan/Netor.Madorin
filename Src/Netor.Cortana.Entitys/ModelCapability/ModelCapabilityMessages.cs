using System.Text.Json.Serialization;

namespace Netor.Cortana.Entitys.ModelCapability;

/// <summary>
/// 描述模型能力调用中的提示词输入结构。
/// </summary>
public sealed record ModelCapabilityPrompt
{
    /// <summary>
    /// 系统级提示词。
    /// </summary>
    [JsonPropertyName("system")]
    public string? System { get; init; }

    /// <summary>
    /// 调用任务指令。
    /// </summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    /// <summary>
    /// 输入内容的格式说明。
    /// </summary>
    [JsonPropertyName("inputFormat")]
    public string? InputFormat { get; init; }

    /// <summary>
    /// 传递给模型的业务输入内容。
    /// </summary>
    [JsonPropertyName("input")]
    public string? Input { get; init; }
}

/// <summary>
/// 描述模型能力调用期望的输出约束。
/// </summary>
public sealed record ModelCapabilityOutput
{
    /// <summary>
    /// 期望输出格式，例如 text 或 json。
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>
    /// 期望输出遵循的结构化 Schema 标识。
    /// </summary>
    [JsonPropertyName("schemaId")]
    public string? SchemaId { get; init; }
}

/// <summary>
/// 描述模型能力调用的输入与输出 Token 预算。
/// </summary>
public sealed record ModelCapabilityBudget
{
    /// <summary>
    /// 最大输入 Token 数。
    /// </summary>
    [JsonPropertyName("maxInputTokens")]
    public int MaxInputTokens { get; init; }

    /// <summary>
    /// 最大输出 Token 数。
    /// </summary>
    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; init; }
}

/// <summary>
/// 描述模型能力调用的采样参数。
/// </summary>
public sealed record ModelCapabilityOptions
{
    /// <summary>
    /// 温度参数，用于控制生成随机性。
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>
    /// Top-P 参数，用于控制 nucleus sampling 范围。
    /// </summary>
    [JsonPropertyName("topP")]
    public double? TopP { get; init; }
}

/// <summary>
/// 插件向宿主请求调用大模型能力的消息。
/// </summary>
public sealed record ModelCapabilityRequest
{
    /// <summary>
    /// 消息类型，调用请求固定为 request。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "request";

    /// <summary>
    /// 插件总线主题。
    /// </summary>
    [JsonPropertyName("topic")]
    public string Topic { get; init; } = CortanaWsEndpoints.ModelTopic;

    /// <summary>
    /// 模型能力操作名。
    /// </summary>
    [JsonPropertyName("op")]
    public string Op { get; init; } = ModelCapabilityProtocol.LlmInvokeOperation;

    /// <summary>
    /// 本次模型能力调用的请求标识。
    /// </summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// 发起调用的插件标识。
    /// </summary>
    [JsonPropertyName("pluginId")]
    public string PluginId { get; init; } = string.Empty;

    /// <summary>
    /// 调用目的，用于授权、审计和日志记录。
    /// </summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    /// <summary>
    /// 发送给模型的任务指令。
    /// </summary>
    [JsonPropertyName("instruction")]
    public string Instruction { get; init; } = string.Empty;

    /// <summary>
    /// 发送给模型的业务输入内容。
    /// </summary>
    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;

    /// <summary>
    /// 期望输出格式，例如 text 或 json。
    /// </summary>
    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; init; }

    /// <summary>
    /// 本次调用的超时时间，单位为毫秒；小于等于 0 时使用宿主配置。
    /// </summary>
    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; init; }

    /// <summary>
    /// 本次调用请求的最大输出 Token 数；小于等于 0 时使用宿主配置。
    /// </summary>
    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; init; }
}

/// <summary>
/// 宿主返回给插件的模型能力调用响应。
/// </summary>
public sealed record ModelCapabilityResponse
{
    /// <summary>
    /// 消息类型，响应消息固定为 response。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "response";

    /// <summary>
    /// 插件总线主题。
    /// </summary>
    [JsonPropertyName("topic")]
    public string Topic { get; init; } = CortanaWsEndpoints.ModelTopic;

    /// <summary>
    /// 模型能力响应操作名。
    /// </summary>
    [JsonPropertyName("op")]
    public string Op { get; init; } = CortanaWsEndpoints.ModelCapabilityResponseOperation;

    /// <summary>
    /// 对应请求的标识。
    /// </summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    /// <summary>
    /// 指示调用是否成功。
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    /// <summary>
    /// 调用成功时返回的模型输出内容。
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// 调用失败时返回的错误码。
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// 调用失败时返回的错误说明。
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 模型能力协议中的结构化错误消息。
/// </summary>
public sealed record ModelCapabilityError
{
    /// <summary>
    /// 消息类型，错误消息固定为 error。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "error";

    /// <summary>
    /// 模型能力协议名称。
    /// </summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = ModelCapabilityProtocol.Protocol;

    /// <summary>
    /// 模型能力协议版本。
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = ModelCapabilityProtocol.Version;

    /// <summary>
    /// 发生错误时正在执行的操作名称。
    /// </summary>
    [JsonPropertyName("op")]
    public string? Op { get; init; }

    /// <summary>
    /// 关联请求标识。
    /// </summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    /// <summary>
    /// 关联追踪标识。
    /// </summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    /// <summary>
    /// 关联插件标识。
    /// </summary>
    [JsonPropertyName("pluginId")]
    public string? PluginId { get; init; }

    /// <summary>
    /// 关联调用目的。
    /// </summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    /// <summary>
    /// 错误码。
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// 错误说明。
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 指示该错误是否允许客户端重试。
    /// </summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}

/// <summary>
/// 模型能力 WebSocket 连接建立后由宿主发送的欢迎消息。
/// </summary>
public sealed record ModelCapabilityConnectedMessage
{
    /// <summary>
    /// 消息类型，连接消息固定为 connected。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "connected";

    /// <summary>
    /// 模型能力协议名称。
    /// </summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = ModelCapabilityProtocol.Protocol;

    /// <summary>
    /// 模型能力协议版本。
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = ModelCapabilityProtocol.Version;

    /// <summary>
    /// 当前 WebSocket 连接的客户端标识。
    /// </summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// 宿主当前暴露给插件的能力列表。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string Capabilities { get; init; } = "llm.invoke";
}
