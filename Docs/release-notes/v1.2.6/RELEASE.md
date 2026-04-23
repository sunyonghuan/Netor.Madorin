# Netor.Cortana v1.2.6 发布说明

**发布日期：** 2026 年 4 月 23 日
**运行时：** .NET 10 | Avalonia 12 | Native AOT
**平台：** Windows 10/11 x64

---

## 🎯 版本亮点

本版本补齐「宿主统一模型路由」与「聊天历史结构化持久化」两块核心基础设施：

- **用途级模型路由**（宿主先行原则）：用户在系统设置里按「用途」给不同任务选模型，宿主统一分发 `IChatClient`，后续插件申请 LLM 无需重复造轮子。
- **工具调用/结果结构化入库**：AI 工具调用（FunctionCall / FunctionResult）不再仅以文本快照落库，而是以 AOT 安全的结构化 JSON 持久化，会话恢复时工具上下文完整重建。

所有改动保持 Native AOT 友好，零反射，零额外运行时警告。

---

## ✨ 新功能

### 用途级模型路由（`ModelPurposeResolver`）

- 新增宿主共享服务 `Netor.Cortana.AI.Providers.ModelPurposeResolver`，按「用途键」从 `SystemSettings` 解析对应模型并实例化缓存 `IChatClient`
- 按键缓存、配置变更自动释放旧实例，避免连接泄漏
- `Compaction.ModelId`（会话压缩摘要）已迁移到该服务，`ChatHistoryDataProvider.ResolveCompactionClient` 改为一行调用
- 新增系统设置 `Memory.ModelId`（分组「记忆体系」，valueType=`model`），用于插件的记忆加工任务，未配置时回退到当前对话模型
- 系统设置页自动用 Provider+Model 级联 ComboBox 渲染，无需额外 UI 代码

### 聊天消息结构化内容持久化

- `ChatMessages` 表新增 `ContentsJson` 列，自动迁移（`TryAddColumn`）对老库兼容
- 新增 POCO `PersistedContent`（Kind = text / functionCall / functionResult / data）+ 源生成 `PersistedContentJsonContext`，完全替代 `AIContent` 多态 JSON（后者在 AOT 下会触发反射）
- 工具调用参数（`IDictionary<string, object?>`）通过 `NormalizeToJsonElement` 规范化为 `Dictionary<string, JsonElement>` 后再序列化，避免 object 多态要求反射
- `StoreChatHistoryAsync` / `SavePartialResponseAsync` / `SaveUserMessage` 三条入库路径同步写 `Content`（人类可读文本快照）和 `ContentsJson`（结构化快照）
- `ProvideChatHistoryAsync` 的 `BuildContentsWithAssets` 优先从 `ContentsJson` 重建 `FunctionCallContent` / `FunctionResultContent`，失败才降级为 `TextContent(Content)`；图片资源仍按原逻辑追加

---

## 🛠 重构

- `ChatHistoryDataProvider` 移除私有字段 `_cachedCompactionClient` / `_cachedCompactionModelId`，相关缓存逻辑统一交由 `ModelPurposeResolver` 管理
- `ChatHistoryDataProvider` 构造函数去掉不再使用的 `AiModelService` 参数
- `BuildContentsWithAssets` 签名改为接收 `ChatMessageEntity`，内部根据 `ContentsJson` 有无自动选择重建策略

---

## 🧭 AOT 风险评估

- 未引入任何依赖反射的序列化路径
- 所有新增 JSON 序列化均基于 `[JsonSerializable]` 源生成上下文（`PersistedContentJsonContext`、`PersistedArgumentsJsonContext`）
- `dotnet build Src/Netor.Cortana.AI/Netor.Cortana.AI.csproj` 零 IL2026 / IL3050 / SYSLIB 警告

---

## 📋 变更文件清单

### 新增

- `Src/Netor.Cortana.AI/Providers/ModelPurposeResolver.cs` — 用途级模型路由解析器
- `Src/Netor.Cortana.AI/Persistence/PersistedContent.cs` — 结构化内容 POCO + 源生成上下文
- `Docs/release-notes/v1.2.6/RELEASE.md` — 本发布说明

### 修改

- `Src/Netor.Cortana.AI/AIServiceExtensions.cs` — 注册 `ModelPurposeResolver`
- `Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs` — 路由重构、INSERT 写 `ContentsJson`、`BuildContentsWithAssets` 还原结构化内容
- `Src/Netor.Cortana.AI/AiChatHostedService.cs` — 用户消息入库写 `ContentsJson`
- `Src/Netor.Cortana.AI/Extensions/ChatMessageExtensions.cs` — 新增 `BuildContentsJson` / `ParseContentsJson` 与 POCO 映射
- `Src/Netor.Cortana.Entitys/Entities/ChatMessageEntity.cs` — 新增 `ContentsJson` 属性
- `Src/Netor.Cortana.Entitys/Services/ChatMessageService.cs` — 读写 `ContentsJson` 列，老库缺列兼容
- `Src/Netor.Cortana.Entitys/CortanaDbContext.cs` — `CREATE TABLE` 与 `EnsureMigrations` 同步新增 `ContentsJson` 列
- `Src/Netor.Cortana.Entitys/Services/SystemSettingsService.cs` — 首次播种 `Memory.ModelId`
- `Src/Netor.Cortana.AvaloniaUI/App.axaml.cs` — 启动迁移时 `EnsureSetting("Memory.ModelId", …)`
- `Src/Netor.Cortana.AvaloniaUI/Netor.Cortana.AvaloniaUI.csproj` — 版本号升级至 1.2.6

---

## ⬆️ 升级说明

- 数据库：启动时自动执行增量迁移（`ALTER TABLE ChatMessages ADD COLUMN ContentsJson`），对老库幂等安全
- 设置：首次启动后「系统设置 → 记忆体系 → 记忆加工模型」会出现新选项，未配置时沿用当前对话模型
- 历史数据：老消息没有 `ContentsJson`，加载时自动退化为文本模式，不影响使用；新消息之后完整保留工具调用/结果结构

---

## 📦 本地发布产物

- `Realases/Netor.Cortana-v1.2.6-win-x64.zip`
- `Realases/Netor.Cortana-v1.2.6-win-x64.sha256`
