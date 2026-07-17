# Git 提交记录 - DeepSeek 代理思维内容回传

## 提交信息

**提交日期**: 2026-05-06  
**提交类型**: Feature / Fix / Tests  
**影响范围**: OpenAI 兼容代理、DeepSeek reasoner 模型、代理请求缓存、Networks 单元测试

---

## 修改概述

本次提交为 OpenAI 兼容代理补齐 DeepSeek reasoner 模型所需的思维内容回传能力。由于外部客户端不会在下一轮请求中回传 `reasoning_content`，代理侧新增 DeepSeek 专用内存缓存，在收到 DeepSeek 响应后提取 reasoning，并在后续 DeepSeek 请求中按协议补写 `assistant.reasoning_content`。

该逻辑严格限制在 DeepSeek provider 下启用，不改变 OpenAI、Azure OpenAI、GLM、Custom 等普通 OpenAI 兼容通道的请求结构。

---

## 修改文件清单

### 1. DeepSeek reasoning 回放缓存

- `Src/Netor.Cortana.Networks/Proxy/DeepSeekReasoningReplayCache.cs`

**修改内容**:

- 新增 DeepSeek 专用 reasoning 内存缓存。
- 缓存 Key 使用 `provider.Id + exposedModel + clientKey` 组合，避免不同厂商、模型和客户端串线。
- 每个 Key 最多保留 32 条 reasoning。
- 缓存条目默认 2 小时过期。
- 空 Key 或空 reasoning 不写入缓存。

**影响功能**: 支持在客户端不回传 reasoning 的情况下，由代理侧保留 DeepSeek 上一次响应中的思维内容，供后续工具调用或连续请求回传。

### 2. DeepSeek 请求重写与响应提取

- `Src/Netor.Cortana.Networks/Proxy/DeepSeekReasoningRequestRewriter.cs`

**修改内容**:

- 新增 DeepSeek provider 识别逻辑：
  - `ProviderType == Deepseek / DeepSeek`
  - 或 Provider 名称、URL 包含 `deepseek`
- 请求前遍历 `messages`，为所有 `assistant` 消息补写 `reasoning_content`。
- 若消息已有 `reasoning_content`，保持不变。
- 若消息只有 `reasoning`，复制为 `reasoning_content`。
- 对最后一条包含 `tool_calls` 的 assistant 注入缓存中的全部 reasoning。
- 其他 assistant 明确写入空字符串，满足 DeepSeek 对 assistant 消息格式的要求。
- 从非流式 JSON 响应中提取：
  - `choices[].message.reasoning_content`
  - `choices[].message.reasoning`
- 从 SSE 流式响应中提取：
  - `choices[].delta.reasoning_content`
  - `choices[].delta.reasoning`
- JSON 属性重排时使用 `DeepClone()`，避免 `JsonNode` 父节点冲突。

**影响功能**: DeepSeek reasoner 在工具调用、多轮代理请求中可收到必要的 `reasoning_content`，避免因缺少思维内容回传而拒绝服务。

### 3. OpenAI 兼容代理接入 DeepSeek 分支

- `Src/Netor.Cortana.Networks/Proxy/OpenAiCompatibleRawProxy.cs`
- `Src/Netor.Cortana.Networks/Proxy/OpenAiCompatibleEndpoints.cs`
- `Src/Netor.Cortana.Networks/Extensions/NetworkServiceExtensions.cs`

**修改内容**:

- `OpenAiCompatibleRawProxy` 注入 `DeepSeekReasoningReplayCache`。
- `ForwardChatCompletionsAsync` 增加 `clientKey` 参数。
- 仅当当前 provider 被识别为 DeepSeek 时：
  - 根据 provider/model/client 构造缓存 Key。
  - 请求上游前读取缓存 reasoning 并重写请求体。
  - 上游成功响应后提取 reasoning 并写入缓存。
- `OpenAiCompatibleEndpoints` 新增 clientKey 解析：
  - `X-Madorin-Session-Id`
  - `X-Madorin-Conversation-Id`
  - `X-Request-Id`
  - `RemoteEndPoint`
- DI 中注册 `DeepSeekReasoningReplayCache` 单例。

**影响功能**: DeepSeek 专用逻辑在 raw proxy 中生效；普通 OpenAI 兼容请求不进入该分支，保持原有透传行为。

### 4. 单元测试补充

- `Tests/Netor.Cortana.Networks.Tests/DeepSeekReasoningRequestRewriterTests.cs`

**修改内容**:

- 验证 OpenAI provider 不被识别为 DeepSeek。
- 验证 assistant 消息自动补空 `reasoning_content`。
- 验证 `reasoning` 字段会复制为 `reasoning_content`。
- 验证最后一条带 `tool_calls` 的 assistant 使用缓存 reasoning。
- 验证非流式响应 reasoning 提取。
- 验证 SSE 流式响应 reasoning 累积提取。
- 验证缓存按 provider/model/client 隔离。

---

## 兼容性说明

- OpenAI 兼容通道不受影响：只有 DeepSeek provider 会执行请求重写和 reasoning 缓存。
- 缓存为内存级别，进程重启后清空。
- 当前以请求头或远端地址作为客户端隔离维度；如果后续客户端能稳定传入 `X-Madorin-Session-Id`，隔离效果会更精确。
- 第一版不访问历史数据库，避免在 raw proxy 中引入不可靠的会话反查逻辑。

---

## 验证记录

已执行：

```bash
dotnet test Tests/Netor.Cortana.Networks.Tests/Netor.Cortana.Networks.Tests.csproj
```

结果：

- [x] 总计 9 个测试
- [x] 成功 9 个
- [x] 失败 0 个

---

## 提交范围说明

本次提交仅包含 DeepSeek 代理 reasoning 回传相关改动和对应提交文档。

未纳入本次提交的工作区现有改动包括：

- 插件外置化相关改动
- ProjectSettingsProvider / FileMemoryProvider 迁移相关改动
- 其他与本次 DeepSeek 代理回传无关的未提交文件

---

## 提交说明

建议提交信息：

`fix(proxy): replay DeepSeek reasoning content`

建议提交描述：

- add DeepSeek reasoning replay cache scoped by provider, model and client
- rewrite DeepSeek assistant messages with required reasoning_content
- extract reasoning from JSON and SSE upstream responses
- keep OpenAI-compatible providers outside the DeepSeek rewrite path
- add Networks tests for DeepSeek reasoning replay behavior

---

**提交人**: GitHub Copilot  
**审核人**: TBD
