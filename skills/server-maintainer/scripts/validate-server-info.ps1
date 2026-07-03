# validate-server-info.ps1
# 验证服务器信息完整性

param(
    [string]$serverPath
)

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\ServerMaintainer.Common.ps1

$requiredFields = @('IP', '端口号', '用户名', '登录方式')
$missingFields = @()

if (Test-Path $serverPath) {
    $serverFile = if ((Get-Item $serverPath).PSIsContainer) { Join-Path $serverPath 'server.md' } else { $serverPath }
    $record = Read-ServerRecord -ServerFile $serverFile

    if ([string]::IsNullOrWhiteSpace($record.IP)) { $missingFields += 'IP' }
    if ($record.Port -le 0) { $missingFields += '端口号' }
    if ([string]::IsNullOrWhiteSpace($record.Username)) { $missingFields += '用户名' }
    if ([string]::IsNullOrWhiteSpace($record.LoginMethod)) { $missingFields += '登录方式' }

    if ($record.LoginMethod -eq '密钥' -and -not (Test-ServerKey -ServerRecord $record)) {
        Write-Host "⚠️ 警告：登录方式为密钥，但找不到 id_rsa 文件" -ForegroundColor Yellow
    }

    if ($missingFields.Count -gt 0) {
        Write-Host "❌ 信息缺失：$($missingFields -join ', ')" -ForegroundColor Red
        return $false
    } else {
        Write-Host "✅ 服务器信息完整" -ForegroundColor Green
        return $true
    }
} else {
    Write-Host "❌ 服务器信息文件不存在：$serverPath" -ForegroundColor Red
    return $false
}
