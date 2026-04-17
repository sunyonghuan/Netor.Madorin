# OS Operations Rules

## Overview
Rules for executing OS-level commands via SSH. Covers system monitoring, service management, log analysis, and maintenance.

## Sub-Skills Reference
本文件是操作系统运维的核心规范，以下 4 个子技能属于 OS 运维层面，执行相关任务时必须加载并遵循：

| 子技能文件 | 职责 | 何时加载 |
|-----------|------|---------|
| `subskills/reporting/SKILL.md` | 服务器监控与报表生成 | 生成巡检报告、状态报表、监控报表时 |
| `subskills/auth-rules.md` | SSH 密钥管理和安全认证 | 连接服务器、处理认证问题、密钥管理时 |
| `subskills/inventory-rules.md` | 服务器清单管理 | 读取/修改服务器清单、验证服务器信息时 |
| `subskills/transfer-rules.md` | 文件传输操作 | 上传/下载文件、同步目录、SFTP/SCP 操作时 |

## SSH Connection Rules
- Use `sys_start_remote_session` with host, username, and privateKeyPath.
- Always close sessions with `sys_close_session` after execution.
- Set timeout to 30000ms for standard commands.
- Sanitize all command inputs to prevent injection.

## Health Check Commands
- CPU: `top -bn1 | grep "Cpu(s)"`
- Memory: `free -m`
- Disk: `df -h`
- Load: `uptime`
- Services: `systemctl status nginx mysql php-fpm`

## Service Management
- Restart: `systemctl restart {service}`
- Stop: `systemctl stop {service}`
- Start: `systemctl start {service}`
- Enable: `systemctl enable {service}`

## System Maintenance
- Reboot: `reboot` (必须确认用户意图后执行)
- Shutdown: `shutdown -h now` (必须确认用户意图后执行)

## Log Analysis
- Nginx error: `tail -n 50 /var/log/nginx/error.log`
- MySQL slow: `tail -n 50 /var/log/mysql/mysql-slow.log`
- System: `journalctl -xe --no-pager -n 50`
- Auth log: `tail -n 50 /var/log/auth.log` (Ubuntu) 或 `tail -n 50 /var/log/secure` (CentOS)
- Failed login: `grep "Failed password" /var/log/auth.log | wc -l`

## System Info
- OS version: `cat /etc/os-release`
- Kernel: `uname -r`
- Hostname: `hostname`
- Uptime: `uptime`

## Network & Ports
- Connections: `ss -tulnp` 或 `netstat -tulnp`
- Listening ports: `ss -tlnp`
- Network stats: `cat /proc/net/dev`

## Process Management
- Top processes: `ps aux --sort=-%cpu | head -20`
- Zombie processes: `ps aux | awk '$8=="Z"'`
- Process tree: `pstree -p`

## Disk & Storage
- Disk usage: `df -h`
- Large files: `find / -type f -size +100M 2>/dev/null | head -20`
- IO stats: `iostat -x 1 3` 或 `cat /proc/diskstats`
- RAID status: `cat /proc/mdstat` 或 `megacli -LDInfo -Lall -aALL`

## Security
- Firewall: `iptables -L -n` 或 `ufw status`
- Failed logins: `lastb | head -20`
- SSH config: `cat /etc/ssh/sshd_config`

## Cleanup Operations
- Temp files: `find /tmp -type f -mtime +7 -delete`
- Old logs: `find /var/log -name "*.log" -mtime +30 -delete`
- Package cache: `apt-get clean` or `yum clean all`

## Report Generation
- 巡检报告必须保存到 `Reports/YYYYMMDD.md`（工作区目录下）
- 同日重复生成时覆盖当日文件
- 连接失败的服务器必须出现在报表中，状态标记为离线或失败
- 详细报表格式和阈值规则参见 `subskills/reporting/SKILL.md` 和 `resources/report-template.md`

## Output Format
Return structured JSON:
```json
{
  "server_ip": "10.10.10.1",
  "status": "success|error",
  "cpu_usage": "45%",
  "mem_usage": "60%",
  "disk_usage": "75%",
  "load_avg": "1.2, 0.8, 0.5",
  "services": {"nginx": "running", "mysql": "running"},
  "error": null
}
```
