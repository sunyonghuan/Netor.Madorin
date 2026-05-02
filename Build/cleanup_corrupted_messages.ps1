#requires -Version 7.0
<#
.SYNOPSIS
	清理 D:\Contrna\cortana.db 中已损坏的 ChatMessages 数据。

.DESCRIPTION
	根据诊断结论处理两类损坏：
	  1) 历史空 tool 消息（Content 与 ContentsJson 都为空）—— 直接删除
	  2) 协议违规会话：assistant(tool_calls) 后未紧邻对应 tool 消息 —— 列出并可选删除整个会话

	默认 DryRun 模式仅打印计划，使用 -Apply 才实际执行。
	始终自动备份数据库到 *.bak.<时间戳>。

.EXAMPLE
	pwsh Build\cleanup_corrupted_messages.ps1                  # 仅扫描
	pwsh Build\cleanup_corrupted_messages.ps1 -Apply           # 应用清理（空 tool 消息）
	pwsh Build\cleanup_corrupted_messages.ps1 -Apply -DropBadSessions   # 同时删除协议违规会话
#>
param(
	[string]$DbPath = 'D:\Contrna\cortana.db',
	[switch]$Apply,
	[switch]$DropBadSessions
)

$ErrorActionPreference = 'Stop'

# ---- 加载 SQLite ----
$binDir = 'E:\Netor.me\Cortana\artifacts\DbInspect\bin\Release\net10.0'
$nativeDir = Join-Path $binDir 'runtimes\win-x64\native'
$env:PATH = $nativeDir + ';' + $env:PATH
Get-ChildItem -Path $binDir -Filter '*.dll' | ForEach-Object {
	try { Add-Type -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
}
Add-Type -Path (Join-Path $binDir 'Microsoft.Data.Sqlite.dll')
[SQLitePCL.Batteries_V2]::Init()

if (-not (Test-Path $DbPath)) { throw "数据库不存在: $DbPath" }

# ---- 备份 ----
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$bakPath = "$DbPath.bak.$ts"
Copy-Item $DbPath $bakPath -Force
Write-Host "已备份数据库到: $bakPath" -ForegroundColor Green

# ---- 打开连接（写模式仅在 -Apply 下使用） ----
$mode = if ($Apply) { '' } else { ';Mode=ReadOnly' }
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$DbPath$mode")
$conn.Open()
try {
	# ====== 1) 扫描空 tool 消息 ======
	Write-Host "`n==== [1] 扫描空 tool 消息（Content+ContentsJson 均为空） ====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT COUNT(*) FROM ChatMessages
WHERE Role='tool' AND IFNULL(Content,'')='' AND IFNULL(ContentsJson,'')=''
"@
	$emptyToolCount = [int]$cmd.ExecuteScalar()
	Write-Host "  待清理空 tool 消息: $emptyToolCount 条" -ForegroundColor Yellow

	# ====== 2) 扫描协议违规会话 ======
	Write-Host "`n==== [2] 扫描协议违规会话（assistant tool_calls 后未紧邻 tool） ====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT Id, SessionId, Role, IFNULL(ContentsJson,'') AS Cj, CreatedTimestamp, rowid
FROM ChatMessages
ORDER BY SessionId, CreatedTimestamp ASC, rowid ASC
"@
	$r = $cmd.ExecuteReader()
	$bySession = @{}
	while ($r.Read()) {
		$sid = [string]$r['SessionId']
		if (-not $bySession.ContainsKey($sid)) { $bySession[$sid] = New-Object System.Collections.ArrayList }
		$null = $bySession[$sid].Add([pscustomobject]@{
			Role = [string]$r['Role']
			Cj   = [string]$r['Cj']
			Ts   = [long]$r['CreatedTimestamp']
		})
	}
	$r.Close()

	$callPattern = '"Kind":"functionCall"'
	$resultPattern = '"Kind":"functionResult"'
	$badSessions = @()
	foreach ($kv in $bySession.GetEnumerator()) {
		$msgs = $kv.Value
		for ($i = 0; $i -lt $msgs.Count; $i++) {
			$m = $msgs[$i]
			if ($m.Role -eq 'assistant' -and ($m.Cj -match $callPattern)) {
				$next = if ($i + 1 -lt $msgs.Count) { $msgs[$i+1] } else { $null }
				if ($null -eq $next -or $next.Role -ne 'tool') {
					$badSessions += $kv.Key
					break
				}
			}
		}
	}
	$badSessions = $badSessions | Sort-Object -Unique
	Write-Host "  协议违规会话数: $($badSessions.Count)" -ForegroundColor Yellow
	if ($badSessions.Count -gt 0) {
		Write-Host "  会话 ID 列表:" -ForegroundColor DarkGray
		foreach ($s in $badSessions) {
			$cnt = $bySession[$s].Count
			$lastTs = ($bySession[$s] | Measure-Object -Property Ts -Maximum).Maximum
			$lastStr = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$lastTs).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
			Write-Host ("    {0}  msgs={1}  last={2}" -f $s, $cnt, $lastStr) -ForegroundColor DarkGray
		}
	}

	# ====== 3) 执行 ======
	if (-not $Apply) {
		Write-Host "`n[DryRun] 未指定 -Apply，未执行任何修改。" -ForegroundColor Magenta
		return
	}

	Write-Host "`n==== [3] 执行清理 ====" -ForegroundColor Cyan
	$tx = $conn.BeginTransaction()
	try {
		# 3.1 删除空 tool 消息
		$cmd = $conn.CreateCommand()
		$cmd.Transaction = $tx
		$cmd.CommandText = @"
DELETE FROM ChatMessages
WHERE Role='tool' AND IFNULL(Content,'')='' AND IFNULL(ContentsJson,'')=''
"@
		$n = $cmd.ExecuteNonQuery()
		Write-Host "  删除空 tool 消息: $n 条" -ForegroundColor Green

		# 3.2 删除协议违规会话（仅在显式指定时）
		if ($DropBadSessions -and $badSessions.Count -gt 0) {
			$totalDeletedMsgs = 0
			$totalDeletedSessions = 0
			$totalDeletedAssets = 0
			$totalDeletedSegments = 0
			foreach ($sid in $badSessions) {
				$cmd = $conn.CreateCommand()
				$cmd.Transaction = $tx
				$cmd.CommandText = "DELETE FROM ChatMessages WHERE SessionId = @sid"
				$null = $cmd.Parameters.AddWithValue('@sid', $sid)
				$totalDeletedMsgs += $cmd.ExecuteNonQuery()

				$cmd = $conn.CreateCommand()
				$cmd.Transaction = $tx
				$cmd.CommandText = "DELETE FROM ChatMessageAssets WHERE SessionId = @sid"
				$null = $cmd.Parameters.AddWithValue('@sid', $sid)
				$totalDeletedAssets += $cmd.ExecuteNonQuery()

				$cmd = $conn.CreateCommand()
				$cmd.Transaction = $tx
				$cmd.CommandText = "DELETE FROM CompactionSegments WHERE SessionId = @sid"
				$null = $cmd.Parameters.AddWithValue('@sid', $sid)
				$totalDeletedSegments += $cmd.ExecuteNonQuery()

				$cmd = $conn.CreateCommand()
				$cmd.Transaction = $tx
				$cmd.CommandText = "DELETE FROM ChatSessions WHERE Id = @sid"
				$null = $cmd.Parameters.AddWithValue('@sid', $sid)
				$totalDeletedSessions += $cmd.ExecuteNonQuery()
			}
			Write-Host "  删除协议违规会话: 会话 $totalDeletedSessions 个 / 消息 $totalDeletedMsgs 条 / 资源 $totalDeletedAssets 条 / 摘要段 $totalDeletedSegments 条" -ForegroundColor Green
		}
		elseif ($badSessions.Count -gt 0) {
			Write-Host "  ℹ️ 协议违规会话存在，但未指定 -DropBadSessions，已跳过。读取端的协议重排会自动处理。" -ForegroundColor DarkYellow
		}

		$tx.Commit()
		Write-Host "`n✓ 清理完成。" -ForegroundColor Green
	}
	catch {
		$tx.Rollback()
		Write-Host "❌ 清理失败已回滚: $_" -ForegroundColor Red
		throw
	}

	# 3.3 VACUUM 收缩
	if ($Apply) {
		Write-Host "  执行 VACUUM ..." -ForegroundColor Cyan
		$cmd = $conn.CreateCommand()
		$cmd.CommandText = "VACUUM"
		$cmd.ExecuteNonQuery() | Out-Null
		Write-Host "  ✓ VACUUM 完成。" -ForegroundColor Green
	}
}
finally {
	$conn.Close()
}
