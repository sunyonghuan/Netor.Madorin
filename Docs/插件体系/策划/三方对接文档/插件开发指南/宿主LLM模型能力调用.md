# 宿主LLM模型能力调用

这篇文档专门说明：插件申请了宿主的大模型能力之后，应该怎么真正调用。

## 一、核心结论

插件不要自己拿模型密钥，也不要自己直连模型供应商。正确路径是：

1. 在入口类上声明 `[RequiredHostCapability(Id = "host.llm.invoke.v1", ...)]`
2. 在运行时通过宿主注入的 PluginBus 连接发送 `model.capability.request`
3. 等待宿主返回 `model.capability.response`
4. 根据 `success`、`content`、`errorCode`、`errorMessage` 做降级或重试

## 二、请求结构

模型能力请求属于 PluginBus 的 `model` topic：

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.3.0",
  "topic": "model",
  "op": "model.capability.request",
  "requestId": "req-001",
  "pluginId": "memory_engine",
  "purpose": "summary.generate",
  "instruction": "把输入总结成三条要点。",
  "input": "这里是原始内容。",
  "outputFormat": "json",
  "timeoutMs": 120000,
  "maxOutputTokens": 1024
}
```

## 三、响应结构

```json
{
  "type": "response",
  "topic": "model",
  "op": "model.capability.response",
  "requestId": "req-001",
  "success": true,
  "content": "{\"summary\":[\"...\",\"...\",\"...\"]}"
}
```

失败时：

```json
{
  "type": "response",
  "topic": "model",
  "op": "model.capability.response",
  "requestId": "req-001",
  "success": false,
  "errorCode": "MODEL_NOT_CONFIGURED",
  "errorMessage": "当前未配置可用模型。"
}
```

## 四、C# 调用示例

下面示例参考仓库里的 Memory 插件实现，表达的是当前正确方向：

```csharp
public sealed class SummaryTools(
    PluginSettings settings,
    MemoryPluginBusConnection pluginBus,
    ILogger<SummaryTools> logger)
{
    [Tool(Name = "generate_summary", Description = "调用宿主大模型生成摘要。")]
    public async Task<string> GenerateSummary(
        [Parameter(Name = "text", Description = "要总结的文本。")] string text,
        CancellationToken cancellationToken)
    {
        var request = new HostModelCapabilityRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            PluginId = settings.Extensions.TryGetValue("pluginId", out var id) ? id : "summary_plugin",
            Purpose = "summary.generate",
            Instruction = "把输入总结成三条要点。",
            Input = text,
            OutputFormat = "json",
            MaxOutputTokens = 1024
        };

        var response = await pluginBus.InvokeModelAsync(request, cancellationToken);
        if (response is null || !response.Success)
        {
            logger.LogWarning("模型调用失败：{Code} {Message}", response?.ErrorCode, response?.ErrorMessage);
            return "{\"ok\":false}";
        }

        return response.Content ?? "{\"ok\":false}";
    }
}
```

## 五、宿主能力申请示例

```csharp
[RequiredHostCapability(
    Id = "host.llm.invoke.v1",
    Purpose = "summary.generate",
    Required = true,
    Reason = "需要调用宿主授权的大模型生成摘要。")]
public static partial class Startup
{
}
```

## 六、写代码时的注意点

- 先判断宿主能力是否授予，再调用模型。
- 不要把 `Purpose` 写成内部实现细节。
- 不要记录 prompt 中的敏感信息。
- 失败时要允许降级，不要让整个插件直接崩掉。
