# Ollama 本地协议代理功能落地方案

## 1. 背景与目标

当前主项目为：

```text
Src\Netor.Cortana.UI
```

目标是在不破坏现有架构、兼容 Native AOT 的前提下，将 Madorin 内部 AI 代理能力以 Ollama 本地协议形式暴露给其他软件使用。

默认监听地址：

```text
http://localhost:11434
```

功能目标：

- 完全仿照 Ollama 常用 HTTP 协议与默认端口。
- 支持第三方软件通过 Ollama API 调用 Madorin AI Agent。
- 提供 UI 设置页，用于启停代理、设置端口、选择厂商/模型/Agent。
- 显示代理当前状态、上下文长度、Token 使用量、请求统计。
- 保持 Native AOT 兼容，不引入 ASP.NET Core Web SDK 或大型 Web 框架。

---

## 2. 总体结论

推荐方案：

```text
HttpListener + System.Text.Json Source Generator + 现有 AI 服务抽象
```

不建议引入：

```text
Microsoft.AspNetCore.*
Kestrel
Minimal API
Newtonsoft.Json
反射式 JSON 序列化
```

原因：

- 当前桌面主程序已开启 Native AOT 发布。
- ASP.NET Core 依赖较重，会显著增加体积和裁剪复杂度。
- 项目中 `Netor.Cortana.Networks` 已经使用 `HttpListener` 实现 WebSocket 服务，继续扩展该项目最自然。
- `HttpListener` + Source Generator JSON 对 AOT 更友好。

---

## 3. 当前项目结构观察

### 3.1 主项目

```text
Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj
```

主项目特点：

- `TargetFramework`: `net10.0`
- `OutputType`: `WinExe`
- Release 已启用：

```xml
<PublishAot>true</PublishAot>
<SelfContained>true</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

主项目已引用：

```text
Netor.Cortana.Entitys
Netor.Cortana.AI
Netor.Cortana.Voice
Netor.Cortana.Plugin
Netor.Cortana.Networks
```

### 3.2 网络项目

```text
Src\Netor.Cortana.Networks
```

现有能力：

- 已依赖 `Microsoft.Extensions.Hosting.Abstractions`
- 已依赖 `Microsoft.Extensions.Logging.Abstractions`
- 已引用 `Netor.Cortana.Entitys`
- 已存在 `WebSocketServerService`
- `WebSocketServerService` 已经基于 `HttpListener` 实现服务监听

因此，Ollama 协议 HTTP 服务放在 `Netor.Cortana.Networks` 中最合适。

### 3.3 AI 项目

```text
Src\Netor.Cortana.AI
```

现有能力：

- `AIAgentFactory`
- `AiChatHostedService`
- `IAiChatEngine`
- `TokenTrackingChatClient`
- 多厂商驱动：OpenAI、AzureOpenAI、Ollama、Anthropic、DeepSeek、Gemini、GLM、Custom

`AIAgentFactory` 已提供 Token 统计：

```csharp
public long LastInputTokens { get; }
public long MaxContextTokens { get; }
public double ContextUsageRatio { get; }
public event Action? TokenUsageChanged;
```

这些可以直接用于代理设置页的上下文使用量监控。

---

## 4. 推荐架构

### 4.1 职责划分

```text
Netor.Cortana.Entitys
  └─ 定义代理抽象接口和通用 DTO

Netor.Cortana.AI
  └─ 实现代理后端，将 Ollama 请求转换为 Madorin AI/Agent 调用

Netor.Cortana.Networks
  └─ HttpListener 服务、Ollama 协议路由、JSON 请求响应、NDJSON 流式输出

Netor.Cortana.UI
  └─ ProxyWindow 设置界面、状态显示、端口和厂商配置
