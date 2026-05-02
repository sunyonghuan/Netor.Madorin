$ErrorActionPreference = 'Stop'

$binDir = 'E:\Netor.me\Cortana\artifacts\DbInspect\bin\Release\net10.0'
$nativeDir = Join-Path $binDir 'runtimes\win-x64\native'
$env:PATH = $nativeDir + ';' + $env:PATH
Get-ChildItem -Path $binDir -Filter '*.dll' | ForEach-Object {
	try { Add-Type -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
}
Add-Type -Path (Join-Path $binDir 'Microsoft.Data.Sqlite.dll')
[SQLitePCL.Batteries_V2]::Init()

$dbPath = 'D:\Contrna\cortana.db'
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath;Mode=ReadOnly")
$conn.Open()
try {
	# 取所有包含问题会话的完整消息
	$targetSessions = @('ec7634a2', '30c5b22a', '2a434d55', '41befae0', '73689b3b', 'f239061d')
	foreach ($shortSess in $targetSessions) {
		Write-Host "`n=========== Session $shortSess 的完整消息 ===========" -ForegroundColor Cyan

		$cmd = $conn.CreateCommand()
		$cmd.CommandText = @"
SELECT Id, SessionId, Role, CreatedTimestamp, IFNULL(Content,'') AS Content, IFNULL(ContentsJson,'') AS Cj
FROM ChatMessages
WHERE SessionId LIKE @s
ORDER BY CreatedTimestamp ASC, rowid ASC
"@
		$null = $cmd.Parameters.AddWithValue('@s', "$shortSess%")
		$r = $cmd.ExecuteReader()
		$idx = 0
		$allCalls = @{}
		$allResults = @{}
		$msgList = @()
		while ($r.Read()) {
			$idx++
			$row = [pscustomobject]@{
				Idx = $idx
				Id = [string]$r['Id']
				Role = [string]$r['Role']
				Ts = [long]$r['CreatedTimestamp']
				Content = [string]$r['Content']
				Cj = [string]$r['Cj']
				Kinds = @()
				Calls = @()
				Results = @()
			}
			if ($row.Cj.Length -gt 2) {
				try {
					$parsed = $row.Cj | ConvertFrom-Json -ErrorAction Stop
					if ($parsed -is [System.Array]) {
						foreach ($it in $parsed) {
							if ($null -ne $it.Kind) { $row.Kinds += [string]$it.Kind }
							if ($null -ne $it.CallId) {
								if ([string]$it.Kind -eq 'functionCall') {
									$row.Calls += [string]$it.CallId
									$allCalls[[string]$it.CallId] = $idx
								}
								elseif ([string]$it.Kind -eq 'functionResult') {
									$row.Results += [string]$it.CallId
									$allResults[[string]$it.CallId] = $idx
								}
							}
						}
					}
				} catch {}
			}
			$msgList += $row
		}
		$r.Close()

		foreach ($m in $msgList) {
			$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds($m.Ts).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss.fff')
			$color = switch ($m.Role) {
				'assistant' { 'Yellow' }; 'tool' { 'Magenta' }; 'user' { 'Green' }; default { 'White' }
			}
			$shortId = if ($m.Id.Length -ge 8) { $m.Id.Substring(0,8) } else { $m.Id }
			Write-Host ("  [{0:00}] {1} {2,-9} id={3} kinds=[{4}]" -f $m.Idx, $tsStr, $m.Role, $shortId, ($m.Kinds -join ',')) -ForegroundColor $color
			if ($m.Calls.Count -gt 0)   { Write-Host ("       calls   : {0}" -f ($m.Calls -join ', ')) -ForegroundColor DarkYellow }
			if ($m.Results.Count -gt 0) { Write-Host ("       results : {0}" -f ($m.Results -join ', ')) -ForegroundColor DarkMagenta }
			if ($m.Role -eq 'user') {
				$s = if ($m.Content.Length -gt 80) { $m.Content.Substring(0,80) + '...' } else { $m.Content }
				Write-Host ("       text: {0}" -f $s) -ForegroundColor DarkGray
			}
		}

		$orphanResults = @($allResults.Keys | Where-Object { -not $allCalls.ContainsKey($_) })
		$unanswered    = @($allCalls.Keys   | Where-Object { -not $allResults.ContainsKey($_) })
		Write-Host ("  统计: 共{0}条, calls={1}, results={2}" -f $msgList.Count, $allCalls.Count, $allResults.Count) -ForegroundColor Cyan
		if ($orphanResults.Count -gt 0) {
			Write-Host ("  ❌ 孤立 functionResult: {0}" -f ($orphanResults -join ', ')) -ForegroundColor Red
		}
		if ($unanswered.Count -gt 0) {
			Write-Host ("  ❌ 未回应 functionCall: {0}" -f ($unanswered -join ', ')) -ForegroundColor Red
		}
	}
}
finally {
	$conn.Close()
}
