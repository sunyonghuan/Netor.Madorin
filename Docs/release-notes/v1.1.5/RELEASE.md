# Netor.Madorin v1.1.5 发布说明

**发布日期：** 2026 年 4 月 16 日
**运行时：** .NET 10 | Avalonia 12 | Native AOT
**平台：** Windows 10/11 x64

---

## 🎉 版本亮点

本版本聚焦**对话体验优化**和**渲染引擎升级**，大幅提升了聊天气泡的文本显示质量，新增语音欢迎语自定义功能，修复了多项影响日常使用的关键问题。

---

## ✨ 新功能

### 🎤 AI 唤醒欢迎语自定义
- 新增 `Tts.WelcomeGreeting` 系统设置项，支持自定义唤醒时的欢迎语
- 默认欢迎语："主人，我在!"
- 在「系统设置 → 语音合成」中修改，重启应用后生效
- 相关提交：`fca2d4b` `000fe29` `a850ab3`

### 💾 取消对话时保存部分 AI 响应
- 当用户取消正在进行的 AI 对话时，已接收到的部分响应内容会自动保存到数据库
- 避免因误取消或网络波动导致长回复内容丢失
- 新增 `ChatHistoryDataProvider.SavePartialResponseAsync()` 方法
- 相关提交：`39b9cf1`

### 📋 聊天气泡跨行文字选择
- 合并相邻的段落和标题为单个 SelectableTextBlock，支持跨行拖选复制
- 代码块内置水平滚动条，长代码行不再撑破气泡宽度
- 相关提交：`c47462e`

---

## 🐛 问题修复

### 🔴 新会话建立失败（严重）
- **现象：** 点击新建会话后，消息仍保存到旧会话
- **原因：** `OnSessionCreated` 事件处理中未调用 `SwitchToSession` 切换到新会话
- **修复：** 新会话创建后立即切换 UI 上下文到新会话 ID
- 相关提交：`f348eae`

### 🟡 列表项文字不自动换行
- **现象：** 带列表格式（• 项目符号）的文字不换行，必须拉宽窗口才能查看
- **原因：** 列表项使用 Horizontal StackPanel 布局，内容无宽度限制
- **修复：** 改用 DockPanel 布局，前缀符号 Dock 到左侧，内容占满剩余宽度，TextWrapping.Wrap 生效
- 相关提交：`f348eae` `f015035`

### 🟡 任务列表显示类型名
- **现象：** Markdown 任务列表（`- [x] 内容`）显示为 `Markdig.Extensions.TaskLists.TaskList 内容`
- **原因：** Markdig 的 `TaskList` 是 `LeafInline`（内联元素），不是块级元素；之前在块级处理中查找导致 fallback 到 `ToString()`
- **修复：** 在 `BuildInlines()` 中正确处理 `TaskList` 内联，渲染为 ☑（已完成，绿色）/ ☐（未完成，灰色）
- 相关提交：`0133533`

### 🟢 设置保存异常
- **现象：** 修改欢迎语设置后保存时报 NullReferenceException
- **原因：** 后台线程调用 `App.Services.GetRequiredService` 引发并发异常
- **修复：** 移除后台缓存更新逻辑，改为启动时初始化，修改后重启生效
- 相关提交：`85fa10d` `1d116bd` `a4a9535`

---

## 📝 文档更新

- README 全面改版：插入 8 张产品截图，新增功能亮点展示区和核心卖点说明
- 修正唤醒词说明：默认"白小月"、"白小娜"，支持通过 `sherpa_models/KWS/keywords.txt` 自定义
- 更新许可证为 MIT 开源
- 相关提交：`c05943b` `796141b`

---

## 📊 版本统计

| 指标 | 数值 |
|:-----|:-----|
| 本版本提交数 | 15 |
| 新增功能 | 3 |
| 修复问题 | 5 |
| 文档更新 | 2 |
| 涉及模块 | UI、AI、Voice、Entitys |

---

## 🔧 技术细节

### 依赖版本
| 组件 | 版本 |
|:-----|:-----|
| .NET SDK | 10.0 |
| Avalonia | 12.0.1 |
| FluentUI | 3.0.0-preview1 |
| Markdig | 1.1.2 |
| Microsoft.Extensions.DI | 11.0.0-preview.3 |
| Serilog | 4.3.2-dev |
| Sherpa-ONNX | 本地集成 |

### 变更文件范围
- `Src/Netor.Madorin.UI/Controls/MarkdownRenderer.cs` — Markdown 渲染引擎重构
- `Src/Netor.Madorin.UI/Views/MainWindow.axaml.cs` — 新会话切换修复
- `Src/Netor.Madorin.AI/AiChatService.cs` — 部分响应保存
- `Src/Netor.Madorin.AI/Providers/ChatHistoryDataProvider.cs` — 新增 SavePartialResponseAsync
- `Src/Netor.Madorin.Voice/TextToSpeechService.cs` — 欢迎语自定义集成
- `Src/Netor.Madorin.Entitys/Services/SystemSettingsService.cs` — 新增设置项
- `README.md` — 全面改版

---

## ⬆️ 升级说明

1. **从 v1.1.0 升级：** 直接替换文件即可，数据库自动迁移（新增 Tts.WelcomeGreeting 设置项）
2. **首次安装：** 解压到任意目录，运行 `Madorin.exe` 即可
3. **自定义欢迎语：** 进入「系统设置 → 语音合成 → 唤醒欢迎语」修改，重启后生效
4. **自定义唤醒词：** 编辑 `sherpa_models/KWS/keywords.txt`，重启后生效

---

## 🔮 下一版本预告

- 对话导出（Markdown / PDF）
- 插件市场与在线安装
- 多轮对话上下文压缩优化
- 更多 MCP 工具集成

---

> **Netor.Madorin** — 你的私人 AI 助手，不联网、不收费、不套路。🦞
