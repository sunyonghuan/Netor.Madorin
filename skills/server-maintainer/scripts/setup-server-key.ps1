param(
    [Parameter(Mandatory=$true)]
    [string]$ServerIP,

    [Parameter(Mandatory=$false)]
    [int]$Port = 22,

    [Parameter(Mandatory=$true)]
    [string]$Username,

    [Parameter(Mandatory=$true)]
    [string]$WorkspaceDir = ''
)

throw "Legacy script disabled. Use SSH plugin tools instead: ssh_run_script_once/ssh_exec_script for remote setup and ssh_sftp_download for key transfer."
