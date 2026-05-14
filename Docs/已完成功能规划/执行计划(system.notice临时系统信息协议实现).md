# system.notice 临时系统信息协议实现 : 100%

## Step 1 新增事件模型 : 100%
- [√] 在 `Src/Netor.Cortana.Entitys/Events.cs` 新增 `Events.OnSystemNotice`。
- [√] 在 `Events.cs` 新增 `SystemNoticeEvent` 事件类型。
- [√] 在 `Events.cs` 新增 `SystemNoticeArgs` 参数类型，字段包含 `Content`、`Title`、`Level`、`Source`、`CreatedAt`。

## Step 2 扩展 WebSocket 输入协议 : 100%
- [√] 在 `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketServerService.cs` 保留现有 `type` / `data` 解析逻辑。
- [√] 在 `WebSocketServerService.HandleClientMessageAsync(...)` 解析扩展字段 `title`、`level`、`source`。
- [√] 尽量不改变 `send`、`stop`、`token`、`done`、`error`、`connected` 原有行为。
- [√] 将 `system.notice` 消息传递到输入通道。

## Step 3 发布系统提示事件 : 100%
- [√] 在 `Src/Netor.Cortana.Networks/WebSockets/Channels/WebSocketInputChannel.cs` 增加 `case "system.notice"`。
- [√] 收到 `system.notice` 后发布 `Events.OnSystemNotice`。
- [√] 确保该分支不调用 `chatEngine.SendMessageAsync(...)`。
- [√] 当 `title`、`level`、`source` 缺省时使用默认值。

## Step 4 主界面临时显示 : 100%
- [√] 在 `Src/Netor.Cortana.UI/Views/MainWindow.axaml.cs` 订阅 `Events.OnSystemNotice`。
- [√] 在收到系统提示时隐藏欢迎面板。
- [√] 调用新增 UI 方法添加临时系统提示卡片。

## Step 5 系统提示卡片与折叠 : 100%
- [√] 在 `Src/Netor.Cortana.UI/Views/Main/MainWindow.Messaging.cs` 新增 `AddSystemNotice(...)`。
- [√] 使用独立系统卡片样式，不复用用户/AI 气泡身份。
- [√] 当 `Content` 超过 300 字时默认折叠。
- [√] 提供“展开/收起”交互。
- [√] 系统提示只加入 `MessageList.Items`，不写数据库。

## Step 6 同步 WebSocket 使用技能文档 : 100%
- [√] 更新 `skills/websocket-integration/SKILL.md` 的“客户端 → 服务端”消息类型，加入 `system.notice`。
- [√] 补充 `system.notice` JSON 示例，明确 `data` 为详细内容。
- [√] 说明可选扩展字段 `title`、`level`、`source`。
- [√] 更新 C# `WsClientMessage` / `WsServerMessage` 示例类型，加入可选属性 `Title`、`Level`、`Source`。
- [√] 在行为约束中说明 `system.notice` 不触发 AI 对话、不写入长期历史，客户端仍需忽略未知 `type`。

## Step 7 验证 : 100%
- [√] 构建 `Netor.Cortana.UI` 项目。
- [√] 通过代码路径验证原有 `send` 消息仍能显示用户消息并触发 AI 回复。
- [√] 通过代码路径验证原有 `stop` 消息仍能取消。
- [√] 通过代码路径验证发送 `type=system.notice` 时仅显示系统提示，不触发 AI 对话。
- [√] 通过代码路径验证长内容折叠显示。

构建命令：`dotnet build .\Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj`

构建结果：成功。

## 修改文件清单

- `Docs/system.notice临时系统信息协议方案.md`
- `Docs/执行计划(system.notice临时系统信息协议实现).md`
- `Src/Netor.Cortana.Entitys/Events.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketServerService.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Channels/WebSocketInputChannel.cs`
- `Src/Netor.Cortana.UI/Views/MainWindow.axaml.cs`
- `Src/Netor.Cortana.UI/Views/Main/MainWindow.Messaging.cs`
- `skills/websocket-integration/SKILL.md`
