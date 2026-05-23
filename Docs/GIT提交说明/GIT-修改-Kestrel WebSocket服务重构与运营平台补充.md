# GIT-修改-Kestrel WebSocket服务重构与运营平台补充

## 修改范围

本次提交包含两部分主要变更：

1. `Netor.Madorin.Networks` WebSocket 服务重构。
2. 运营平台 Web/Admin/实体模型与插件项目配置补充。

## 一、Kestrel WebSocket 服务重构

### 目标

解决原 WebSocket 服务在错误、重启、客户端异常断开、慢客户端广播等场景下的稳定性问题，降低服务卡死和应用崩溃风险。

### 主要改动

- 使用 Kestrel 替换原 `HttpListener` + 裸线程模式。
- 将原 `WebSocketServerService` 重命名并重构为 `WebSocketInteractionServerService`，更准确表达其交互通道职责。
- 新增并接入公共 WebSocket 基础设施：
  - `WebSockets/Hosting/KestrelWebSocketHost.cs`
  - `WebSockets/Connections/WebSocketConnection.cs`
  - `WebSockets/Connections/WebSocketConnectionManager.cs`
  - `WebSockets/Connections/WebSocketSendQueue.cs`
  - `WebSockets/Connections/WebSocketHeartbeatLoop.cs`
  - `WebSockets/Connections/WebSocketClosePolicy.cs`
- 新增 `IConversationFeedBroadcaster`，将 conversation-feed 广播能力从具体服务解耦。
- `WebSocketInteractionServerService` 已接入公共 Host、连接管理器、发送队列与心跳循环。
- `WebSocketFeedServerService` 已接入公共 Host、连接管理器、发送队列与心跳循环。
- 保持旧端口、旧路径、旧消息类型兼容：
  - `/ws/`
  - `/internal/conversation-feed/`
  - `ModelCapabilityProtocol.Path`
- 保留迁移期 legacy 入口，避免破坏既有插件和已连接服务。
- 广播改为客户端级隔离，慢客户端不再阻塞整体广播。
- 发送路径改为队列入队，避免多线程并发写同一 WebSocket。
- 增加服务运行状态与重启状态记录。
- 清理旧内联心跳逻辑、旧发送锁残留与重复关闭路径。
- 加固 `WebSocketSendQueue` Dispose 与故障自清理路径，避免重复释放和 worker 自等待。

### 文档

- 新增执行计划文档：
  - `Docs/执行计划(Kestrel WebSocket 服务重构).md`
- 文档记录了重构目标、兼容策略、AOT 安全讨论、迁移步骤和验证结果。

## 二、运营平台与插件补充

### 平台侧

- 补充个人用户前台 Web 相关控制器、模型和页面。
- 补充市场、下载、订单、用户中心等页面结构。
- 补充资产管理页面与创建页面。
- 更新平台实体、数据库初始化、设计时 DbContext、迁移与配置。
- 调整 Web/Admin/API appsettings 配置。

### 插件侧

- 调整多个插件项目文件与启动注册逻辑。
- 覆盖 ApplicationLauncher、Bt、GoogleSearch、Memory、Office、Reminder、ScriptRunner、WindowManagement、WsBridge 等插件。

## 三、验证结果

已执行以下验证：

```text
dotnet build Netor.Madorin.slnx
```

结果：通过。

```text
dotnet run --project artifacts/KestrelWsProbe/KestrelWsProbe.csproj
```

结果：通过，输出 `Kestrel WebSocket probe passed.`。

```text
dotnet test Tests/Netor.Madorin.Networks.Tests/Netor.Madorin.Networks.Tests.csproj --no-build
```

结果：通过，`9` 个测试全部成功。

## 四、兼容性说明

- 不更改既有 WebSocket 外部协议入口。
- 不删除 legacy conversation-feed / model-capability 入口。
- Feed 目标职责为会话事实广播，但迁移期继续保留旧协议兼容。
- Kestrel 使用限定在轻量 WebSocket、显式路径、源生成 JSON、显式 DI 范围内，避免引入 MVC/Razor/动态扫描等 AOT 风险。
