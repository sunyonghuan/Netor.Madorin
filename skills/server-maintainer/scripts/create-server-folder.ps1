# create-server-folder.ps1
# 创建服务器文件夹结构

param(
    [string]$ip,
    [string]$name,
    [string]$username,
    [int]$port = 22,
    [string]$domain = '',
    [string]$loginMethod = '密钥'
)

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\ServerMaintainer.Common.ps1

$serversRoot = Get-ServersRoot -ScriptRoot $PSScriptRoot
$serverPath = Join-Path $serversRoot $ip

if (-not (Test-Path $serversRoot)) {
    New-Item -Path $serversRoot -ItemType Directory -Force | Out-Null
}

# 创建服务器文件夹
if (-not (Test-Path $serverPath)) {
    New-Item -Path $serverPath -ItemType Directory -Force | Out-Null
    Write-Host "✅ 创建服务器文件夹：$serverPath" -ForegroundColor Green
} else {
    Write-Host "⚠️ 服务器文件夹已存在：$serverPath" -ForegroundColor Yellow
}

# 创建 server.md 文件
$serverMdPath = Join-Path $serverPath 'server.md'
$createDate = Get-Date -Format 'yyyy-MM-dd'

$serverMdContent = @"
# $name
 
## 基础信息
- IP：$ip
- 端口号：$port
- 名称：$(if ($domain) { $domain } else { $name })
- 用户名：$username
- 登录方式：$loginMethod

## 系统信息
- 系统版本：待更新
- 公网访问：待更新
- 业务用途：待更新

## SSH 密钥信息
- 私钥文件：id_rsa
- 密钥类型：待确认
- 创建时间：$createDate

## 备注
- 待补充

---
创建日期：$createDate
最后更新时间：$createDate
"@

Set-Content -Path $serverMdPath -Value $serverMdContent -Encoding UTF8
Write-Host "✅ 创建服务器信息文件：$serverMdPath" -ForegroundColor Green

Write-Host "`n📁 服务器文件夹结构创建完成！" -ForegroundColor Cyan
Write-Host "请将 SSH 私钥存放到：$serverPath\id_rsa" -ForegroundColor Cyan
