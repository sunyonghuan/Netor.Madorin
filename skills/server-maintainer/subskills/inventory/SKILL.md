---
name: inventory
description: '服务器登记与清单子技能。用于新增服务器、维护 server.md、列出服务器、校验目录结构。'
user-invocable: true
---

# Server Inventory

## Scope

- 新增服务器记录。
- 更新 server.md。
- 列出服务器并支持按 IP、名称选择。
- 校验 Servers 目录结构与字段完整性。

## Rules

- 服务器记录统一保存在工作区下的 Servers/{IP}/。
- server.md 信息完整时，不重复询问 IP、端口、用户名。
- 新增或更新记录后必须按 `resources/server-record-schema.md` 校验字段完整性。
- 如需验证远程连通性，使用 SSH 插件 `ssh_connect` 或 `ssh_run_once`，不要调用 PowerShell 脚本或 `ssh` 命令。

## Legacy Scripts

- `scripts/create-server-folder.ps1`、`scripts/list-servers.ps1`、`scripts/validate-server-info.ps1` 是旧本地辅助脚本。优先直接读写工作区文件并按 schema 校验。

## Resources

- resources/server-record-schema.md
