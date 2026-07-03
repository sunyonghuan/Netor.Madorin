# list-servers.ps1
# 列出所有服务器

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\ServerMaintainer.Common.ps1

$serversRoot = Get-ServersRoot -ScriptRoot $PSScriptRoot

if (-not (Test-Path $serversRoot)) {
    Write-Host "❌ 服务器目录不存在：$serversRoot" -ForegroundColor Red
    return @()
}

$servers = Get-ChildItem -Path $serversRoot -Directory | ForEach-Object {
    $serverMd = Join-Path $_.FullName 'server.md'
    if (-not (Test-Path $serverMd)) {
        return [PSCustomObject]@{
            IP = $_.Name
            Name = ''
            Username = ''
            Port = 22
            Path = $_.FullName
            HasKey = Test-Path (Join-Path $_.FullName 'id_rsa')
        }
    }

    $record = Read-ServerRecord -ServerFile $serverMd
    [PSCustomObject]@{
        IP = $record.IP
        Name = $record.Name
        Username = $record.Username
        Port = $record.Port
        Path = $_.FullName
        HasKey = Test-ServerKey -ServerRecord $record
    }
}

Write-Host "`n📊 服务器列表" -ForegroundColor Cyan
Write-Host "=============" -ForegroundColor Cyan

if ($servers.Count -eq 0) {
    Write-Host "暂无服务器，请先添加服务器。" -ForegroundColor Yellow
} else {
    $index = 1
    foreach ($server in $servers) {
        $displayName = if ($server.Name) { "$($server.Name) ($($server.IP))" } else { $server.IP }
        $authState = if ($server.HasKey) { 'key' } else { 'setup-required' }
        Write-Host "$index. $displayName [$authState]" -ForegroundColor White
        $index++
    }
}

return $servers
