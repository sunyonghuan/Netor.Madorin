# system.notice 临时系统信息协议方案

## 背景

主界面聊天对话框需要显示一类临时系统信息，例如：

- 模型审核过程；
- 工具调用过程；
- 插件或第三方软件通过网络发送的提示；
- 外部应用状态同步、告警或操作反馈。

这类信息只用于当前界面临时展示，不属于用户输入或 AI 回复，不需要写入数据库，也不需要进入历史记录。

## 设计目标

1. **兼容现有协议**：不修改 `send`、`stop`、`token`、`done`、`error`、`connected` 的语义。
2. **最小改动**：继续使用现有 WebSocket 消息结构，通过 `type` 区分新增消息。
3. **支持扩展字段**：现有 JSON 协议本身允许额外字段，新增字段不影响旧客户端。
4. **不入库**：系统提示只进入 UI 临时消息列表，不写入 `ChatMessages`。
5. **可折叠展示**：长内容默认折叠，避免刷屏。

## 协议名称

新增消息类型：

```text
system.notice
```

命名原因：

- 比 `notice` 更明确，表示系统/流程/第三方提示；
- 避免和普通聊天消息混淆；
- 不和 LLM 的 `system role` 绑定，仅表示 UI 层系统提示；
- 通过 `type` 与既有协议区分，保持扩展方式一致。

## 消息格式

### 第三方 / 插件 / 客户端 -> Cortana

```json
{
  "type": "system.notice",
  "data": "正在同步第三方软件状态...",
  "title": "外部提示",
  "level": "info",
  "source": "Photoshop"
}
```

字段说明：

| 字段 | 必填 | 说明 |
|---|---:|---|
| `type` | 是 | 固定为 `system.notice` |
| `data` | 是 | 详细提示内容 |
| `title` | 否 | 提示标题，例如“外部提示”“工具调用”“模型审核” |
| `level` | 否 | 提示等级，建议值：`info`、`success`、`warning`、`error`、`progress` |
| `source` | 否 | 来源名称，例如插件名、第三方软件名、客户端标识 |

### Cortana -> WebSocket 客户端

如果后续需要把宿主内部系统提示广播给网络客户端，也复用同样结构：

```json
{
  "type": "system.notice",
  "data": "正在调用工具 google_search_web",
  "title": "工具调用",
  "level": "progress",
  "source": "GoogleSearch"
}
```

## 兼容策略

- 老客户端继续发送 `send` / `stop`，行为不变。
- 老客户端接收未知 `system.notice` 时可忽略。
- 新客户端只需要根据 `type == "system.notice"` 判断为系统提示。
- `data` 仍是主体内容，不新增特殊 `notice` 对象字段。
- `title`、`level`、`source` 都是扩展字段，缺省时 UI 使用默认值。

## 事件模型

在 `Events.cs` 新增事件：

```text
Events.OnSystemNotice
```

建议参数：

```text
SystemNoticeArgs(
    Content,
    Title,
    Level,
    Source,
    CreatedAt)
```

其中：

- `Content` 来自协议 `data`；
- `Title` 来自协议 `title`，为空时使用“系统提示”；
- `Level` 来自协议 `level`，为空时使用 `info`；
- `Source` 来自协议 `source`，为空时可使用客户端 ID 或通道名；
- `CreatedAt` 在接收时生成。

## UI 展示规则

1. 主界面订阅 `Events.OnSystemNotice`。
2. 收到事件后调用 `AddSystemNotice(...)` 加入 `MessageList`。
3. 系统提示不使用用户/AI 气泡样式，使用轻量系统卡片。
4. 长内容折叠：
   - `data.Length <= 300`：直接展示；
   - `data.Length > 300`：默认展示摘要，提供“展开/收起”。
5. 新会话、清空页面、切换历史时，提示随当前 UI 消息列表清空，不从数据库恢复。

## 实现范围

第一阶段仅实现最小闭环：

- 网络输入 `system.notice`；
- 发布 `Events.OnSystemNotice`；
- 主界面临时显示；
- 长内容折叠；
- 不入库、不进历史；
- 更新 `skills/websocket-integration/SKILL.md`，把 `system.notice` 新协议补充到主程序网络输入输出使用说明中。

## 技能文档同步

`skills/websocket-integration/SKILL.md` 是指导插件、第三方程序、外部客户端使用主程序 WebSocket 输入输出协议的技能文档。实现 `system.notice` 时必须同步更新该技能，避免后续生成客户端代码或协议说明时遗漏新协议。

需要补充的内容：

- 在“客户端 → 服务端”消息类型中增加 `system.notice`；
- 说明 `data` 为系统提示详细内容；
- 说明可选扩展字段 `title`、`level`、`source`；
- 增加 `system.notice` JSON 示例；
- 在 C# 消息类型定义中增加 `Title`、`Level`、`Source` 可选属性；
- 在行为约束中说明客户端必须继续忽略未知 `type`，并且 `system.notice` 不触发 AI 对话、不写入长期历史。

第二阶段再接入内部流程：

- 模型能力调用状态；
- 工具调用开始/完成/失败；
- MCP 或插件运行状态；
- 必要时向外部 WebSocket 客户端广播 `system.notice`。
