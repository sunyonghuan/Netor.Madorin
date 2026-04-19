# Gemini / GLM 接入存档

## 概要

- 当前主项目：`Src/Netor.Cortana.AvaloniaUI`
- 当前 AI 接入层：`Src/Netor.Cortana.AI`
- 存档目的：记录 Gemini 与 GLM 第一批接入的代码状态、验证边界与后续联调方式。
- 当前结论：代码已接入并构建通过，但因缺少有效 API Key，尚未完成真实模型拉取与对话联调。

## 本次已完成内容

### 1. 厂商驱动架构继续扩展

已在现有 Driver Registry 架构中接入两个新驱动：

- `GeminiProviderDriver`
- `GlmProviderDriver`

相关代码位置：

- `Src/Netor.Cortana.AI/Drivers/GeminiProviderDriver.cs`
- `Src/Netor.Cortana.AI/Drivers/GlmProviderDriver.cs`
- `Src/Netor.Cortana.AI/AIServiceExtensions.cs`
- `Src/Netor.Cortana.AI/AiModelFetcherService.cs`

### 2. Gemini 接入方式

- 依赖包：`Google.GenAI 1.0.0`
- 聊天接入方式：通过 `Google.GenAI.Client` 与 `AsIChatClient(...)` 转为现有 `IChatClient` 链路。
- 模型拉取方式：通过 `client.Models.ListAsync(...)` 获取 `Pager`，再 `await foreach` 遍历模型。
- 模型过滤逻辑：仅保留支持 `generateContent` 的模型动作。

当前建议配置：

- URL：`https://generativelanguage.googleapis.com`
- 首测模型：`gemini-2.0-flash`

### 3. GLM 接入方式

- 当前不是独立原生协议驱动。
- 当前实现复用 OpenAI 兼容驱动基类：`OpenAiCompatibleProviderDriverBase`
- 目标是先接入当前系统，后续如有必要再下沉为 GLM 原生协议专用驱动。

当前建议配置：

- URL：`https://open.bigmodel.cn/api/paas/v4`
- 首测模型：`glm-5.1`

### 4. UI 与配置链路

由于系统设置页已经改为动态读取驱动定义，因此 Gemini / GLM 加入 DI 后，UI 侧不需要再增加硬编码选项。

## 当前验证结果

### 1. 构建验证

已通过以下构建验证：

```powershell
dotnet build Src/Netor.Cortana.AvaloniaUI/Netor.Cortana.AvaloniaUI.csproj -v minimal
```

结论：构建成功。

### 2. 无 Key 条件下的烟测结论

已使用当前仓库中的真实驱动代码做过最小烟测。

#### Gemini

- 使用当前代码 + 官方 URL 时，若 `Key` 为空，会在驱动前置校验阶段直接失败。
- 当前失败信息：`Gemini 厂商缺少 API Key 配置。`

结论：

- 当前代码链路已接入。
- 但没有有效 Key 时，无法继续验证官方接口是否返回模型数据。

#### GLM

- 使用当前代码 + 官方 URL 时，空 Key 可打到服务端鉴权层。
- 当前观测到的失败信息：`401 Unauthorized`

结论：

- `https://open.bigmodel.cn/api/paas/v4` 对当前这版兼容驱动路径是可达的。
- 当前拿不到模型数据的直接原因是缺少有效 Key。

### 3. 本地配置现状

当前本地数据库中未发现 Gemini / GLM 的现成启用厂商配置。

当前已启用的主要配置仍是：

- Dmxapi Claude
- Ollama

因此，后续真实联调需要新增 Gemini / GLM 厂商记录后再进行。

## 后续待办

拿到有效 Key 后，按以下顺序继续：

### 1. 先测模型拉取

#### Gemini

- 厂商类型：`Gemini`
- URL：`https://generativelanguage.googleapis.com`
- Key：真实 Gemini API Key

验证点：

- 是否能成功拉取模型列表
- 返回模型中是否包含 `gemini-2.0-flash`

#### GLM

- 厂商类型：`GLM`
- URL：`https://open.bigmodel.cn/api/paas/v4`
- Key：真实智谱 API Key

验证点：

- 是否能成功拉取兼容模型列表
- 如果拉取失败，先手工创建模型 `glm-5.1`，继续测试对话链路

### 2. 再测消息对话

建议先只测纯文本对话，不要第一轮就叠加工具调用、多模态或复杂系统提示。

建议测试问题：

- `你好，请用一句话介绍你自己。`

验证点：

- 是否能返回正常文本
- 是否出现协议字段不匹配
- 是否出现模型名要求带前缀/不带前缀的差异

## 已知风险

### 1. GLM 当前为兼容模式接入

这意味着：

- 如果模型拉取成功，不代表所有高级能力都完全兼容。
- 如果后续函数调用、流式 usage、多模态字段出现差异，需要单独下沉原生 GLM 驱动。

### 2. Gemini 当前依赖有效 Key 才能继续向下验证

当前代码会在 Key 为空时直接拒绝继续请求，这是预期行为，不是 bug。

### 3. 旧主项目不在本次交付范围

本次工作范围仅针对：

- `Src/Netor.Cortana.AI`
- `Src/Netor.Cortana.AvaloniaUI`

旧项目 `Src/Netor.Cortana` 不作为本轮主要交付目标。

## 存档结论

当前阶段可以认定为：

- Gemini：代码接入完成，构建通过，待 Key 进行真实接口验证。
- GLM：代码接入完成，构建通过，官方 URL 已验证可达鉴权层，待 Key 进行真实模型与对话验证。

本文件用于后续拿到 Key 后快速续测，不需要重新回溯整段聊天记录。