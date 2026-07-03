param(
	[string]$ServerId,
	[string]$FilePath
)

$ErrorActionPreference = 'Stop'
. $PSScriptRoot\ServerMaintainer.Common.ps1

if ([string]::IsNullOrWhiteSpace($FilePath)) {
	if ([string]::IsNullOrWhiteSpace($ServerId)) {
		throw '必须提供 ServerId 或 FilePath。'
	}

	$serverPath = Get-ServerDirectory -ScriptRoot $PSScriptRoot -ServerId $ServerId
	if (-not $serverPath) {
		throw "未找到服务器目录：$ServerId"
	}

	$FilePath = Join-Path $serverPath 'id_rsa'
}

if (-not (Test-Path $FilePath)) {
	throw "私钥文件不存在：$FilePath"
}

# 2. 获取当前执行用户的 SID
$CurrentUserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

# 3. 断开继承权限 (清除旧权限)
icacls "$FilePath" /inheritance:r

# 4. 使用 SID 授予完全控制权限
icacls "$FilePath" /grant:r "*$CurrentUserSid`:F"

# 5. 验证结果
Write-Host "权限设置完成。当前权限如下：" -ForegroundColor Green
icacls "$FilePath"
