# Baota Panel Operations Rules

## Overview
Rules for managing Baota Panel via REST API. Covers site management, database operations, backups, and security settings.

## Sub-Skills Reference
本文件是宝塔面板运维的核心规范，以下子技能在执行相关任务时可能需要加载：

| 子技能文件 | 职责 | 何时加载 |
|-----------|------|---------|
| `subskills/auth-rules.md` | API 凭证管理和安全认证 | 处理宝塔 API 认证、密钥管理时 |
| `subskills/inventory-rules.md` | 服务器清单管理 | 读取宝塔面板 URL 和 API SK 时 |

## API Authentication
- Always use `panel_url` and `api_sk` from inventory.
- Never hardcode credentials.
- Validate API response for authentication errors.

## Available Operations

### 网站管理
- `bt_bt_list_sites`: Query site list with pagination
- `bt_bt_add_site`: Create new website
- `bt_bt_delete_site`: Remove website
- `bt_bt_manage_site_domain`: Add/remove domains
- `bt_bt_set_site_status`: Enable/disable site (start/stop/pause/resume)

### 备份管理
- `bt_bt_get_site_backups`: Query backup list
- `bt_bt_site_backup`: Create/delete backups

### 系统监控
- `bt_bt_get_system_total`: Server system status (CPU, memory, load)
- `bt_bt_get_network_status`: Network and load status
- `bt_bt_get_disk_info`: Disk partition info

### 数据库管理
- `bt_bt_get_database_list`: Query database list
- `bt_bt_add_database`: Create new database
- `bt_bt_delete_database`: Remove database
- `bt_bt_manage_database_user`: Manage database users

### 面板管理
- `bt_bt_get_panel_status`: Get panel running status
- `bt_bt_restart_panel`: Restart Baota panel service
- `bt_bt_stop_panel`: Stop Baota panel service
- `bt_bt_start_panel`: Start Baota panel service
- `bt_bt_get_panel_log`: Get panel operation logs
- `bt_bt_get_login_logs`: Get panel login logs

### 安全与配置
- `bt_bt_get_firewall_rules`: Get firewall rules
- `bt_bt_add_firewall_rule`: Add firewall rule
- `bt_bt_delete_firewall_rule`: Delete firewall rule
- `bt_bt_get_ssl_info`: Get SSL certificate info
- `bt_bt_deploy_ssl`: Deploy SSL certificate
- `bt_bt_get_php_versions`: Get available PHP versions
- `bt_bt_set_php_version`: Set PHP version for site
- `bt_bt_get_proxy_config`: Get reverse proxy config
- `bt_bt_set_proxy_config`: Set reverse proxy config
- `bt_bt_get_cron_list`: Get cron task list
- `bt_bt_add_cron`: Add cron task
- `bt_bt_get_ftp_list`: Get FTP account list
- `bt_bt_add_ftp`: Add FTP account
- `bt_bt_get_dir_protection`: Get directory protection config
- `bt_bt_set_dir_protection`: Set directory protection

## Common Workflows
1. **Check all sites**: Loop through inventory, call `bt_bt_list_sites` for each panel.
2. **Create backup**: Call `bt_bt_site_backup` with action=create.
3. **Add domain**: Call `bt_bt_manage_site_domain` with action=add.
4. **Disable site**: Call `bt_bt_set_site_status` with target_status=stop.
5. **Restart panel**: Call `bt_bt_restart_panel`.
6. **Add database**: Call `bt_bt_add_database` with db name and password.
7. **Deploy SSL**: Call `bt_bt_deploy_ssl` with site_id and certificate path.

## Error Handling
- API timeout: Retry once, then log error.
- Permission denied: Check api_sk validity.
- Site not found: Verify site_id from list_sites first.
- Return structured JSON with status, data, and error fields.

## Output Format
```json
{
  "server_ip": "10.10.10.1",
  "panel_url": "http://10.10.10.1:8888",
  "action": "list_sites",
  "status": "success|error",
  "data": [],
  "error": null
}
```
