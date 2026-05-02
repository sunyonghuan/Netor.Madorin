$ErrorActionPreference = 'Stop'

$binDir = 'E:\Netor.me\Cortana\artifacts\DbInspect\bin\Release\net10.0'
$nativeDir = Join-Path $binDir 'runtimes\win-x64\native'
$env:PATH = $nativeDir + ';' + $env:PATH
Get-ChildItem -Path $binDir -Filter '*.dll' | ForEach-Object {
	try { Add-Type -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
}
Add-Type -Path (Join-Path $binDir 'Microsoft.Data.Sqlite.dll')
[SQLitePCL.Batteries_V2]::Init()

$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=D:\Contrna\cortana.db;Mode=ReadOnly")
$conn.Open()
try {
	# 取 ec7634a2 全部消息，使用纯字符串 contains 来识别 functionCall/Result（避开 PowerShell JSON 数组展开 bug）
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT Id, Role, CreatedTimestamp, IFNULL(Content,'') AS Content, IFNULL(ContentsJson,'') AS Cj
FROM ChatMessages WHERE SessionId LIKE 'ec7634a2%'
ORDER BY CreatedTimestamp ASC, rowid ASC
"@
	$r = $cmd.ExecuteReader()
	$rows = @()
	while ($r.Read()) {
		$rows += [pscustomobject]@{
			Id = [string]$r['Id']
			Role = [string]$r['Role']
			Ts = [long]$r['CreatedTimestamp']
			Content = [string]$r['Content']
			Cj = [string]$r['Cj']
		}
	}
	$r.Close()

	# 用正则提取 CallId
	$callPattern = '"Kind":"functionCall","CallId":"([^"]+)"'
	$resultPattern = '"Kind":"functionResult","CallId":"([^"]+)"'

	$allCalls = @{}
	$allResults = @{}
	$idx = 0
	foreach ($m in $rows) {
		$idx++
		$cm = [regex]::Matches($m.Cj, $callPattern)
		$rm = [regex]::Matches($m.Cj, $resultPattern)
		$calls = @($cm | ForEach-Object { $_.Groups[1].Value })
		$results = @($rm | ForEach-Object { $_.Groups[1].Value })
		foreach ($c in $calls) { $allCalls[$c] = $idx }
		foreach ($c in $results) { $allResults[$c] = $idx }

		$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds($m.Ts).ToLocalTime().ToString('HH:mm:ss.fff')
		$color = switch ($m.Role) { 'assistant' {'Yellow'}; 'tool' {'Magenta'}; 'user' {'Green'}; default {'White'} }
		$callsStr = if ($calls.Count -gt 0) { "calls=[$($calls -join ',')]" } else { '' }
		$resultsStr = if ($results.Count -gt 0) { "results=[$($results -join ',')]" } else { '' }
		Write-Host ("[{0:00}] {1} {2,-9} {3} {4}" -f $idx, $tsStr, $m.Role, $callsStr, $resultsStr) -ForegroundColor $color
		if ($m.Role -eq 'tool' -and $results.Count -eq 0) {
			Write-Host ("       ❌ tool 消息无 functionResult! Content='{0}' CjLen={1}" -f `
				($(if ($m.Content.Length -gt 50) { $m.Content.Substring(0,50) + '...' } else { $m.Content })), $m.Cj.Length) -ForegroundColor Red
			if ($m.Cj.Length -gt 0) {
				Write-Host ("       Cj: {0}" -f $(if ($m.Cj.Length -gt 200) { $m.Cj.Substring(0,200) + '...' } else { $m.Cj })) -ForegroundColor DarkRed
			}
		}
		if ($m.Role -eq 'user') {
			$s = if ($m.Content.Length -gt 60) { $m.Content.Substring(0,60) + '...' } else { $m.Content }
			Write-Host ("       text: {0}" -f $s) -ForegroundColor DarkGray
		}
	}

	Write-Host "`n--- 工具链匹配 ---" -ForegroundColor Cyan
	Write-Host "总 functionCall  CallId 数: $($allCalls.Count)"
	Write-Host "总 functionResult CallId 数: $($allResults.Count)"
	$orphan = @($allResults.Keys | Where-Object { -not $allCalls.ContainsKey($_) })
	$unanswered = @($allCalls.Keys | Where-Object { -not $allResults.ContainsKey($_) })
	if ($orphan.Count -gt 0) { Write-Host "❌ 孤立 functionResult: $($orphan -join ',')" -ForegroundColor Red }
	if ($unanswered.Count -gt 0) {
		Write-Host "❌ 未回应 functionCall:" -ForegroundColor Red
		foreach ($k in $unanswered) { Write-Host "   $k 出现于 row#$($allCalls[$k])" -ForegroundColor Red }
	}

	# 同时检查 OpenAI 协议级别的"邻接性"问题：assistant 含 tool_calls 后必须紧邻 tool 消息
	Write-Host "`n--- OpenAI 协议邻接性检查（assistant 含 functionCall 后必须紧跟 tool 消息）---" -ForegroundColor Cyan
	for ($i = 0; $i -lt $rows.Count; $i++) {
		$m = $rows[$i]
		if ($m.Role -eq 'assistant' -and ([regex]::IsMatch($m.Cj, $callPattern))) {
			$next = if ($i + 1 -lt $rows.Count) { $rows[$i+1] } else { $null }
			if ($null -eq $next -or $next.Role -ne 'tool') {
				$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds($m.Ts).ToLocalTime().ToString('HH:mm:ss.fff')
				$nextRole = if ($null -eq $next) { '<EOF>' } else { $next.Role }
				Write-Host ("  ⚠️ row#{0} {1} assistant(tool_calls) 后紧邻 role={2} —— 违反 OpenAI 协议" -f ($i+1), $tsStr, $nextRole) -ForegroundColor Red
			}
		}
	}
}
finally {
	$conn.Close()
}
