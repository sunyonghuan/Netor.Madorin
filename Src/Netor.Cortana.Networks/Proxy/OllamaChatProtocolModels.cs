using System.Text.Json.Serialization;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 聊天消息模型。
/// </summary>
/// <remarks>
/// 表示对话中的一条消息，包含角色和内容。
/// 用于 Ollama Chat API 的请求和响应中。
/// </remarks>
/// <param name="Role">消息角色，如 "system"、"user"、"assistant"。</param>
/// <param name="Content">消息文本内容。</param>
public sealed record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Ollama Chat API 请求模型。
/// </summary>
/// <remarks>
/// 对应 Ollama 的 /api/chat 端点请求格式。
/// 支持多轮对话（messages 数组）和流式输出（stream）。
/// </remarks>
/// <param name="Model">模型名称，如 "llama3"、"qwen2" 等。</param>
/// <param name="Messages">对话消息列表，按时间顺序排列。</param>
/// <param name="Stream">是否启用流式输出。true 为流式，false 为一次性返回。</param>
/// <param name="Options">推理参数配置，如温度、最大生成长度等。</param>
public sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage>? Messages,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("options")] OllamaRequestOptions? Options = null);

/// <summary>
/// Ollama Generate API 请求模型。
/// </summary>
/// <remarks>
/// 对应 Ollama 的 /api/generate 端点请求格式。
/// 用于单轮文本生成任务，不支持多轮对话。
/// </remarks>
/// <param name="Model">模型名称。</param>
/// <param name="Prompt">用户输入的提示词文本。</param>
/// <param name="System">系统提示词，用于设定 AI 行为。</param>
/// <param name="Stream">是否启用流式输出。</param>
/// <param name="Options">推理参数配置。</param>
public sealed record OllamaGenerateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("options")] OllamaRequestOptions? Options = null);

/// <summary>
/// Ollama 推理参数配置。
/// </summary>
/// <remarks>
/// 包含模型推理时的可调参数，所有参数均为可选。
/// 未指定的参数将使用模型默认值。
/// </remarks>
/// <param name="Temperature">温度参数（0.0-2.0），控制输出随机性。值越高越随机，值越低越确定。</param>
/// <param name="NumPredict">最大生成 token 数量。-1 表示无限，-2 表示使用上下文长度。</param>
/// <param name="TopP">核采样参数（0.0-1.0），控制候选 token 的累积概率阈值。</param>
public sealed record OllamaRequestOptions(
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("num_predict")] int? NumPredict = null,
    [property: JsonPropertyName("top_p")] double? TopP = null);

/// <summary>
/// Ollama Chat API 响应模型。
/// </summary>
/// <remarks>
/// 对应 /api/chat 端点的响应格式。
/// 流式模式下会返回多个响应片段，每个片段的 done 字段标识是否为最后一条。
/// 非流式模式下只返回一次完整响应。
/// </remarks>
/// <param name="Model">使用的模型名称。</param>
/// <param name="CreatedAt">响应创建时间戳（ISO 8601 格式）。</param>
/// <param name="Message">AI 生成的消息内容。</param>
/// <param name="Done">是否完成生成。true 表示生成结束。</param>
/// <param name="DoneReason">完成原因，如 "stop"（正常结束）、"length"（达到长度限制）。</param>
/// <param name="TotalDuration">总处理时间（纳秒）。</param>
/// <param name="LoadDuration">模型加载时间（纳秒）。</param>
/// <param name="PromptEvalCount">提示词评估的 token 数量。</param>
/// <param name="PromptEvalDuration">提示词评估耗时（纳秒）。</param>
/// <param name="EvalCount">生成内容的 token 数量。</param>
/// <param name="EvalDuration">生成内容耗时（纳秒）。</param>
public sealed record OllamaChatResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("message")] OllamaMessage Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("done_reason")] string? DoneReason = null,
    [property: JsonPropertyName("total_duration")] long? TotalDuration = null,
    [property: JsonPropertyName("load_duration")] long? LoadDuration = null,
    [property: JsonPropertyName("prompt_eval_count")] long? PromptEvalCount = null,
    [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration = null,
    [property: JsonPropertyName("eval_count")] long? EvalCount = null,
    [property: JsonPropertyName("eval_duration")] long? EvalDuration = null);

/// <summary>
/// Ollama Generate API 响应模型。
/// </summary>
/// <remarks>
/// 对应 /api/generate 端点的响应格式。
/// 与 Chat 响应类似，但使用 response 字段直接返回文本而非 message 对象。
/// 适用于单轮文本生成场景。
/// </remarks>
/// <param name="Model">使用的模型名称。</param>
/// <param name="CreatedAt">响应创建时间戳（ISO 8601 格式）。</param>
/// <param name="Response">AI 生成的文本内容。</param>
/// <param name="Done">是否完成生成。</param>
/// <param name="DoneReason">完成原因。</param>
/// <param name="TotalDuration">总处理时间（纳秒）。</param>
/// <param name="LoadDuration">模型加载时间（纳秒）。</param>
/// <param name="PromptEvalCount">提示词评估的 token 数量。</param>
/// <param name="PromptEvalDuration">提示词评估耗时（纳秒）。</param>
/// <param name="EvalCount">生成内容的 token 数量。</param>
/// <param name="EvalDuration">生成内容耗时（纳秒）。</param>
public sealed record OllamaGenerateResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("response")] string Response,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("done_reason")] string? DoneReason = null,
    [property: JsonPropertyName("total_duration")] long? TotalDuration = null,
    [property: JsonPropertyName("load_duration")] long? LoadDuration = null,
    [property: JsonPropertyName("prompt_eval_count")] long? PromptEvalCount = null,
    [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration = null,
    [property: JsonPropertyName("eval_count")] long? EvalCount = null,
    [property: JsonPropertyName("eval_duration")] long? EvalDuration = null);
