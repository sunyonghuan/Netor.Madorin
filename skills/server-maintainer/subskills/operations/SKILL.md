---
name: operations
description: '服务器维护操作子技能。用于清理、安装、服务管理与风险确认矩阵。'
user-invocable: true
---

# Server Operations

## Scope

- 清理磁盘和日志。
- 安装和维护常见服务。
- 统一维护任务风险分级。

## Rules

- 所有远程维护动作必须通过 SSH 插件工具执行：`ssh_connect`、`ssh_session_exec`、`ssh_exec_script`、`ssh_exec_async`、`ssh_service_action`、`ssh_log_tail` 等。
- 不要通过 PowerShell 工具执行 `ssh`/`scp` 或运行旧的远程维护 ps1 脚本。
- 只读操作可直接执行。
- 低风险操作执行前要复述目标服务器和目标路径。
- 中高风险操作必须二次确认。
- 默认拒绝破坏性删除和不可逆覆盖。

## Legacy Scripts

- `scripts/cleanup-disk.ps1`、`scripts/cleanup-logs.ps1`、`scripts/install-baota.ps1` 是旧模板。执行时应将其中的远程命令转换为 SSH 插件工具调用。

## Resources

- resources/operation-matrix.md
- resources/maintenance-checklist.md
