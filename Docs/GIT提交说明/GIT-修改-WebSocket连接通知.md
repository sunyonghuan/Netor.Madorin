# Git 提交记录 - WebSocket 连接通知

## 提交信息

**提交日期**: 2026-04-20  
**提交类型**: Feature  
**影响范围**: WebSocket 客户端连接状态通知、隐藏主窗口时的浮动气泡提醒

---

## 修改概述

本次提交为 Avalonia UI 增加了 WebSocket 客户端连接/断开通知能力。

当外部客户端连接到内置 WebSocket 服务时：
- 主窗口可见时，在聊天消息区追加一条系统通知。
- 主窗口隐藏时，除聊天消息外，额外弹出浮动气泡提示连接状态。

当客户端断开连接时，行为与连接时保持一致，提示文案改为断开状态。

---

## 修改文件清单

### 1. Src/Netor.Cortana.Entitys/Events.cs

**修改内容**:
- 新增 `OnWebSocketClientConnectionChanged` 事件定义。
- 新增 `WebSocketClientConnectionChangedEvent` 事件类型。
- 新增 `WebSocketClientConnectionChangedArgs` 参数类型，统一封装客户端 ID、远端 IP、端口与连接状态。

**影响功能**: 为网络层和 UI 层之间提供统一的连接状态事件通道。

### 2. Src/Netor.Cortana.Networks/WebSocketServerService.cs

**修改内容**:
- 在客户端接入时记录远端 IP 和端口。
- 在连接成功后发布连接事件。
- 在接收循环退出后发布断开事件。
- 日志中补充远端地址信息，便于排查连接来源。

**影响功能**: WebSocket 服务能够主动向上层广播客户端连接状态变化。

### 3. Src/Netor.Cortana.AvaloniaUI/App.axaml.cs

**修改内容**:
- 应用初始化阶段订阅全局 WebSocket 连接状态事件。
- 主窗口存在时，将连接/断开消息写入聊天气泡。
- 主窗口隐藏时，调用浮动气泡窗口显示短时系统通知。

**影响功能**: App 层统一协调主窗口与浮动气泡窗口的通知呈现逻辑。

### 4. Src/Netor.Cortana.AvaloniaUI/Views/BubbleWindow.axaml.cs

**修改内容**:
- 新增 `ShowSystemNotification` 方法。
- 为系统通知引入单独的自动消失时长。
- 根据连接或断开状态切换状态点颜色。

**影响功能**: 主窗口隐藏时，浮动气泡可以承载简洁的连接状态提示。

---

## 行为变化

1. WebSocket 客户端连接后，聊天区会新增一条系统消息，说明客户端已接入。
2. WebSocket 客户端断开后，聊天区会新增一条系统消息，说明客户端已断开。
3. 若主窗口隐藏，浮动气泡会短暂显示连接或断开提示，不要求用户手动展开主界面。
4. 通知文案中包含远端地址信息，方便确认具体客户端来源。

---

## 测试建议

- [ ] 启动 WebSocket 服务后使用外部客户端连接，确认聊天区出现连接通知。
- [ ] 断开客户端连接，确认聊天区出现断开通知。
- [ ] 隐藏主窗口后重复连接/断开，确认浮动气泡提示能够正常显示并自动消失。
- [ ] 检查多客户端场景下远端地址是否记录正确。

---

## 提交说明

建议提交信息：

feat(websocket): add client connection notifications for UI

建议提交描述：

- publish websocket client connection change events
- append system notifications to chat view
- show bubble notifications when main window is hidden
- include remote endpoint information in connection logs