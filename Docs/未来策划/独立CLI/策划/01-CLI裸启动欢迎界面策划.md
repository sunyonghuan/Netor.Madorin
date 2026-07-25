# CLI 裸启动欢迎界面策划 : 0%

> 文档状态：待评审
>
> 策划日期：2026-07-23
>
> 适用项目：`Madorin.AI.Runtime.Cli`

## 1. 目标

为用户直接执行 `madorin` 或双击 `madorin.exe` 的裸启动场景提供一次性的欢迎信息和简要状态摘要，让用户在输入第一条消息前明确知道当前 Runtime、Agent、Provider、模型、工作区和会话状态。

本策划只负责欢迎信息，不包含 Codex 风格输入框、多行编辑器、快捷键、历史选择或斜杠命令补全。这些属于独立的 REPL 输入体验策划。

## 2. 当前实现结论

- 当前没有正式欢迎界面。
- [`CliApplication.NormalizeArgs`](../../../../Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs) 会把无参数启动转换成 `run`。
- 配置和本地 Runtime 初始化完成后，[`RunReplAsync`](../../../../Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs) 直接输出 `madorin [Provider/Model] > ` 提示符。
- 首次配置时显示的 `Madorin standalone configuration` 只是配置向导标题，不属于欢迎界面。
- 当前 CLI 仅依赖 `System.CommandLine`，没有富终端 UI 依赖；欢迎界面应保持轻量并兼容 Native AOT。

## 3. 严格触发规则

欢迎界面只能由进程收到的原始参数是否为空决定，不能根据参数规范化后的 `run` 命令决定。

| 启动方式 | 是否显示欢迎界面 | 说明 |
| --- | --- | --- |
| `madorin` | 是 | 原始参数为空的裸启动 |
| 双击 `madorin.exe` | 是 | 与无参数启动保持一致 |
| `madorin run` | 否 | 用户显式执行子命令，即使随后进入 REPL |
| `madorin run --session <id>` | 否 | 显式恢复会话 |
| `madorin run --input "..."` | 否 | 单次非交互执行 |
| `madorin version`、`doctor`、`config` 等 | 否 | 普通命令调用 |
| 任意 `--json` / JSONL 输出 | 否 | 不得污染机器可读输出 |

实现时应在调用 `NormalizeArgs` 前保存 `args.Length == 0`，并将该状态显式传递到 `run` 处理流程。禁止在 `RunReplAsync` 内通过命令名称反推是否属于裸启动，因为无参数启动和显式 `madorin run` 在规范化后已经无法区分。

## 4. 展示时机

欢迎界面必须满足以下顺序：

1. 解析并规范化工作区、配置目录和数据目录。
2. 配置不存在时先完成首次配置向导。
3. 校验配置并解析最终 Agent、Provider、模型和运行模式。
4. 成功创建本地 Runtime。
5. 仅当原始参数为空时，输出一次欢迎界面。
6. 进入 REPL 并显示输入提示符。

Runtime 创建失败、配置无效或选择项不存在时不显示欢迎界面，只输出对应错误。欢迎界面中的 `ready` 只表示本地 Runtime 已成功创建，不代表 Provider 网络连通性已经验证。

## 5. 推荐界面

```text
Madorin AI Runtime 0.1.0
Local runtime ready

Mode       Expert
Agent      default
Provider   openai
Model      gpt-5
Workspace  E:\Projects\MyApp
Session    New

Enter a message to begin. /exit to quit.

madorin [openai/gpt-5] >
```

### 5.1 字段规则

- `Version`：复用 `version` 命令使用的程序集版本来源，避免两套版本计算逻辑。
- `Mode`：显示最终解析后的运行模式；当前只允许 `Expert`。
- `Agent`：优先显示 Agent 的 `Name`，为空时回退到稳定 `Id`。
- `Provider`：显示最终生效的 Provider 名称，不显示 BaseURL 或认证信息。
- `Model`：显示最终生效的模型 ID。
- `Workspace`：显示已规范化的绝对工作区路径。
- `Session`：裸启动时显示 `New`；不要在第一条消息创建会话前伪造 sessionId。

### 5.2 不展示的内容

- API Key、认证头和其他凭据。
- 配置文件、数据目录和日志目录路径。
- Runtime 实例 ID、工作区哈希和内部锁信息。
- Provider 在线、健康或已连接等未经探测的状态。
- 动态内存、Token、活动 Run 和缓冲区指标；这些应由后续 `/status` 提供。
- 尚未实现的 `/help` 等命令提示。

## 6. 视觉与终端约束

- 不使用大型 ASCII Logo，避免挤占终端首屏。
- 不使用固定宽度边框，长工作区路径应允许终端自然换行。
- 第一版优先使用纯文本，不新增第三方终端 UI 包。
- 后续若增加 ANSI 颜色，只允许强化品牌、`ready` 状态和弱化标签。
- ANSI 输出必须服从 `--no-color`，并在不支持颜色或机器输出场景完全关闭。
- 欢迎界面只输出一次，不能在每轮输入或每次 Provider 响应后重复显示。

## 7. 实现计划 : 0%

### Step 1 保留裸启动上下文 : 0%

- [×] 在参数规范化前记录原始参数是否为空。
- [×] 将裸启动标记显式传递到 `CreateRun` 和 REPL 启动流程。
- [×] 保证显式 `madorin run` 不显示欢迎界面。

### Step 2 实现欢迎信息渲染 : 0%

- [×] 增加轻量的 REPL 欢迎信息渲染组件或独立方法。
- [×] 复用现有版本来源并传入解析后的 Runtime 状态快照。
- [×] 在本地 Runtime 成功启动后、首个输入提示符之前渲染一次。
- [×] 保证欢迎信息不触发 Provider 网络探测或额外持久化。

### Step 3 补齐自动化验证 : 0%

- [×] 验证裸启动在首个提示符前输出一次欢迎信息。
- [×] 验证首次配置向导完成后才输出欢迎信息。
- [×] 验证显式 `madorin run`、单次输入和其他子命令不输出欢迎信息。
- [×] 验证 JSON/JSONL 输出不被欢迎信息污染。
- [×] 验证新会话状态、字段回退和长工作区路径。
- [×] 验证输出不包含 API Key、BaseURL、数据目录或内部实例标识。

## 8. 验收标准

- 裸启动用户能够在第一条消息前看到品牌、版本和可信的当前选择摘要。
- 显式命令调用保持安静，现有脚本输出和机器可读协议不发生变化。
- 欢迎信息不会把“Runtime 已创建”错误表达为“Provider 已联网”。
- 首次配置、异常退出和显式 `run` 的行为边界清晰且有自动化测试覆盖。
- 欢迎界面与后续 REPL 输入组件保持职责分离，可以独立调整或移除。

## 9. 关联文档

- [独立 AI CLI / Runtime 命令规范](../命令规范/04-CLI命令规范.md)
- [阶段 7：CLI、Client SDK 与参考宿主](../备档/执行步骤/07-CLI-ClientSDK与参考宿主.md)
