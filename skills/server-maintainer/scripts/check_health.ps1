param(
    [string]$Ip,
    [string]$User,
    [string]$KeyPath
)

# Legacy template only. Remote health collection must use the dedicated SSH plugin tools:
# ssh_connect/ssh_run_once/ssh_session_exec for commands, then ssh_disconnect for reusable sessions.

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