```

### 4.2 依赖方向

推荐依赖方向：

```text
UI -> AI
UI -> Networks
UI -> Entitys
AI         -> Entitys
Networks   -> Entitys
```

不推荐直接变成：

```text
Networks -> AI
```

原因：

- `Networks` 应保持网络基础设施层职责。
- AI 调用细节不应侵入网络层。
- 用接口隔离后，后续可替换代理后端实现。

### 4.3 关键抽象

建议在 `Netor.Cortana.Entitys` 中新增：

```text
Src\Netor.Cortana.Entitys\Proxy\
  IAiProxyAgentBackend.cs
  AiProxyModels.cs
```

示例接口：

```csharp
namespace Netor.Cortana.Entitys.Proxy;

public interface IAiProxyAgentBackend
{
    IReadOnlyList<AiProxyModelDescriptor> ListModels();

    IAsyncEnumerable<AiProxyChatDelta> ChatAsync(
        AiProxyAgentRequest request,
        CancellationToken cancellationToken);
}
```

通用模型：

```csharp
public sealed record AiProxyModelDescriptor(
    string Name,
    string ProviderId,
    string ModelId,
    string DisplayName,
    int ContextLength);

public sealed record AiProxyAgentRequest(
    string Model,
    IReadOnlyList<AiProxyMessage> Messages,
    bool Stream,
    string? ProviderId = null,
    string? ModelId = null,
    string? AgentId = null);

public sealed record AiProxyMessage(
    string Role,
    string Content);

public sealed record AiProxyChatDelta(
    string Content,
    bool Done,
    long? InputTokens = null,
    long? OutputTokens = null);
