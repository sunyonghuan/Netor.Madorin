---
title: Cortana Process 通道协议规范
version: 2
---

# Process 通道协议规范

本文档**语言中立**。任何能产出可执行文件（EXE）并处理 stdin/stdout 的语言都可实现。

## 1. 传输层

- 编码：**UTF-8**
- 帧格式：**NDJSON**（每行一个 JSON 对象，以 `\n` 分隔，不能内嵌换行）
- 方向：宿主 → 插件用 **stdin**；插件 → 宿主用 **stdout**
- stderr：插件的日志输出；宿主会以 WARN 级别转发，不参与协议
- 超时：每次请求 **30 秒**未收到响应，宿主会 kill 进程
- 退出：stdin 关闭后插件应自行退出

## 2. 消息格式

**请求**（宿主 → 插件）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `method` | string | ✅ | `get_info` / `init` / `invoke` / `destroy` |
| `toolName` | string | invoke 时 ✅ | 工具名 |
| `args` | string | init/invoke 时 ✅ | JSON **字符串**（不是对象），内含真实参数 |

**响应**（插件 → 宿主）：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `success` | bool | ✅ | 是否成功 |
| `data` | string | 成功时 | JSON 字符串或纯文本 |
| `error` | string | 失败时 | 错误消息 |

## 3. 方法与生命周期

调用顺序：`get_info` → `init` → 多次 `invoke` → `destroy`

### 3.1 `get_info`

宿主启动进程后立刻询问，获取插件元数据和工具清单。

请求：`{"method":"get_info"}`

响应 `data` 是 JSON 字符串，反序列化后结构：

```json
{
  "id": "my_plugin",
  "name": "显示名",
  "version": "1.0.0",
  "description": "可选",
  "instructions": "可选，告诉 AI 何时用这些工具",
  "tags": ["可选"],
  "tools": [
    {
      "name": "tool_name",
      "description": "工具描述",
      "parameters": [
        { "name": "arg1", "type": "string", "description": "可选", "required": true }
      ]
    }
  ]
}
```

`parameters[].type` 取值：`string` / `number` / `integer` / `boolean` / `array` / `object`。

### 3.2 `init`

宿主传入运行时配置。`args` 是 JSON **字符串**，反序列化后：

```json
{
  "dataDirectory": "<插件私有数据目录，插件可读写>",
  "workspaceDirectory": "<用户工作区根>",
  "pluginDirectory": "<本插件安装目录，只读>",
  "wsPort": 12345
}
```

响应：`{"success":true}` 或 `{"success":false,"error":"..."}`。

### 3.3 `invoke`

请求：`{"method":"invoke","toolName":"xxx","args":"<工具参数 JSON 字符串>"}`

`args` 反序列化后的对象字段 = `get_info` 里声明的参数。

响应 `data` 是返回值（通常是 JSON 字符串或纯文本）。

### 3.4 `destroy`

宿主关闭前的清理信号。收到后释放资源并尽快返回 `{"success":true}`。之后 stdin 关闭，进程应自行退出。

## 4. plugin.json（Process 通道）

```json
{
  "id": "my_plugin",
  "name": "我的插件",
  "version": "1.0.0",
  "runtime": "process",
  "command": "my-plugin.exe"
}
```

- `runtime` 必须为 `"process"`
- `command` 是相对插件目录的可执行文件名
- 其他字段（description/tags/instructions）可留空，由 `get_info` 覆盖

## 5. 实现要点清单（非 C# 语言）

- [ ] 主循环：逐行读 stdin → 解析 JSON → 分派 method → 逐行写 stdout（`\n` 结尾，**立即 flush**）
- [ ] **不要**向 stdout 写日志、print 调试、横幅；stdout 只能是协议消息。日志走 stderr
- [ ] 所有字符串按 UTF-8 编码/解码
- [ ] 单个响应必须单行，内嵌换行替换成 `\n` 或从字段里删掉
- [ ] 在 `init` 阶段记下 `dataDirectory` 作为可写位置，**不要**写入 `pluginDirectory`
- [ ] 捕获异常，封装成 `{"success":false,"error":"..."}` 继续循环，不要让进程崩溃
- [ ] 检测到 stdin EOF 时优雅退出

## 6. 参考实现

- C# Process 插件请直接使用 `scripts/create-process-plugin.ps1` 脚手架，不再提供手写协议模板目录
