# Madorin CLI 安装与使用

> 适用版本：Madorin.AI.Runtime V1，协议 1.1，SQLite Schema 14
>
> 当前发布基线：Windows x64 Native AOT。其他平台的正式安装制品在阶段 8 验收。

## 1. 安装

发布包应完整解压到独立目录，不要只复制仓库中的 Debug/Release 中间产物。以下示例使用当前用户目录，不要求管理员权限：

```powershell
$installDir = Join-Path $env:LOCALAPPDATA 'Madorin\bin'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path '.\publish\*' -Destination $installDir -Recurse -Force
```

将安装目录加入当前用户 PATH：

```powershell
$installDir = Join-Path $env:LOCALAPPDATA 'Madorin\bin'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$entries = @($userPath -split ';' | Where-Object { $_ })
if ($installDir -notin $entries) {
    [Environment]::SetEnvironmentVariable(
        'Path',
        (($entries + $installDir) -join ';'),
        'User')
}
```

关闭并重新打开终端后验证：

```powershell
madorin --help
madorin version --json
```

`version --json` 的 `success` 应为 `true`，`data.product` 应为 `Madorin.AI.Runtime`，`data.protocolVersion` 应为 `1.1`。

## 2. 无参数与双击启动

在终端直接执行 `madorin`，或在 Windows 资源管理器中双击 `madorin.exe`，都会进入同一个轻量单行 REPL。首次启动若没有个人配置，会先进入初始化向导；配置完成后进入交互会话。

双击窗口立即关闭通常表示配置、工作区或 Provider 检查失败。此时从 PowerShell 运行 `madorin` 查看完整错误和退出码。V1 不实现全屏 TUI、多行编辑器、输入队列或终端光标接管。

## 3. 用户目录与工作区

个人 CLI 文件位于当前系统用户主目录：

```text
~/.madorin/
├── config.json
├── agents/
│   └── <agentId>.json
├── memory.md
└── data/
    └── workspaces/
        └── <workspaceKey>/
```

- `config.json`、`agents/` 和全局 `memory.md` 只供直接启动的个人 CLI 使用。
- `workspaceKey` 由规范化工作区路径稳定派生，不在目录名中暴露原始路径。
- 项目记忆固定为 `<workspace>/.madorin/memory.md`。
- 宿主集成不读取个人 `config.json`，配置与选择由宿主通过 Client SDK/协议提供。
- 不要手工修改 Runtime 数据区的 `state.db`、`messages/`、`blobs/` 或运行实例文件。

用 `--workspace <path>` 显式选择工作区；`--data-dir <path>` 只在需要受控覆盖 Runtime 数据位置时使用。同一规范化工作区只允许一个写实例，不同工作区可以并行。

## 4. 首次配置

无参数首次启动会自动进入向导，也可以显式执行：

```powershell
madorin config init
madorin config validate
madorin config show
```

向导配置 Provider 协议、Base URL、API Key、模型和默认 Agent。API Key 通过无回显输入写入当前用户的 `config.json`；CLI 不提供 `--api-key <value>`，避免密钥进入命令历史和进程参数。后续修改使用 `madorin config edit`，展示和诊断输出只显示脱敏值。

创建或修改 Agent：

```powershell
madorin agent create
madorin agent list
madorin agent edit <agentId>
madorin agent delete <agentId>
```

`agent create/edit` 在缺少参数时使用交互选择。自动化场景可以传 `--name`、`--description`、`--system-prompt`、`--provider`、`--model` 和 `--temperature`，但 Provider 密钥仍只能通过安全输入写入配置。

## 5. 全局与项目记忆

初始化并查看两个固定记忆文件：

```powershell
madorin memory init global
madorin memory init project --workspace 'E:\Projects\demo'
madorin memory show effective --workspace 'E:\Projects\demo'
```

`effective` 按“全局后项目”的实际注入顺序展示来源；项目规则更具体。追加、编辑和清空分别使用：

```powershell
madorin memory add global
madorin memory edit project --workspace 'E:\Projects\demo'
madorin memory clear project --workspace 'E:\Projects\demo'
```

记忆只由这些用户命令或经审批的 `builtin.memory` 工具写入，不从对话、项目文件或工具结果自动提取。

## 6. 单次运行

最小单次执行：

```powershell
madorin run --workspace 'E:\Projects\demo' --input '检查当前任务并给出下一步'
```

从文件读取长文本并生成可脚本解析的结果：

```powershell
madorin run --workspace 'E:\Projects\demo' --input-file '.\request.md' --mode expert --output-format json --output-file '.\result.json' --no-stream
```

`--input` 与 `--input-file` 互斥。三种模式为 `expert`、`meeting` 和 `work`；`--session <sessionId>` 继续已有 Session。JSON/JSONL 模式下 stdout 只包含机器数据，进度和诊断写 stderr；输出文件使用同目录临时文件原子发布，失败时保留已有有效结果。

## 7. 单行 REPL

```powershell
Set-Location 'E:\Projects\demo'
madorin
```

常用斜杠命令包括 `/model`、`/provider`、`/agent`、`/memory`、`/remember`、`/compact`、`/context`、`/session`、`/new`、`/resume`、`/export`、`/history`、`/status`、`/help` 和 `/exit`。`/model`、`/provider`、`/agent` 只更新下一轮 Selection；`/mode` 只显示当前模式，不能在同一 Session 内修改。

中断行为固定为两阶段：

1. Run 执行中第一次 Ctrl+C 只取消当前 Run。
2. CLI 等待该 Run 收敛为唯一 `Cancelled` 终态，然后返回提示符。
3. 用户可以在同一 Session 中修改要求并继续输入。
4. 取消收敛期间再次 Ctrl+C，或空闲时 Ctrl+C，退出 CLI 并返回 130。

EOF 也会结束 REPL。Run 执行期间 CLI 不并发读取下一条输入；多行内容通过 `--input-file` 提交。

## 8. 常用诊断

```powershell
madorin version --json
madorin doctor --workspace 'E:\Projects\demo'
madorin doctor --workspace 'E:\Projects\demo' --fix
```

`doctor` 按 Runtime、个人配置、记忆、工作区、数据、Provider、传输、日志和 Blob 的固定顺序检查。`doctor --fix` 只修复已识别的可恢复数据库损坏，先展示计划并要求确认；JSON 模式实际修复必须提供 `--yes`。