```

---

## 5. 文件落点规划

### 5.1 Entitys 项目

```text
Src\Netor.Cortana.Entitys\Proxy\IAiProxyAgentBackend.cs
Src\Netor.Cortana.Entitys\Proxy\AiProxyModels.cs
```

职责：

- 定义 Networks 和 AI 之间的协议抽象。
- 避免 Networks 直接依赖 AI。

---

### 5.2 AI 项目

```text
Src\Netor.Cortana.AI\Proxys\CortanaOllamaProxyAgentBackend.cs
```

职责：

- 实现 `IAiProxyAgentBackend`。
- 从 `AiProviderService`、`AiModelService`、`AgentService` 中读取可用厂商、模型和 Agent。
- 根据代理配置选择目标 Provider/Model/Agent。
- 调用现有 AI Agent 或 Chat Engine。
- 将内部流式响应转换为 `AiProxyChatDelta`。

注意：

当前已存在：

```text
Src\Netor.Cortana.AI\Proxys\Models.cs
Src\Netor.Cortana.AI\Proxys\OllamaProxyService.cs
```

其中 `OllamaProxyService.cs` 当前 namespace 为：

```csharp
namespace Netor.Cortana.UI.Proxys;
```

这个命名空间不合适，建议后续删除或重构迁移。

---

### 5.3 Networks 项目

```text
Src\Netor.Cortana.Networks\Proxy\OllamaProxyServerService.cs
Src\Netor.Cortana.Networks\Proxy\OllamaProxyOptions.cs
Src\Netor.Cortana.Networks\Proxy\OllamaProxyState.cs
Src\Netor.Cortana.Networks\Proxy\OllamaProtocolModels.cs
Src\Netor.Cortana.Networks\Proxy\OllamaProxyJsonContext.cs
Src\Netor.Cortana.Networks\Proxy\OllamaHttpResponseWriter.cs
```

职责：

- `OllamaProxyServerService`：负责服务生命周期、监听、路由、请求分发。
- `OllamaProxyOptions`：代理设置项。
- `OllamaProxyState`：代理运行状态。
- `OllamaProtocolModels`：Ollama 兼容请求/响应 DTO。
- `OllamaProxyJsonContext`：System.Text.Json Source Generator 上下文。
- `OllamaHttpResponseWriter`：统一写 JSON、错误、NDJSON 流式响应。

---

### 5.4 UI 项目

项目里现有 UI 文件使用 `.axaml`，不是 `.xaml`。

建议新增：

```text
Src\Netor.Cortana.UI\Views\Proxy\ProxyWindow.axaml
Src\Netor.Cortana.UI\Views\Proxy\ProxyWindow.axaml.cs
Src\Netor.Cortana.UI\Views\Proxy\ProxyViewModel.cs
```

职责：

- 显示代理当前状态。
- 提供启动/停止开关。
- 设置端口号。
- 选择 Proxy 专用厂商、模型、Agent。
- 显示上下文长度、Token 使用量。
- 显示请求量、错误量、最近错误。

---

## 6. Ollama 协议支持范围

### 6.1 第一阶段必须实现

```http
GET  /api/version
GET  /api/tags
POST /api/chat
POST /api/generate
```

这四个接口可兼容大多数使用 Ollama 的客户端。

---

### 6.2 第二阶段建议实现

```http
GET  /
HEAD /
POST /api/show
POST /api/ps
```

用于提升兼容性。

---

### 6.3 暂不实现或返回兼容错误

```http
POST   /api/pull
POST   /api/push
POST   /api/create
POST   /api/copy
DELETE /api/delete
POST   /api/embed
```

原因：

- Madorin 并不是本地模型仓库。
- 这些接口属于 Ollama 模型管理或嵌入能力。
- 第一版不应扩大范围。

---

## 7. Ollama API 映射方案

### 7.1 GET /api/version

返回示例：

```json
{
  "version": "0.5.7"
}
```

说明：

- 可以写死 Ollama 兼容版本。
- 也可以返回 Madorin 自身版本，但部分客户端可能依赖 Ollama 版本格式。

---

### 7.2 GET /api/tags

返回当前 Madorin 可用模型。

模型来源：

- `AiProviderService.GetAll()`
- `AiModelService.GetByProviderId(provider.Id)`

返回示例：

```json
{
  "models": [
    {
      "name": "madorin/default:latest",
      "model": "madorin/default:latest",
      "modified_at": "2026-04-29T00:00:00Z",
      "size": 0,
      "digest": "",
      "details": {
        "format": "madorin-proxy",
        "family": "madorin",
        "families": ["madorin"],
        "parameter_size": "",
        "quantization_level": ""
      }
    }
  ]
}
```

推荐暴露模型名：

```text
madorin/default:latest
{providerName}/{modelName}:latest
```

例如：

```text
DeepSeek/deepseek-chat:latest
OpenAI/gpt-4o:latest
Ollama/qwen2.5:latest
```

---

### 7.3 POST /api/chat

请求示例：

```json
{
  "model": "madorin/default:latest",
  "messages": [
    {
      "role": "user",
      "content": "你好"
    }
  ],
  "stream": true
}
```

内部映射：

- `model` 解析为 Madorin Provider/Model/Agent。
- `messages` 转换为内部通用消息。
- `system`、`user`、`assistant` 角色尽量保留。
- `stream=true` 时返回 NDJSON。
- `stream=false` 时返回完整 JSON。

流式响应示例：

```jsonl
{"model":"madorin/default:latest","created_at":"2026-04-29T00:00:00Z","message":{"role":"assistant","content":"你"},"done":false}
{"model":"madorin/default:latest","created_at":"2026-04-29T00:00:00Z","message":{"role":"assistant","content":"好"},"done":false}
{"model":"madorin/default:latest","created_at":"2026-04-29T00:00:00Z","message":{"role":"assistant","content":""},"done":true}
```

非流式响应示例：

```json
{
  "model": "madorin/default:latest",
  "created_at": "2026-04-29T00:00:00Z",
  "message": {
    "role": "assistant",
    "content": "你好"
  },
  "done": true
}
```

---

### 7.4 POST /api/generate

请求示例：

```json
{
  "model": "madorin/default:latest",
  "prompt": "你好",
  "stream": true
}
```

内部转换：

```text
prompt -> user message
```

流式响应示例：

```jsonl
{"model":"madorin/default:latest","created_at":"2026-04-29T00:00:00Z","response":"你","done":false}
{"model":"madorin/default:latest","created_at":"2026-04-29T00:00:00Z","response":"好","done":false}
{"model":"madorin/default:latest","created_at":"2026-04-29T00:00:00Z","response":"","done":true}
```

---

## 8. 代理模式设计

> 重要修正：Ollama Proxy 必须是独立外部调用通道，不能和 Madorin 主聊天窗口、组装体会话、历史上下文进行混合。代理请求应该进入专门的 Proxy Agent 实例或 Proxy Agent 通道，避免污染主会话，也避免外部客户端受到主聊天历史影响。
### 8.0 独立通道原则
Ollama Proxy 的设计原则调整为：
```text
外部软件 -> localhost:11434 -> OllamaProxyServerService -> Proxy 专用 Agent 通道 -> AI Provider
```
禁止链路：
```text
外部软件 -> localhost:11434 -> 主聊天窗口会话
外部软件 -> localhost:11434 -> 当前 Madorin 组装体会话
外部软件 -> localhost:11434 -> AiChatHostedService 的主 UI 会话上下文
```
也就是说：
- 不复用主窗口当前会话。
- 不读取主聊天历史。
- 不向主聊天历史写入外部请求。
- 不触发主聊天窗口的消息流。
- 不与当前 UI 正在使用的 Agent 实例共享上下文。
- 外部调用只走专门的 Proxy Agent/Proxy Session。
### 8.0.1 Proxy Agent 定义
Ollama Proxy 需要拥有自己的智能体配置，称为：
```text
Proxy Agent
```
Proxy Agent 可以来自两种方式：
1. 用户在 UI 中选择一个已有 Agent，然后为代理创建隔离运行实例。
2. 用户新建一个专门用于外部调用的 Agent，例如：`Ollama Proxy Agent`。
无论哪种方式，运行时都必须隔离：
```text
Agent 配置可以复用
Agent 会话上下文不能复用
```
也就是说，可以复用：
- Agent 名称
- Instructions
- 默认 Provider
- 默认 Model
- 温度、TopP、MaxTokens 等配置
- 插件/MCP 配置，后续按需支持
不能复用：
- 主聊天窗口的历史消息
- 当前 UI 会话状态
- 主聊天窗口的 token 统计源
- 当前正在生成的对话任务
### 8.0.2 Proxy Session 管理
建议为代理请求单独引入 Proxy Session 概念：
```csharp
public interface IAiProxySessionManager
{
    IAiProxySession GetOrCreateSession(string sessionKey);
    void ClearSession(string sessionKey);
}
```
第一版可以采用无状态策略：
```text
每个 /api/chat 或 /api/generate 请求独立构造上下文
请求结束即释放
```
后续如果要支持外部客户端连续对话，可按以下方式隔离：
```text
sessionKey = clientId + model + optional conversationId
```
但即使支持连续对话，也只能保存在 Proxy Session 中，不能写入 Madorin 主聊天历史。
### 8.0.3 后端接口调整
原方案中的 `IAiProxyAgentBackend` 应明确表示它服务于独立代理通道，不应调用主 UI 会话引擎。
建议接口语义调整为：
```csharp
public interface IAiProxyAgentBackend
{
    IReadOnlyList<AiProxyModelDescriptor> ListModels();
    IAsyncEnumerable<AiProxyChatDelta> ChatAsync(
        AiProxyAgentRequest request,
        CancellationToken cancellationToken);
}
```
实现类建议命名：
```text
CortanaOllamaProxyAgentBackend
```
职责：
- 根据 Proxy 配置加载 Proxy Agent。
- 为外部请求构造独立消息上下文。
- 创建或复用 Proxy 专用 ChatClient。
- 不调用主聊天窗口的会话处理流程。
- 不读写主聊天历史。
- 将 Token 使用量写入 Proxy 专用统计。
### 8.0.4 Token 统计隔离
当前 `AIAgentFactory` 中已有：
```csharp
LastInputTokens
MaxContextTokens
ContextUsageRatio
TokenUsageChanged
```
这些可以作为实现参考，但 Proxy 应拥有独立统计源，例如：
```csharp
public sealed class ProxyUsageTracker
{
    public long LastInputTokens { get; }
    public long TotalOutputTokens { get; }
    public long MaxContextTokens { get; }
    public double ContextUsageRatio { get; }
    public long TotalRequests { get; }
    public long FailedRequests { get; }
    public event Action? UsageChanged;
}
```
UI 中 `ProxyWindow` 应绑定 ProxyUsageTracker，而不是直接绑定主聊天窗口的 `AIAgentFactory` 统计，避免两个通道的数据互相覆盖。

Ollama 客户端传来的 messages 和 Madorin 内部 Agent 有两种组合方式。

### 8.1 ModelOnly 模式

特点：

- 只将外部 messages 作为上下文。
- 不混入 Madorin 当前聊天窗口历史。
- 不使用插件/MCP/工具。
- 行为最接近普通 Ollama 模型。

优点：

- 兼容性最好。
- 外部客户端行为更可控。

缺点：

- 无法完整体现 Madorin Agent 能力。

---

### 8.2 ProxyAgent 模式

特点：

- 外部请求进入 Proxy 专用 Madorin Agent 隔离实例。
- 可带 Agent 系统提示词、厂商、模型配置。
- 后续可扩展插件/MCP/工具能力。

优点：

- 真正将 Madorin 的 AI 代理能力暴露给外部软件。

缺点：

- 外部客户端未必理解工具调用、插件行为。
- 需要维护独立 Proxy Session，不能复用或写入主聊天历史。

---

### 8.3 推荐默认值

第一版建议：

```text
默认模式：ProxyAgent
可选模式：ModelOnly
```

配置项：

```text
Proxy.Ollama.Mode = ProxyAgent | ModelOnly
```

---

## 9. 设置项规划

建议通过 `SystemSettingsService` 保存。

配置 Key：

```text
Proxy.Ollama.Enabled
Proxy.Ollama.Host
Proxy.Ollama.Port
Proxy.Ollama.ProviderId
Proxy.Ollama.ModelId
Proxy.Ollama.AgentId
Proxy.Ollama.Mode
Proxy.Ollama.ExposeDefaultModel
Proxy.Ollama.AllowLan
Proxy.Ollama.RequireApiKey
Proxy.Ollama.ApiKey
Proxy.Ollama.MaxConcurrentRequests
```

默认值：

```text
Proxy.Ollama.Enabled = false
Proxy.Ollama.Host = localhost
Proxy.Ollama.Port = 11434
Proxy.Ollama.Mode = ProxyAgent
Proxy.Ollama.ExposeDefaultModel = true
Proxy.Ollama.AllowLan = false
Proxy.Ollama.RequireApiKey = false
Proxy.Ollama.ApiKey = ""
Proxy.Ollama.MaxConcurrentRequests = 2
```

---

## 10. UI 方案

### 10.1 文件

```text
Src\Netor.Cortana.UI\Views\Proxy\ProxyWindow.axaml
Src\Netor.Cortana.UI\Views\Proxy\ProxyWindow.axaml.cs
Src\Netor.Cortana.UI\Views\Proxy\ProxyViewModel.cs
```

### 10.2 界面元素

建议包含：

1. 当前状态

```text
未启动 / 运行中 / 端口占用 / 启动失败 / 已停止
```

2. 开关按钮

```text
启动代理 / 停止代理
```

3. 地址显示

```text
http://localhost:11434
```

4. 端口设置

```text
默认：11434
```

5. 代理模式

```text
ProxyAgent / ModelOnly
```

6. 厂商选择

```text
Proxy Provider ComboBox
```

7. 模型选择

```text
Proxy Model ComboBox
```

8. Agent 选择

```text
Proxy Agent ComboBox
```

9. 上下文监控

```text
LastInputTokens / MaxContextTokens / ContextUsageRatio
```

10. 使用量监控

```text
请求总数
成功数
失败数
当前并发数
最近错误
```

---

## 11. AOT 兼容要求

### 11.1 必须使用 Source Generator JSON

示例：

```csharp
[JsonSerializable(typeof(OllamaVersionResponse))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatResponse))]
[JsonSerializable(typeof(OllamaGenerateRequest))]
[JsonSerializable(typeof(OllamaGenerateResponse))]
[JsonSerializable(typeof(OllamaErrorResponse))]
internal partial class OllamaProxyJsonContext : JsonSerializerContext
{
}
```

反序列化：

```csharp
var request = await JsonSerializer.DeserializeAsync(
    context.Request.InputStream,
    OllamaProxyJsonContext.Default.OllamaChatRequest,
    cancellationToken);
