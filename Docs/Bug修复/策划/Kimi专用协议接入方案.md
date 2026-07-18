# Kimi 专用协议接入方案

## 背景

Kimi（Moonshot AI）的 API 在工具调用 + 思维链场景下，有两处与标准 OpenAI 兼容协议不同的行为，若按通用 `OpenAiCompatible` 协议发送请求，会导致思维内容丢失或多轮工具调用时上下文断裂：

1. **thinking 参数**：开启思维模式需要在请求体顶层注入 `thinking` 字段（等效于 Python SDK 的 `extra_body`），该字段不属于 OpenAI 规范，标准 SDK 不会自动携带。
2. **partial 标记**：在多轮工具调用时，含 `tool_calls` 的 assistant 消息必须携带 `"partial": true`，用于告知 Kimi 该轮思维过程尚未结束（思维被工具调用截断）。若缺失此字段，Kimi 将拒绝请求或返回错误。

> 参考文档：
> - <https://platform.kimi.com/docs/api/tool-use>
> - <https://platform.kimi.com/docs/api/partial>

---

## 方案设计

### 整体思路

参照已有的 `DeepseekProviderDriver` 架构，为 Kimi 新增一套专用协议驱动：

- **`KimiProviderDriver`**：注册协议 ID 为 `"Kimi"`，使用专属命名 HttpClient，继承 `OpenAiCompatibleProviderDriverBase` 复用模型列表拉取逻辑。
- **`KimiOverrideHandler`**：`DelegatingHandler` 拦截器，在发送前重写请求体，完成两项注入。

DeepSeek 与 Kimi 的对比：

| 需求 | DeepSeek | Kimi |
|---|---|---|
| 思维内容回传 | assistant 消息补写 `reasoning_content` | 顶层注入 `thinking: {type: "enabled"}` |
| 多轮工具调用 | 最后一条含 tool_calls 的 assistant 写入 reasoning | 所有含 `tool_calls` 的 assistant 消息写入 `"partial": true` |

### 新增文件

```
Src/Netor.Cortana.AI/Drivers/Providers/Kimi/
  KimiProviderDriver.cs    — 协议驱动，定义 Id="Kimi"，DisplayName="Kimi 专用协议"
  KimiOverrideHandler.cs   — HTTP 拦截器，执行请求体重写
```

### 修改文件

```
Src/Netor.Cortana.AI/Infrastructure/AIServiceExtensions.cs
  +  services.AddTransient<KimiOverrideHandler>()
         .AddHttpClient("Kimi")
         .AddHttpMessageHandler<KimiOverrideHandler>();
  +  services.AddSingleton<IAiProviderDriver, KimiProviderDriver>();
```

---

## 关键实现细节

### KimiOverrideHandler 重写逻辑

```
输入：原始 OpenAI 兼容请求体（JSON）

步骤 1 — partial 注入：
  遍历 messages 数组
  对每条 role=assistant 且含 tool_calls 的消息：
    若不存在 "partial" 字段 → 写入 "partial": true

步骤 2 — thinking 注入：
  若顶层不存在 "thinking" 字段 → 写入：
    "thinking": { "type": "enabled" }

优化：若 thinking 已存在且所有需要 partial 的消息均已标记，跳过重写直接透传。
```

### 协议注册

`ProviderType` 使用字符串 `"Kimi"`，`CanHandle` 通过基类的大小写不敏感比较自动匹配。UI 的协议下拉框从 `AgentFactory.GetDriverDefinitions()` 动态加载，无需修改 XAML 即可出现"Kimi 专用协议"选项。

---

## 数据库侧

无需迁移。`AiProviders.ProviderType` 为普通文本字段，已有记录不受影响。用户在设置页选择"Kimi 专用协议"后，新建/编辑的提供商实体会写入 `ProviderType = "Kimi"`，驱动注册表按此字段路由到 `KimiProviderDriver`。

---

## 验收标准

1. 设置页「协议」下拉框出现「Kimi 专用协议」选项，可正常保存。
2. 单轮对话（无工具）：请求体含 `"thinking": {"type": "enabled"}`，无 `partial` 注入。
3. 多轮工具调用：replay 的 assistant 消息含 `"partial": true`，顶层含 `thinking`。
4. 思维内容（thinking tokens）在前端正常回传显示。
5. 普通模型（不带 thinking 的 Kimi 模型）：`thinking` 字段存在但 Kimi 服务端会忽略，不影响正常响应。
