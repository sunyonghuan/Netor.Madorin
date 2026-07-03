---
name: auth
description: '服务器认证与密钥治理子技能。用于密钥优先连接、密钥初始化、权限修复、密码兜底条件控制。'
user-invocable: true
---

# Server Auth

## Scope

- 检查并决定认证方式。
- 初始化 SSH 密钥。
- 修复 Windows 私钥权限。
- 约束何时允许询问密码。

## Rules

- 远程连接、认证验证和服务器端密钥初始化必须使用 SSH 插件工具，不要通过 PowerShell 启动 `ssh`/`scp`。
- 连接验证优先使用 `ssh_connect` + `ssh_session_exec`，临时验证可使用 `ssh_run_once`。
- 服务器端批量配置脚本使用 `ssh_exec_script` 或 `ssh_run_script_once` 上传并执行。
- 只要 Servers/{IP}/id_rsa 存在，就禁止再次询问 SSH 密码。
- 只有首次接入且本地不存在密钥时，才允许进入密码兜底流程。
- 密码不能写入文件、脚本参数、日志或报表。
- 密钥权限异常时先执行 fix-key-permission.ps1，再决定是否要求重新配置。

## Legacy Scripts

- `scripts/connect-server.ps1` and `scripts/setup-server-key.ps1` are legacy references. Translate their remote actions into SSH plugin tool calls.
- `scripts/fix-key-permission.ps1` may be used only for local Windows ACL repair of a local private key file.

## Resources

- resources/auth-policy.md
