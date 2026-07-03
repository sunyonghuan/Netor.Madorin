---
name: transfer
description: '服务器文件传输子技能。用于上传、下载、路径确认、覆盖策略和回传结果。'
user-invocable: true
---

# Server Transfer

## Scope

- 上传文件到服务器。
- 从服务器下载文件。
- 确认远端路径和覆盖策略。

## Rules

- 所有服务器文件传输必须使用 SSH 插件 SFTP 工具：`ssh_sftp_upload`、`ssh_sftp_download`、`ssh_sftp_transfer_poll`、`ssh_sftp_read_text`、`ssh_sftp_write_text_with_backup`、`ssh_sftp_replace_file_with_backup`。
- 如需先检查远端路径或目录，使用 `ssh_sftp_stat`、`ssh_sftp_list`、`ssh_sftp_mkdir`。
- 主机到服务器、服务器到主机的可控上传/下载优先使用 SSH 插件 SFTP 工具。
- 远端 `scp` 命令是可行的，但只能通过 `ssh_session_exec`、`ssh_run_once` 或 `ssh_exec_async` 在远端执行，适用于远端发起的服务器到服务器复制等场景。
- 不要通过 PowerShell 工具或本地 shell 执行 `scp`、`ssh cat`、重定向或其他文本转存方式传输远程文件。
- 传输前必须确认源路径和目标路径。
- 默认不覆盖目标文件；需要覆盖时必须明确说明。
- 传输结果要回报文件路径、结果和失败原因。

## Legacy Scripts

- `scripts/upload-file.ps1` 和 `scripts/download-file.ps1` 是旧模板。执行传输时使用 SSH 插件 SFTP 工具。

## Resources

- resources/transfer-policy.md