```

序列化：

```csharp
await JsonSerializer.SerializeAsync(
    response.OutputStream,
    payload,
    OllamaProxyJsonContext.Default.OllamaChatResponse,
    cancellationToken);
```

---

### 11.2 禁止写法

避免：

```csharp
JsonSerializer.Serialize(objectValue)
JsonSerializer.Deserialize<T>(json) // 未提供 JsonTypeInfo 时不推荐
JsonSerializer.SerializeToElement(objectValue)
dynamic
反射扫描类型
运行时表达式编译
```

---

## 12. HTTP 监听设计

### 12.1 默认监听

建议默认只监听：

```text
http://localhost:11434/
```

或者：

```text
http://127.0.0.1:11434/
```

### 12.2 不建议默认监听

```text
http://+:11434/
```

原因：

- 可能需要管理员 URL ACL。
- 会暴露到局域网，有安全风险。

如果用户开启局域网访问，再提示需要管理员授权：

```powershell
netsh http add urlacl url=http://+:11434/ user=Everyone
```

---

## 13. 端口冲突处理

Ollama 官方默认使用：

```text
11434
```

如果用户已安装 Ollama，本端口可能被占用。

启动时需要处理：

- 成功：状态为 Running。
- `HttpListenerException` / 地址占用：状态为 PortInUse。
- 权限不足：状态为 AccessDenied。
- 其他异常：状态为 Failed，并记录最近错误。

UI 显示：

```text
端口 11434 被占用，可能是 Ollama 官方服务正在运行。请关闭 Ollama 或修改代理端口。
```

---

## 14. 并发与取消

### 14.1 并发限制

建议第一版默认：

```text
MaxConcurrentRequests = 2
```

实现：

```csharp
private readonly SemaphoreSlim _requestLimiter;
```

避免多个外部软件同时请求导致桌面程序卡顿。

### 14.2 客户端断开取消

流式写入时，如果 `OutputStream.WriteAsync` 抛异常，应取消内部 AI 请求。

伪代码：

```csharp
try
{
    await foreach (var chunk in backend.ChatAsync(input, linkedToken))
    {
        await WriteNdjsonAsync(context.Response, chunk, linkedToken);
    }
}
catch (IOException)
{
    requestCts.Cancel();
}
catch (HttpListenerException)
{
    requestCts.Cancel();
}
```

---

## 15. 安全设计

### 15.1 默认安全策略

默认：

```text
只监听 localhost
不开放局域网
不需要 API Key
```

因为 localhost 场景主要给本机软件使用。

### 15.2 局域网访问

如果用户启用：

```text
AllowLan = true
```

必须建议同时启用：

```text
RequireApiKey = true
```

请求头兼容：

```http
Authorization: Bearer {apiKey}
```

或：

```http
X-API-Key: {apiKey}
```

---

## 16. DI 注册方案

### 16.1 Networks 注册

修改：

```text
Src\Netor.Cortana.Networks\NetworkServiceExtensions.cs
```

增加：

```csharp
services.AddSingleton<OllamaProxyServerService>();
services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OllamaProxyServerService>());
```

### 16.2 AI 注册

修改：

```text
Src\Netor.Cortana.AI\AIServiceExtensions.cs
```

增加：

```csharp
services.AddSingleton<IAiProxyAgentBackend, CortanaOllamaProxyAgentBackend>();
```

前提：

- `IAiProxyAgentBackend` 放在 AI 和 Networks 都可引用的项目中，推荐 `Netor.Cortana.Entitys.Proxy`。

---

## 17. 实施阶段规划

### 阶段一：MVP 协议跑通

目标：外部软件可以通过 Ollama 协议调用 Madorin。

实现：

```http
GET  /api/version
GET  /api/tags
POST /api/chat
POST /api/generate
```

限制：

- 只监听 localhost。
- 默认端口 11434。
- 支持 stream true/false。
- 默认使用专用 Proxy Agent/Provider/Model，不复用主聊天窗口当前会话。
- 使用 AOT Source Generator JSON。

验收方式：

```powershell
curl http://localhost:11434/api/version
curl http://localhost:11434/api/tags
```

以及：

```powershell
curl http://localhost:11434/api/chat `
  -H "Content-Type: application/json" `
  -d '{"model":"madorin/default:latest","messages":[{"role":"user","content":"你好"}],"stream":false}'
```

