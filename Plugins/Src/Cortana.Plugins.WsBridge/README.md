# Cortana.Plugins.WsBridge

## 1. 项目说明

通用 WebSocket 中转插件，实现 AI ↔ 插件 ↔ 外部应用的双向消息路由。
AI 到插件侧协议固定（Cortana WebSocket），插件到外部应用侧通过适配器扩展。

## 2. 设计原则

1. 三层架构：Bridge Core（通用层）→ App Adapter（适配层）→ Tool Integration（工具层）。
2. 单一职责：每个文件只做一件事，连接器只管连接，适配器只管转换，工具只管暴露。
3. DI 容器：所有服务通过 `IServiceCollection` 注册，生命周期由容器管理。
4. Native AOT 兼容：所有序列化使用 Source Generator，禁止反射。
5. 适配器编译期注册：新增应用只需新增适配器类并在 `AdapterRegistry` 注册。

## 3. 架构概览

```
外部应用 WebSocket ←→ ExternalConnector ←→ BridgeSession ←→ CortanaConnector ←→ Cortana WebSocket
                              ↑                    ↑
                        IExternalAppAdapter    SessionQueue（串行执行）
```

## 4. 代码结构

```
├── Startup.cs                          插件入口与 DI 注册
├── PluginJsonContext.cs                 AOT JSON 序列化上下文
├── ToolResult.cs                       统一返回结构
├── Models/
│   ├── BridgeEnvelope.cs               统一消息信封
│   ├── BridgeConfig.cs                 连接配置
│   ├── CortanaMessage.cs               Cortana 协议消息
│   └── BridgeSessionInfo.cs            会话摘要信息
├── Core/
│   ├── IExternalAppAdapter.cs          适配器接口
│   ├── AdapterRegistry.cs              适配器注册表
│   ├── SessionQueue.cs                 串行执行队列
│   └── BridgeSession.cs               会话管理与消息路由
├── Connectors/
│   ├── CortanaConnector.cs             Cortana 固定侧连接器
│   └── ExternalConnector.cs            外部应用连接器
├── Adapters/
│   └── GenericAdapter.cs               通用直通适配器
├── Services/
│   ├── BridgeSessionManager.cs         会话管理器
│   └── BridgeBackgroundService.cs      后台托管服务
└── Tools/
    └── BridgeTools.cs                  AI 工具暴露
```

## 5. 工具清单

| 工具名 | 说明 |
|--------|------|
| ws_bridge_connect | 建立中转连接，返回 session_id |
| ws_bridge_send | 向外部应用发送消息 |
| ws_bridge_stop | 中止当前 AI 回复 |
| ws_bridge_status | 查询会话状态 |
| ws_bridge_disconnect | 关闭连接并清理资源 |

## 6. 使用示例

```
1. 调用 ws_bridge_connect(adapter_id="generic", ws_url="ws://app:8080/ws", auth_token="")
   → 返回 session_id

2. 外部应用发送消息 → 自动转发到 Cortana → Cortana 回复流式回传外部应用

3. 调用 ws_bridge_status(session_id) 查看连接状态

4. 调用 ws_bridge_disconnect(session_id) 关闭连接
```

## 7. 扩展新适配器

1. 在 `Adapters/` 目录新建适配器类，实现 `IExternalAppAdapter`。
2. 在 `AdapterRegistry` 构造函数中注册新适配器。
3. 在 `PluginJsonContext` 中注册新增的序列化类型（如有）。

## 8. 串行执行约束

同一会话同一时刻只有一个 Cortana 请求在处理。
`SessionQueue` 通过信号量保证 send → 等待 done/error → 下一条的顺序。
新请求在队列中等待，不会并发抢占。

## 9. 当前阶段（Phase 1）

- Cortana 固定侧连接 ✓
- 标准 Envelope 消息模型 ✓
- Generic 通用适配器 ✓
- 串行执行队列 ✓
- 工具暴露 ✓

## 10. 后续规划

- Phase 2：重连机制（指数退避）、消息去重、日志观测增强。
- Phase 3：附件中转与本地缓存策略。
- Phase 4：多应用适配器与配置中心。
