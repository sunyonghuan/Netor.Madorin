param(
    [string]$Ip,
    [string]$User,
    [string]$KeyPath
)

# This script is a template for AI to fill in actual SSH command execution
# AI should use sys_start_remote_session, sys_send_command, sys_close_session

# Template output
@{
    server_ip = $Ip
    status = "success"
    cpu_usage = "45%"
    mem_usage = "60%"
    disk_usage = "75%"
    load_avg = "1.2, 0.8, 0.5"
    services = @{
        nginx = "running"
        mysql = "running"
        php_fpm = "running"
    }
    timestamp = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
} | ConvertTo-Json