---

### 阶段二：设置 UI

目标：用户可以通过界面控制代理。

实现：

- `ProxyWindow.axaml`
- `ProxyWindow.axaml.cs`
- `ProxyViewModel.cs`

功能：

- 启动/停止代理。
- 设置端口。
- 选择模式。
- 选择 Proxy 专用 Provider/Model/Agent。
- 显示当前状态。
- 显示上下文使用量。
- 显示请求统计和最近错误。

---

### 阶段三：兼容性增强

实现：

```http
GET  /
HEAD /
POST /api/show
POST /api/ps
```

增强：

- 模型名映射优化。
- API Key。
- 局域网访问。
- 请求日志。
- 并发限制可配置。

---

## 18. 风险与注意事项

### 18.1 端口冲突

11434 可能已被官方 Ollama 占用。

处理：

- UI 明确提示。
- 支持修改端口。
- 不要自动抢占端口。

### 18.2 Agent 语义和 Ollama 语义差异

Ollama 是模型接口，Madorin 是 Agent 接口。

处理：

- 提供 `Agent` / `ModelOnly` 两种模式。
- 默认 ProxyAgent，兼顾 Madorin 特色，同时与主聊天会话彻底隔离。
- 必要时给外部客户端说明模型名：`madorin/default:latest`。

