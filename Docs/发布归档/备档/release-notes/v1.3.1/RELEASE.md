# Madorin v1.3.1 Release Notes

发布日期：2026-04-30

## 概览

v1.3.1 是 Madorin 向“模型入口自由化”迈出的一大步：新增 **Ollama 本地协议代理**，可以把 Madorin 中已经配置好的国产大模型、企业模型、私有 API 模型，通过 `http://localhost:11434` 伪装成本机 Ollama 模型暴露给外部工具。

这意味着：不用安装第三方 VSCode 插件，不用修改 VSCode 代码，不用修改 Visual Studio 代码，也不用单独开发 IDE 造轮子，就能让支持 Ollama 本地协议的编辑器和工具，把网络大模型 API 当成本地模型一样调用。

一句话：**Madorin 不替代编辑器，Madorin 接管模型入口。**

## 核心亮点：把国产模型直接送进 Copilot 工作流

过去，AI 编程工具的模型入口往往被平台锁死：

- 想用高级模型，要购买固定月费会员。
- 想用国产模型，要等官方适配。
- 想用企业内部模型，要自己写插件、改配置、绕协议。
- 想接私有化部署模型，常常要牺牲原生编辑器体验。

v1.3.1 直接把这件事打穿。

Madorin 内置的 Ollama 协议代理可以把远程 API 包装为本地 Ollama 服务：

```text
http://localhost:11434
```

外部工具以为自己在调用本地模型，实际上请求会由 Madorin 转发到你配置好的国产模型、企业网关、私有 API 或任何 OpenAI-compatible 服务。

## 这不是造 IDE，这是释放现有编辑器生态

很多项目会选择重新做一个 AI IDE，但 Madorin 反过来：不重复造轮子，不和 VSCode / Visual Studio 抢饭碗。

编辑器继续做它最擅长的事情：

- 原生代码编辑
- 智能代码补全
- 多文件上下文理解
- 代码重构
- 文档书写
- 工作计划拆解
- 项目级任务执行
- 成熟插件生态协作

Madorin 只负责一件更关键的事：**把用户真正想用的模型送进编辑器。**

这样既保留了 VSCode / Visual Studio 原生、成熟、稳定的代码工作流，又摆脱了“软件规定你只能用哪些模型”的限制。

## 新能力

### 1. Ollama 本地协议代理

新增本地协议代理服务，默认协议地址：

```text
http://localhost:11434
```

支持端点包括：

- `GET /`
- `GET /api/version`
- `GET /api/tags`
- `POST /api/chat`
- `POST /api/generate`
- `POST /api/show`
- `GET/POST /api/ps`
- `GET /v1/models`
- `POST /v1/chat/completions`

它让 Madorin 可以作为“本地模型网关”存在，对外表现为 Ollama，对内接入任意已配置 AI 厂商。

### 2. 国产模型 / 企业模型 / 私有 API 统一暴露

只要模型已经在 Madorin 中配置，就可以通过 Ollama 协议对外暴露。

适用场景包括：

- 国产大模型 API
- 企业内部模型网关
- 私有化部署模型
- OpenAI-compatible API
- 云端商业模型
- 局域网内统一模型入口

这使得第三方工具不需要理解每家厂商的 API 差异，只需要按 Ollama 协议调用本地地址即可。

### 3. 对编辑器极其友好

该能力天然适合 VSCode、Visual Studio 以及任何支持 Ollama 本地协议的工具。

它最大的价值不是“又多了一个聊天入口”，而是让模型接入编辑器原生工作流：

- 保留编辑器原生代码编辑能力
- 保留原有代码生成体验
- 保留文档书写能力
- 保留工作计划和任务执行体验
- 保留现有项目上下文能力
- 不破坏用户熟悉的快捷键、插件和工程环境

不需要为了换模型，牺牲整个 IDE 生态。

### 4. OpenAI 兼容接口原始透传

新增 `/v1/chat/completions` 兼容通道，用于最大程度保留上游协议字段。

这对工具调用类客户端非常重要，因为它可以保留：

- `tools`
- `tool_choice`
- `tool_calls`
- 厂商扩展字段
- 其他 OpenAI-compatible 请求字段

Madorin 在这里不做过度“翻译”，尽量少干预，让工具链保持原生兼容。

### 5. 模型名前缀隔离

对外暴露模型时统一使用 `madorin-` 前缀，避免和本机已有 Ollama 模型冲突。

例如：

