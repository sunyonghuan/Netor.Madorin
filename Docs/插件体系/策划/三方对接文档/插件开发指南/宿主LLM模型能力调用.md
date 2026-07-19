# 宿主 LLM 模型能力调用

插件通过 PluginBus 的 `model` topic 请求宿主代为调用模型。插件不读取宿主模型密钥，也不把模型供应商凭据写入自己的设置。

## 一、前置声明

`plugin.json.requiredHostCapabilities` 必须声明：

```json
{
  "requiredHostCapabilities": [
    {
      "id": "host.llm.invoke.v1",
      "purpose": "summary.generate",
      "required": true,
      "reason": "调用宿主授权的模型生成摘要。"
    }
  ]
}
```

宿主会根据插件 ID、purpose 和用户授权决定是否执行。清单声明不等于已经授权。

## 二、连接

请求发送到 `init.extensions.pluginBusEndpoint`，缺省时可使用 `ws://localhost:{wsPort}/internal`。连接建立后宿主可能先发送 `connected`、`ping` 等控制帧，客户端应继续读取，直到收到与本次 `requestId` 匹配的模型响应。

模型调用是请求/响应协议，不是 `type: "event"`，也不需要发送事件订阅帧。

## 三、请求

```json
{
  "type": "request",
  "topic": "model",
  "op": "model.capability.request",
  "requestId": "req-001",
  "pluginId": "summary_plugin",
  "purpose": "summary.generate",
  "instruction": "把输入总结成三条要点。",
  "input": "这里是原始内容。",
  "outputFormat": "json",
  "timeoutMs": 120000,
  "maxOutputTokens": 1024
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | string | 固定为 `request` |
| `topic` | string | 必须为 `model` |
| `op` | string | 必须为 `model.capability.request` |
| `requestId` | string | 插件生成的唯一请求 ID |
| `pluginId` | string | 必须与插件身份一致 |
| `purpose` | string? | 应与能力申请用途一致 |
| `instruction` | string | 模型任务指令 |
| `input` | string | 业务输入 |
| `outputFormat` | string? | 例如 `text` 或 `json` |
| `timeoutMs` | integer | 小于等于 0 时使用宿主配置 |
| `maxOutputTokens` | integer | 小于等于 0 时使用宿主配置 |

当前请求 DTO 不包含 `protocol` 和 `version` 字段；不要依赖这两个字段参与模型请求校验。

## 四、响应

成功：

```json
{
  "type": "response",
  "topic": "model",
  "op": "model.capability.response",
  "requestId": "req-001",
  "success": true,
  "content": "{\"summary\":[\"...\"]}",
  "errorCode": null,
  "errorMessage": null
}
```

失败：

```json
{
  "type": "response",
  "topic": "model",
  "op": "model.capability.response",
  "requestId": "req-001",
  "success": false,
  "content": null,
  "errorCode": "MODEL_NOT_CONFIGURED",
  "errorMessage": "当前未配置可用模型。"
}
```

客户端必须同时检查 `type`、`op` 和 `requestId`，再读取 `success`。当前宿主可能返回的错误码包括 `INVALID_REQUEST`、`INVALID_OPERATION`、`UNAUTHORIZED_CAPABILITY`、`TIMEOUT`、`MODEL_NOT_CONFIGURED` 和 `INTERNAL_ERROR`。

## 五、连接实现要求

一个可用于生产的模型请求客户端应实现：

1. 为每个请求生成唯一 `requestId`。
2. 持续读取 WebSocket，处理 `connected`、`ping` 和模型响应。
3. 收到 `ping` 时回复 `{"type":"pong"}`。
4. 按 `requestId` 关联并发请求。
5. 超时后移除等待项，但不把迟到响应交给其他请求。
6. 连接断开时结束全部等待请求并执行受控重连。
7. 不记录完整 instruction、input 或模型输出中的敏感内容。

`Netor.Cortana.Plugin.Process` 当前提供的 `PluginBusClient` 只发布和订阅 `type: "event"`，接收循环会忽略 `response` 帧，也没有 `InvokeModelAsync`。模型调用应使用独立的请求客户端，不能直接复用事件客户端等待响应。

## 六、C# 示例（使用官方元数据 SDK）

Process 入口使用：

```csharp
using Netor.Cortana.Plugin;

[RequiredHostCapability(
    Id = "host.llm.invoke.v1",
    Purpose = "summary.generate",
    Required = true,
    Reason = "调用宿主授权的模型生成摘要。")]
public static partial class Startup
{
}
```

Native 入口使用同名 Attribute，但命名空间为 `Netor.Cortana.Plugin.Native`。

请求客户端可使用以下 AOT 友好 DTO；传输层仍需按上一节实现独立 WebSocket 请求循环：

```csharp
internal sealed record ModelRequestFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = "request";
    [JsonPropertyName("topic")] public string Topic { get; init; } = "model";
    [JsonPropertyName("op")] public string Op { get; init; } = "model.capability.request";
    [JsonPropertyName("requestId")] public string RequestId { get; init; } = string.Empty;
    [JsonPropertyName("pluginId")] public string PluginId { get; init; } = string.Empty;
    [JsonPropertyName("purpose")] public string? Purpose { get; init; }
    [JsonPropertyName("instruction")] public string Instruction { get; init; } = string.Empty;
    [JsonPropertyName("input")] public string Input { get; init; } = string.Empty;
    [JsonPropertyName("outputFormat")] public string? OutputFormat { get; init; }
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; init; }
    [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; init; }
}

internal sealed record ModelResponseFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("topic")] public string Topic { get; init; } = string.Empty;
    [JsonPropertyName("op")] public string Op { get; init; } = string.Empty;
    [JsonPropertyName("requestId")] public string RequestId { get; init; } = string.Empty;
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}

[JsonSerializable(typeof(ModelRequestFrame))]
[JsonSerializable(typeof(ModelResponseFrame))]
internal partial class ModelProtocolJsonContext : JsonSerializerContext;
```