### 18.3 AOT 裁剪

处理：

- 所有 Ollama DTO 必须进入 `JsonSerializerContext`。
- 避免反射式序列化。
- Release AOT 发布作为验收项。

### 18.4 局域网安全

处理：

- 默认只允许 localhost。
- 局域网访问需要明确开启。
- 局域网访问建议强制 API Key。

---

## 19. 推荐最终形态

```text
Cortana.exe
  ├─ 主聊天窗口
  ├─ 设置窗口
  ├─ Ollama Proxy 设置窗口
  └─ 内置 HttpListener 代理
        ├─ http://localhost:11434/api/version
        ├─ http://localhost:11434/api/tags
        ├─ http://localhost:11434/api/chat
        └─ http://localhost:11434/api/generate
```

外部软件配置：

```text
Base URL: http://localhost:11434
Model: madorin/default:latest
```

---

## 20. 结论

该方案可行，且非常适合当前工程。

最终建议：

1. HTTP 服务放在 `Netor.Cortana.Networks`。
2. Proxy Agent 调用实现放在 `Netor.Cortana.AI`，并与主聊天会话隔离。
3. 抽象接口放在 `Netor.Cortana.Entitys.Proxy`，接口语义必须明确为独立代理通道。
4. 设置界面放在 `UI.Views.Proxy`。
5. 使用 `HttpListener`，不引入 ASP.NET Core。
6. 使用 `System.Text.Json Source Generator`，确保 Native AOT 兼容。
7. 默认监听 `localhost:11434`，不要默认开放局域网。
8. 第一阶段先实现 `/api/version`、`/api/tags`、`/api/chat`、`/api/generate`。


