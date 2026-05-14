# WebSocket 中转插件项目方案

## 1. 目标

建设一个通用中转插件，实现 AI <-> 插件 <-> 外部应用 的双向消息路由。
AI 到插件侧协议固定，插件到外部应用侧通过适配器扩展。

## 2. 范围与非范围

范围包括连接外部 WebSocket、消息收发、协议转换、路由分发、调用聊天工具动作。
范围包括按应用扩展适配器，复用统一连接管理与消息总线。
非范围包括把所有应用协议硬编码进核心层，或在核心层写业务判断。

## 3. Native AOT 风险评估（先行）

该方案可用 C# 实现并保持 Native AOT 友好。
推荐使用 `ClientWebSocket`、`System.Text.Json` 与源码生成序列化上下文。
应避免动态反射映射、动态脚本执行、运行时类型加载。
结论是可实施，前提是消息协议采用强类型信封模型，适配器通过编译期注册。

## 4. 总体架构

采用三层架构。

1. Bridge Core（通用层）
负责连接管理、会话管理、消息信封、路由分发、重试与限流。

2. App Adapter（应用适配层）
每个外部应用一个适配器，负责协议转换、地址构造、鉴权头、动作映射。

3. Tool Integration（聊天工具集成层）
负责把标准化消息映射为聊天工具动作调用，并把动作结果回传外部应用。

核心原则是 AI 到插件接口稳定不变，应用差异全部下沉到适配器层。

## 5. 统一消息模型

建议定义统一消息信封 `BridgeMessageEnvelope`。
关键字段包括 `message_id`、`session_id`、`source`、`target`、`event_type`、`timestamp`、`payload`。
其中 payload 使用强类型 Union 方案，不使用动态对象反射分发。

路由规则如下。

1. 外部应用消息进入后先归一化为 Envelope。
2. Router 根据 `event_type` 与 `target` 决定调用哪类聊天工具动作。
3. 动作执行结果再封装为 Envelope 回发到外部应用。

这样可保证不同应用仅改“转换器”，不改核心路由。

## 6. 固定侧协议（Cortana WebSocket）

固定侧连接地址为 ws://host:52841/ws/（端口可配置）。
编码为 UTF-8 JSON 文本帧，连接成功后服务端先下发 connected 与 clientId。

客户端到 Cortana 的核心消息只有两类。
1. send：发送用户消息，可附带 attachments。
2. stop：中止当前回复。

服务端到客户端的关键消息包括。
1. token：流式文本片段。
2. done：本轮完成。
3. error：错误或 cancelled。
4. stt_*、tts_*、chat_completed、wakeword_detected：系统事件广播。

## 7. 中转站核心设计

### 7.1 双端连接器

1. LeftConnector（固定）
连接 Cortana WebSocket，负责 send/stop 与 token/done/error 接收。

2. RightConnector（可插拔）
连接外部应用 WebSocket，每个应用由独立适配器提供连接参数和协议模型。

### 7.2 路由主循环

1. RightConnector 收到外部消息，交给 Adapter 做 InboundConvert。
2. 转换后得到标准 Envelope，交给 Router。
3. Router 调用 LeftConnector 发到 Cortana。
4. Cortana 回包后由 Adapter 做 OutboundConvert，再回发外部应用。

### 7.3 串行执行约束

根据技能约束，同一时刻只有一个 AI 请求在处理。
Bridge Core 必须实现会话级请求队列，避免并发 send 抢占。
新请求进入队列后等待 done 或 error(cancelled) 才可继续。

## 8. 适配器插件机制（通用版关键）

定义统一接口 IExternalAppAdapter。

1. AdapterId：应用标识。
2. BuildConnectOptions：构造右侧连接参数。
3. InboundConvert：外部协议转标准 Envelope。
4. OutboundConvert：标准 Envelope 转外部协议。
5. ValidateConfig：校验应用配置。

Bridge Core 不感知应用业务字段，所有字段映射留在适配器。
新增应用时只新增一个适配器项目，不改核心路由。

## 9. 附件与文件路径策略

技能约束要求 attachments.path 必须是 Cortana 进程机器上的本地路径。
因此远端应用上传附件时，中转站需要先把文件落到 Cortana 所在机器临时目录。

推荐流程。
1. 外部应用发送附件元数据或文件引用。
2. 中转站通过安全通道拉取文件到本地缓存目录。
3. 生成本地绝对路径写入 attachments.path 后再发 send。
4. 对话结束后按策略清理临时文件。

## 10. AOT 与序列化规范

所有消息类型必须为强类型 record/class。
所有序列化与反序列化必须走 JsonSerializerContext Source Generator。
项目开启 JsonSerializerIsReflectionEnabledByDefault=false，编译期拦截反射序列化遗漏。

禁止项。
1. 匿名类型序列化。
2. dynamic/ExpandoObject 反序列化路由。
3. 运行时按字符串反射创建消息类型。

## 11. 安全、可靠性与观测

1. 安全
外部连接鉴权采用 token 或签名头。
附件缓存目录限定白名单，文件名做规范化处理，防路径穿越。

2. 可靠性
右侧连接断开时自动重连（指数退避）。
消息使用 message_id 去重，避免重连后重复投递。

3. 观测
日志字段至少包含 trace_id、session_id、adapter_id、direction、event_type、latency_ms。
度量至少包含连接状态、队列长度、消息吞吐、错误率。

## 12. 建议项目结构

1. Src/Cortana.Plugins.WsBridge
核心插件入口、工具暴露、配置加载。

2. Src/Cortana.Plugins.WsBridge.Core
Envelope、Router、SessionQueue、重试与限流。

3. Src/Cortana.Plugins.WsBridge.Cortana
固定侧 CortanaWsClient 与协议模型。

4. Src/Cortana.Plugins.WsBridge.Adapters.AbcApp
某个应用的适配器实现。

5. Src/Cortana.Plugins.WsBridge.Adapters.XyzApp
另一个应用的适配器实现。

## 13. 对外工具建议

1. ws_bridge_connect
建立中转连接并返回 bridge_session_id。

2. ws_bridge_send
向外部应用或 Cortana 发送标准消息。

3. ws_bridge_stop
中止当前 AI 回复（转发 stop）。

4. ws_bridge_subscribe_events
订阅 token、done、error、tts_*、stt_* 等事件。

5. ws_bridge_disconnect
关闭连接并清理会话资源。

## 14. 分阶段实施

第一阶段：实现 Cortana 固定侧连接、标准 Envelope 与单适配器打通。
第二阶段：加入会话队列、重连、观测与错误码体系。
第三阶段：支持附件中转与本地缓存策略。
第四阶段：扩展多应用适配器与配置中心。




