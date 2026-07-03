---
name: server-maintainer
description: Central dispatcher for server management. Reads server inventory and performs all remote server operations through the dedicated SSH plugin tools.
license: MIT
user-invocable: true
---

# Server Maintainer

## Overview
Central hub for managing multiple Linux servers and Baota panels. Reads `Servers/Inventory.json` to get server details, credentials, and tags. Routes tasks to sub-rules for specific operations (OS, Baota, Transfer, etc.). Aggregates results into unified reports.

## Tool Policy

- Remote server login, command execution, interactive shell, long-running jobs, and file transfer MUST use the dedicated SSH plugin tools.
- Use SSH connection/session tools for reusable sessions: `ssh_connect`, `ssh_session_info`, `ssh_session_status`, `ssh_list_sessions`, `ssh_disconnect`.
- Use SSH command tools for remote commands and scripts: `ssh_run_once`, `ssh_session_exec`, `ssh_exec_script`, `ssh_run_script_once`.
- Use SSH job tools for long-running remote work: `ssh_exec_async`, `ssh_job_poll`, `ssh_job_cancel`, `ssh_job_close`, `ssh_list_jobs`.
- Use SSH SFTP tools as the first-class host-controlled transfer path: `ssh_sftp_upload`, `ssh_sftp_download`, `ssh_sftp_list`, `ssh_sftp_read_text`, `ssh_sftp_write_text_with_backup`, `ssh_sftp_replace_file_with_backup`, `ssh_sftp_transfer_poll`.
- Remote-side `scp` commands are allowed only when intentionally executed through SSH plugin command/job tools such as `ssh_session_exec`, `ssh_run_once`, or `ssh_exec_async` (for example, server-to-server copy from the remote host). This is not a dedicated SCP transfer tool.
- Use SSH ops tools for common operations when available: `ssh_service_action`, `ssh_log_tail`, `ssh_find_process`, `ssh_find_port`, `ssh_wait_for_port`, `ssh_http_check`.
- Do not use PowerShell tools to create SSH sessions, run local `ssh`/`scp`, or manage remote servers.
- Local workspace file operations such as reading inventory, writing reports, and fixing local key ACLs may use normal filesystem/local commands, but remote effects must still go through SSH plugin tools.

## Routing Table

### 操作系统运维（路由到 `subskills/os-ops.md`）
| 关键词 | 说明 |
|--------|------|
| `check all servers`, `server status`, `collect status`, `巡检`, `收集状态` | 批量巡检所有服务器状态 |
| `server health`, `health check`, `健康检查` | 服务器健康检查 |
| `system log`, `view log`, `check log`, `查看日志`, `系统日志` | 查看系统日志、应用日志 |
| `check disk`, `disk usage`, `检查硬盘`, `磁盘空间` | 检查磁盘使用情况 |
| `check memory`, `memory usage`, `检查内存`, `内存状态` | 检查内存使用情况 |
| `check cpu`, `cpu status`, `CPU状态`, `CPU运行状态` | 检查 CPU 使用情况 |
| `system load`, `load average`, `系统负载`, `运行时间` | 查看系统负载和运行时间 |
| `check network`, `network status`, `端口状态`, `网络连接` | 检查网络连接和端口状态 |
| `check process`, `process status`, `进程状态`, `僵尸进程` | 检查进程状态 |
| `check service`, `service status`, `服务状态`, `systemctl` | 检查系统服务状态 |
| `restart server`, `reboot`, `重启服务器`, `关机重启` | 重启服务器（需确认） |
| `restart services`, `mass restart`, `重启服务`, `批量重启` | 批量重启服务 |
| `check update`, `security patch`, `系统更新`, `安全补丁` | 检查系统更新和安全补丁 |
| `check firewall`, `firewall rules`, `防火墙规则`, `安全组` | 检查防火墙规则 |
| `check login`, `login record`, `登录记录`, `失败尝试` | 查看用户登录记录和失败尝试 |
| `cleanup`, `clean log`, `清理日志`, `清理临时文件` | 清理日志和临时文件 |
| `check version`, `system info`, `系统版本`, `内核信息` | 查看系统版本和内核信息 |
| `check raid`, `storage status`, `RAID状态`, `存储阵列` | 检查 RAID/存储阵列状态 |
| `check cron`, `定时任务`, `crontab` | 查看 cron 定时任务 |
| `check swap`, `swap usage`, `交换分区` | 检查 swap 使用情况 |
| `check disk io`, `io wait`, `磁盘IO`, `读写速度` | 检查磁盘 IO 和读写速度 |
| `os maintenance`, `系统维护`, `操作系统维护` | 操作系统层面维护任务 |
| `generate report`, `巡检报告`, `监控报表`, `状态报表` | 生成巡检报告（属于 OS 运维） |