```text
qwen-plus       -> madorin-qwen-plus
deepseek-chat   -> madorin-deepseek-chat
```

外部工具看到的是本地模型名，Madorin 内部会自动还原到真实模型名调用上游 API。

### 6. 更真实的 Ollama 模型形状

模型列表和模型详情会模拟真实 Ollama 返回结构，包括：

- `name`
- `model`
- `modified_at`
- `size`
- `digest`
- `details.format`
- `details.family`
- `details.parameter_size`
- `details.quantization_level`
- `capabilities`
- `model_info`

这让第三方工具更容易把 Madorin 暴露的云端模型识别为“本地可用模型”。

## 对用户意味着什么

### 模型选择权回到用户手里

软件不应该决定你只能用哪几个模型。

v1.3.1 之后，用户可以把自己购买的 API、企业分配的模型、国产厂商模型、私有部署模型，以本地 Ollama 协议的方式交给编辑器使用。

### 成本控制权回到用户手里

不再因为想用高端模型能力，就必须购买某个平台固定月费会员。

你可以选择：

- 普通任务用便宜模型
- 复杂推理用高端模型
- 企业项目用内部模型
- 私有代码用私有化模型
- 临时需求按量调用 API

Madorin 把模型调度入口开放出来，成本和能力都由用户自己控制。

### 国产模型进入主流开发工作流

国产模型不再只能停留在网页聊天、控制台 Demo 或孤立插件里。

通过 Ollama 本地协议代理，它可以进入现有编辑器生态，参与真实项目开发：

- 写代码
- 改代码
- 补文档
- 拆任务
- 执行计划
- 辅助重构
- 支持工程级上下文工作流

这才是真正有生产力的模型接入方式。

## 架构说明

本版本采用轻量级 `HttpListener` 实现本地协议服务，不引入 ASP.NET Core，保持 Native AOT 友好。

核心结构：

- `OllamaProxyServerService`：监听、启停、状态、限流
- `ProxyRouteDispatcher`：Ollama / OpenAI 兼容路由分发
- `ProxyModelEndpoints`：模型列表、模型详情、OpenAI 模型列表
- `ProxyChatEndpoints`：Ollama 原生 `/api/chat`、`/api/generate`
- `OpenAiCompatibleEndpoints`：OpenAI 兼容 `/v1/chat/completions`
- `OpenAiCompatibleRawProxy`：原始 JSON 透传，保留工具调用字段
- `OllamaModelShapeFactory`：将远程模型包装成本地 Ollama 模型形状

设计原则：

- 不引入重量级 Web 框架
- 不复用主聊天历史
- 不污染主聊天会话
- 厂商由本地代理窗口选择
- 模型由外部工具请求传入
- API Key 使用 Madorin 已配置的厂商密钥
- 对外尽可能模拟真实 Ollama 行为

## Native AOT 与协议稳定性

本版本同步完成了 Ollama 协议 DTO 和 JSON 源生成上下文整理，保持 Native AOT 兼容。

验证结果：

```powershell
dotnet build Src\Netor.Cortana.Networks\Netor.Cortana.Networks.csproj --no-restore
```

结果：0 error

```powershell
dotnet build Src\Netor.Cortana.Networks\Netor.Cortana.Networks.csproj `
  --no-restore `
  /p:EnableAotAnalyzer=true `
  /p:EnableTrimAnalyzer=true `
  /p:TrimmerSingleWarn=false
```

结果：0 error，0 IL2xxx / IL3xxx warning

```powershell
dotnet build Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj --no-restore
```

结果：0 error

## 不兼容变更

无面向用户的不兼容变更。

该功能默认作为本地代理能力存在，不强制改变主聊天、不迁移历史记录、不改变已有模型配置。

## 升级与验证

1. 更新到 v1.3.1。
2. 在 Madorin 中配置 AI 厂商与模型。
3. 打开代理功能，确认本地地址为：

   ```text
   http://localhost:11434
   ```

4. 在支持 Ollama 的第三方工具中配置本地模型地址。
5. 拉取模型列表，确认可以看到 `madorin-` 前缀模型。
6. 发起聊天、代码生成或工具调用，确认请求经 Madorin 转发到目标模型。

## 一句话总结

v1.3.1 的意义不是“多了一个代理端口”，而是让 Madorin 成为模型自由入口：

**编辑器还是那个最强编辑器，模型不再是官方指定模型。国产模型、企业模型、私有模型，都可以通过本地 Ollama 协议进入主流开发工作流。**
