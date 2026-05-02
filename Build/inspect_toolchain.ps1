$ErrorActionPreference = 'Stop'

$binDir = 'E:\Netor.me\Cortana\artifacts\DbInspect\bin\Release\net10.0'
$sqliteDll = Join-Path $binDir 'Microsoft.Data.Sqlite.dll'
$nativeDir = Join-Path $binDir 'runtimes\win-x64\native'
$env:PATH = $nativeDir + ';' + $env:PATH

Get-ChildItem -Path $binDir -Filter '*.dll' | ForEach-Object {
	try { Add-Type -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
}
Add-Type -Path $sqliteDll
[SQLitePCL.Batteries_V2]::Init()

$dbPath = 'D:\Contrna\cortana.db'
$cs = "Data Source=$dbPath;Mode=ReadOnly"
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection($cs)
$conn.Open()
try {
	# 取最后 30 条
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT Id, SessionId, Role, CreatedTimestamp, IFNULL(Content,'') AS Content, IFNULL(ContentsJson,'') AS ContentsJson
FROM ChatMessages
ORDER BY CreatedTimestamp DESC, rowid DESC
LIMIT 30
"@
	$r = $cmd.ExecuteReader()
	$rows = @()
	while ($r.Read()) {
		$rows += [pscustomobject]@{
			Id = [string]$r['Id']
			SessionId = [string]$r['SessionId']
			Role = [string]$r['Role']
			CreatedTs = [long]$r['CreatedTimestamp']
			Content = [string]$r['Content']
			Json = [string]$r['ContentsJson']
		}
	}
	$r.Close()
	[array]::Reverse($rows)

	Write-Host "==== 工具链结构分析（按时间正序） ====" -ForegroundColor Cyan
	$i = 0
	foreach ($row in $rows) {
		$i++
		$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds($row.CreatedTs).ToLocalTime().ToString('HH:mm:ss.fff')
		$callIds = @()
		$resultIds = @()
		$kinds = @()
		if ($row.Json -and $row.Json.Length -gt 2) {
			try {
				$parsed = $row.Json | ConvertFrom-Json -ErrorAction Stop
				if ($parsed -is [System.Array]) {
					foreach ($item in $parsed) {
						if ($null -ne $item.Kind) { $kinds += [string]$item.Kind }
						if ($null -ne $item.CallId) {
							if ([string]$item.Kind -eq 'functionCall') { $callIds += [string]$item.CallId }
							elseif ([string]$item.Kind -eq 'functionResult') { $resultIds += [string]$item.CallId }
						}
					}
				}
			} catch {}
		}
		$kindStr = ($kinds -join ',')
		$shortSess = if ($row.SessionId.Length -ge 8) { $row.SessionId.Substring(0,8) } else { $row.SessionId }
		$shortId = if ($row.Id.Length -ge 8) { $row.Id.Substring(0,8) } else { $row.Id }

		$color = switch ($row.Role) {
			'assistant' { 'Yellow' }
			'tool'      { 'Magenta' }
			'user'      { 'Green' }
			default     { 'White' }
		}
		Write-Host ("[{0:00}] {1} sess={2} id={3} role={4,-9} kinds=[{5}]" -f `
			$i, $tsStr, $shortSess, $shortId, $row.Role, $kindStr) -ForegroundColor $color
		if ($callIds.Count -gt 0)   { Write-Host ("       functionCall   CallIds: {0}" -f ($callIds -join ', ')) -ForegroundColor DarkYellow }
		if ($resultIds.Count -gt 0) { Write-Host ("       functionResult CallIds: {0}" -f ($resultIds -join ', ')) -ForegroundColor DarkMagenta }
		if ($row.Role -eq 'user' -or $row.Role -eq 'system') {
			$snippet = if ($row.Content.Length -gt 60) { $row.Content.Substring(0,60) + '...' } else { $row.Content }
			Write-Host ("       text: {0}" -f $snippet) -ForegroundColor DarkGray
		}
	}

	# ==== 全局诊断：每个 session 内"孤立的 tool 消息"和"未被消费的 functionCall" ====
	Write-Host "`n==== 跨会话工具链完整性扫描（最近 30 条）====" -ForegroundColor Cyan
	$bySession = $rows | Group-Object SessionId
	foreach ($g in $bySession) {
		$allCalls = @{}     # CallId -> 出现 functionCall 在第几条
		$allResults = @{}   # CallId -> 出现 functionResult 在第几条
		$idx = 0
		foreach ($row in $g.Group) {
			$idx++
			if ($row.Json -and $row.Json.Length -gt 2) {
				try {
					$parsed = $row.Json | ConvertFrom-Json -ErrorAction Stop
					if ($parsed -is [System.Array]) {
						foreach ($item in $parsed) {
							if ($null -ne $item.CallId) {
								if ([string]$item.Kind -eq 'functionCall')   { $allCalls[[string]$item.CallId] = "row#$idx role=$($row.Role)" }
								elseif ([string]$item.Kind -eq 'functionResult') { $allResults[[string]$item.CallId] = "row#$idx role=$($row.Role)" }
							}
						}
					}
				} catch {}
			}
		}
		$orphanResults = @($allResults.Keys | Where-Object { -not $allCalls.ContainsKey($_) })
		$unanswered    = @($allCalls.Keys   | Where-Object { -not $allResults.ContainsKey($_) })
		$shortSess = if ($g.Name.Length -ge 8) { $g.Name.Substring(0,8) } else { $g.Name }
		Write-Host ("Session {0}: {1} 条消息, functionCall={2}, functionResult={3}" -f $shortSess, $g.Count, $allCalls.Count, $allResults.Count) -ForegroundColor Cyan
		if ($orphanResults.Count -gt 0) {
			Write-Host "  ❌ 孤立的 functionResult（无对应 functionCall）—— 这是 OpenAI 400 错误的根因之一：" -ForegroundColor Red
			foreach ($k in $orphanResults) { Write-Host ("     CallId={0} 来自 {1}" -f $k, $allResults[$k]) -ForegroundColor Red }
		}
		if ($unanswered.Count -gt 0) {
			Write-Host "  ⚠️ 未被回应的 functionCall（无对应 functionResult）：" -ForegroundColor DarkYellow
			foreach ($k in $unanswered) { Write-Host ("     CallId={0} 来自 {1}" -f $k, $allCalls[$k]) -ForegroundColor DarkYellow }
		}
		if ($orphanResults.Count -eq 0 -and $unanswered.Count -eq 0 -and $allCalls.Count -gt 0) {
			Write-Host "  ✓ 工具链完整" -ForegroundColor Green
		}
	}
}
finally {
	$conn.Close()
}