### 宝塔面板运维（路由到 `subskills/baota-ops.md`）
| 关键词 | 说明 |
|--------|------|
| `manage baota`, `panel operations`, `宝塔管理`, `面板操作` | 宝塔面板管理任务 |
| `create site`, `add site`, `创建网站`, `添加网站` | 创建/添加网站 |
| `modify site`, `edit site`, `修改网站`, `编辑网站` | 修改网站配置 |
| `pause site`, `stop site`, `暂停网站`, `停止网站` | 暂停/停止网站 |
| `resume site`, `start site`, `恢复网站`, `启动网站` | 恢复/启动网站 |
| `delete site`, `remove site`, `删除网站`, `移除网站` | 删除网站 |
| `baota status`, `面板状态`, `宝塔状态` | 查看宝塔面板状态 |
| `restart baota`, `stop baota`, `start baota`, `重启宝塔`, `暂停宝塔`, `启动宝塔` | 重启/停止/启动宝塔面板 |
| `backup site`, `backup database`, `备份网站`, `备份数据库` | 备份网站或数据库 |
| `restore backup`, `恢复备份` | 恢复备份 |
| `ssl certificate`, `SSL证书`, `配置证书` | 配置 SSL 证书 |
| `php version`, `PHP版本`, `切换PHP` | 修改 PHP 版本 |
| `site log`, `网站日志`, `宝塔日志` | 查看网站/面板日志 |
| `add database`, `create database`, `添加数据库`, `创建数据库` | 添加数据库/用户 |
| `reverse proxy`, `反向代理`, `配置代理` | 配置反向代理 |
| `baota firewall`, `宝塔防火墙` | 设置宝塔防火墙规则 |
| `panel log`, `登录日志`, `面板登录` | 查看面板登录日志 |
| `change panel port`, `修改面板端口` | 修改面板端口 |
| `install plugin`, `uninstall plugin`, `安装插件`, `卸载插件` | 安装/卸载宝塔插件 |
| `plan task`, `计划任务`, `定时任务` | 配置计划任务 |
| `ftp account`, `FTP账号`, `FTP管理` | 管理 FTP 账号 |
| `directory protection`, `目录保护`, `密码保护` | 设置目录保护 |

### 其他路由
| 关键词 | 说明 |
|--------|------|
| `update inventory`, `manage servers`, `更新清单`, `管理服务器` | 修改 `Servers/Inventory.json` |
| `file transfer`, `upload`, `download`, `文件传输`, `上传文件`, `下载文件` | 文件传输操作 |
| `ssh key`, `authentication`, `密钥管理`, `认证问题` | SSH 密钥和认证管理 |

## Scope
1. Reading and parsing `Servers/Inventory.json` from workspace root.
2. Validating SSH key paths and API keys exist.
3. Iterating through server list and dispatching tasks to appropriate sub-rules.
4. Aggregating JSON outputs from SSH plugin tool calls and sub-skill workflows.
5. Formatting final Markdown reports with success/failure summaries.
6. Handling timeouts and connection errors gracefully.

## Out of Scope
1. Direct local `ssh`/`scp` process invocation or PowerShell-wrapped SSH sessions.
2. Direct Baota API calls (handled by `baota-ops.md` rules).
3. Writing or modifying `Inventory.json` (manual or separate script).

## Workflow
1. **Load Inventory**: Read `Servers/Inventory.json` from workspace root `Servers/` directory.
2. **Validate**: Ensure SSH key paths specified in inventory exist. If missing, halt and report error.
3. **Dispatch**:
   - For OS tasks: Load `subskills/os-ops.md` and use SSH plugin command/session tools.
   - For Baota tasks: Load `subskills/baota-ops.md` and execute Baota API calls.
   - For transfer tasks: Load `subskills/transfer-rules.md` and use SSH SFTP tools for host-controlled transfer; use remote `scp` only through SSH plugin command/job tools when the copy must run on the remote side.
4. **Collect**: Gather structured responses from each SSH plugin tool call or sub-skill workflow.
5. **Aggregate**: Combine results, handle errors, and output a structured Markdown table.

## Resources
- `inventory-location` - Server inventory is located at `Servers/Inventory.json` in the workspace root.
- `keys-directory` - Directory containing SSH private keys (`Servers/Keys/`).
- `inventory-template` - JSON template for server inventory (`resources/inventory-template.json`).

## Legacy Scripts
- Files under `scripts/` are legacy local helpers/templates. Do not use them for remote server execution when SSH plugin tools are available.
- If a legacy script documents a remote action, translate that action into the matching SSH plugin tool call instead of running the script through PowerShell.

## Best Practices
- Always validate key paths before dispatching to avoid runtime errors.
- Use parallel execution where possible for independent servers.
- Return structured results from SSH plugin tools; do not return raw console output as the final answer.
- If a server fails, log the error and continue to the next; do not abort the entire batch.
- Do not store sensitive data in the skill directory; use `Servers/` for data.
- **关键**：收到服务器相关请求时，必须先根据 Routing Table 判断属于 OS 运维还是宝塔运维，然后加载对应的子技能文件，严格遵循子技能中的规范执行。
