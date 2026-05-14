# 多模态能力规划文档

## 当前已完成的功能

### 图片处理
- **附件支持**：支持通过文件选择器或拖拽方式添加图片附件，图片文件会被复制到资源目录。
- **发送给模型**：图片附件会被转换为DataContent对象，包含二进制数据和MIME类型，发送给AI模型进行识别。
- **保存机制**：图片资源通过ChatMessageAssetEntity保存到数据库，包括路径、哈希、大小等元数据。
- **显示渲染**：使用MarkdownRenderer控件支持显示图片，支持本地路径、HTTP URL和数据URI格式。
- **历史恢复**：聊天历史加载时，会重建包含图片的DataContent，确保图片在会话恢复时正确显示。

### 视频和音频处理
- **附件支持**：支持视频和音频文件的附件添加，作为文本链接或卡片显示。
- **卡片显示**：使用ResourceCardPanel控件显示非图片资源（视频/音频/文件），点击可使用系统默认程序打开。
- **资产索引**：通过ChatMessageAssetEntity索引所有资源，包括视频和音频的文件信息。

### 模型能力配置
- **能力枚举**：定义了InputCapabilities（Text/Image/Audio/Video/File）、OutputCapabilities（Text/Image/Audio/Video）和InteractionCapabilities。
- **设置界面**：ModelSettingsPage支持配置模型的多模态输入/输出能力。
- **插件集成**：PluginModelCapabilityService提供受控的模型访问，目前主要处理文本消息。

### 基础设施
- **数据结构**：ChatMessageAssetEntity用于资源索引，DataContent用于二进制内容传输。
- **WebSocket通信**：WebSocketInputChannel将附件传递给AI引擎。
- **AI引擎接口**：IAiChatEngine定义了支持附件的SendMessageAsync方法。

## 下一步规划（按工作量排序）

### 小工作量任务（优先级：高）
1. **完善AI返回图片显示**
   - 问题：当前AI生成的图片可能不会自动插入到聊天气泡中。
   - 方案：修改UiChatOutputChannel和ChatHistoryDataProvider，确保生成的图片DataContent自动转换为Markdown图片链接或内联渲染。
   - 影响：改进用户体验，使生成的图片无缝显示。

### 中等工作量任务（优先级：中）
2. **实现视频作为附件发送给模型**
   - 问题：当前视频附件仅作为文本链接发送，不支持作为DataContent发送给模型进行识别。
   - 方案：修改AiChatHostedService.cs中的SendMessageAsync方法，为视频和音频文件创建DataContent对象（类似图片处理），而不是仅文本链接。
   - 影响：启用视频/音频内容的AI识别功能。

3. **完善视频保存功能**
   - 问题：AI生成的视频资源保存机制不完整。
   - 方案：扩展SaveGeneratedAssetsAsync方法，支持保存视频DataContent到ChatMessageAssetEntity，并确保在UI中正确显示为卡片。
   - 影响：完整支持AI生成视频的保存和显示。

### 大工作量任务（优先级：低）
4. **接通文生图接口**
   - 问题：当前不支持文本生成图片的功能。
   - 方案：集成文生图API（如DALL-E、Stable Diffusion等），在PluginModelCapabilityService中添加生成图片的调用逻辑。
   - 影响：启用文本到图片的生成能力。

5. **完善视频生成API**
   - 问题：AI生成视频的功能未实现。
   - 方案：集成视频生成API，扩展OutputCapabilities支持视频生成，修改相关服务处理视频DataContent输出。
   - 影响：支持AI生成视频内容。

### 说明
- **视频和音频播放**：暂时不实现内部播放器，使用外部软件播放即可。
- **优先级排序**：基于工作量从小到大排序，小工作量任务优先处理。
- **依赖关系**：图片显示完善后，可为视频附件发送提供参考；文生图和视频生成API需要外部API集成，可能涉及更多配置和错误处理。

## 实施建议
1. 从完善AI返回图片显示开始，验证基础渲染逻辑。
2. 逐步扩展到视频附件发送，复用图片处理的DataContent模式。
3. 最后处理生成API，重点关注API集成和资源管理。

此规划基于当前代码审计结果，将指导后续多模态功能的开发。