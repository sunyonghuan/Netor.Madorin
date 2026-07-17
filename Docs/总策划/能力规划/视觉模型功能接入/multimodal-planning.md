# 多模态能力完成归档

> 完成时间：2026年6月2日
> 归档目录：`Docs/已完成功能规划/视觉模型功能接入/`

## 已完成范围

### 图片处理

- **附件支持**：支持通过文件选择器或拖拽方式添加图片附件，图片文件会被复制到资源目录。
- **发送给模型**：图片附件会被转换为 `DataContent` 对象，包含二进制数据和 MIME 类型，发送给 AI 模型进行识别。
- **保存机制**：图片资源通过 `ChatMessageAssetEntity` 保存到数据库，包括路径、哈希、大小等元数据。
- **显示渲染**：`MarkdownRenderer` 支持显示本地 `file://`、Windows 本地路径、HTTP URL 和数据 URI 图片。
- **历史恢复**：聊天历史加载时可恢复用户图片、AI 返回图片和生成图片预览。

### 图片与视频生成

- **显式入口**：聊天输入区已支持 `对话`、`图片`、`视频` 三种提交模式。
- **能力校验**：图片生成校验 `OutputCapabilities.Image`，视频生成校验 `OutputCapabilities.Video`。
- **厂商驱动**：OpenAI 驱动已接入图片/视频生成端点；Aliyun/DashScope 驱动已接入图片生成与 Wan 视频任务协议。
- **真实验证**：Aliyun 图片生成已通过真实配置验证，生成图片可保存并在历史记录中恢复。
- **上下文补全**：视觉生成提示词会合并当前智能体、最近会话和历史生成资源上下文，支持基于上一轮结果继续修改。

### 资源展示

- **图片预览**：用户图片、AI 返回图片、生成图片均可在聊天气泡中内联预览。
- **资源卡片**：视频、音频、压缩包、文档和普通文件通过 `ResourceCardPanel` 统一展示。
- **右键菜单**：图片/视频等资源支持复制路径、复制 Markdown、打开文件、打开目录、另存为、重新加载等操作。
- **本地链接**：点击本地文件链接时使用系统默认程序打开，文件不存在时显示明确提示。

### 运行与兼容

- **过程卡片**：图片/视频生成过程卡显示在用户气泡下方，完成时展示生成结果摘要。
- **思考过程**：视觉生成端点通常不返回聊天流式 reasoning，因此不强制展示空思考内容。
- **AOT 修复**：修复 Native AOT 下 MEAI 工具集合参数元数据缺失导致的新建会话异常。
- **构建验证**：UI 项目构建验证通过，AOT 发布链路已完成过成功验证。

## 暂缓事项

- 暂不实现内置视频播放器，生成视频点击后由系统默认播放器打开。
- 暂不实现内置音频播放器。
- 暂不做自然语言意图识别，图片/视频生成继续依赖显式提交模式。
- 暂不把视频历史内容回灌给 AI 上下文。
- Aliyun 视频生成需要在数据库配置具体 Wan 视频模型后做端到端验收。

## 关键涉及文件

- `Src/Netor.Cortana.AI/AiChatHostedService.cs`
- `Src/Netor.Cortana.AI/Drivers/AliyunProviderDriver.cs`
- `Src/Netor.Cortana.AI/Drivers/OpenAiProviderDriver.cs`
- `Src/Netor.Cortana.AI/Drivers/ImageGenerationModels.cs`
- `Src/Netor.Cortana.UI/Controls/Common/MarkdownRenderer.cs`
- `Src/Netor.Cortana.UI/Controls/Shared/ResourceCardPanel.cs`
- `Src/Netor.Cortana.UI/Controls/Shared/ResourceContextMenuFactory.cs`
- `Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml`
- `Src/Netor.Cortana.UI/ViewModels/ExpertMode/ChatInputVm.cs`
