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
	# 1. 检查 ec7634a2 内每条 tool 消息的真实存储情况
	Write-Host "==== Session ec7634a2 中所有 tool 消息的存储详情 ====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT Id, CreatedTimestamp, AuthorName,
	   length(IFNULL(Content,'')) AS Cl,
	   length(IFNULL(ContentsJson,'')) AS Jl,
	   IFNULL(Content,'') AS Content,
	   IFNULL(ContentsJson,'') AS Cj
FROM ChatMessages
WHERE SessionId LIKE 'ec7634a2%' AND Role='tool'
ORDER BY CreatedTimestamp ASC
"@
	$r = $cmd.ExecuteReader()
	$idx = 0
	while ($r.Read()) {
		$idx++
		$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$r['CreatedTimestamp']).ToLocalTime().ToString('HH:mm:ss.fff')
		$cl = [int]$r['Cl']
		$jl = [int]$r['Jl']
		$tag = if ($jl -eq 0 -and $cl -eq 0) { '❌空' } elseif ($jl -eq 0) { '⚠️无JSON' } else { '✓' }
		Write-Host ("[{0:00}] {1} id={2} author='{3}' contentLen={4} jsonLen={5} {6}" -f `
			$idx, $tsStr, [string]$r['Id'], [string]$r['AuthorName'], $cl, $jl, $tag) -ForegroundColor Yellow
		if ($cl -gt 0) {
			$c = [string]$r['Content']
			$s = if ($c.Length -gt 150) { $c.Substring(0,150) + '...' } else { $c }
			Write-Host ("     content : {0}" -f $s) -ForegroundColor Gray
		}
		if ($jl -gt 0) {
			$c = [string]$r['Cj']
			$s = if ($c.Length -gt 200) { $c.Substring(0,200) + '...' } else { $c }
			Write-Host ("     json    : {0}" -f $s) -ForegroundColor DarkGray
		}
	}
	$r.Close()

	# 2. 按时间分布看一下空 tool 消息的趋势
	Write-Host "`n==== 空 tool 消息时间分布（按月）====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT strftime('%Y-%m', datetime(CreatedTimestamp/1000, 'unixepoch')) AS Mon,
	   SUM(CASE WHEN IFNULL(ContentsJson,'')='' AND IFNULL(Content,'')='' THEN 1 ELSE 0 END) AS EmptyCount,
	   COUNT(*) AS TotalCount
FROM ChatMessages
WHERE Role='tool'
GROUP BY Mon
ORDER BY Mon
"@
	$r = $cmd.ExecuteReader()
	while ($r.Read()) {
		Write-Host ("  {0}: 空 {1} / 总 {2}" -f [string]$r['Mon'], $r['EmptyCount'], $r['TotalCount'])
	}
	$r.Close()

	# 3. 检查 assistant 消息中带有 functionCall 但对应 functionResult 缺失的最近样本
	Write-Host "`n==== 最近的孤立 functionCall（没有 functionResult 对应）会话样本 ====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT SessionId, COUNT(*) AS C, MAX(CreatedTimestamp) AS LastTs
FROM ChatMessages
WHERE Role='assistant' AND ContentsJson LIKE '%functionCall%'
GROUP BY SessionId
ORDER BY LastTs DESC
LIMIT 5
"@
	$r = $cmd.ExecuteReader()
	while ($r.Read()) {
		$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$r['LastTs']).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
		Write-Host ("  sess={0} 含 functionCall 的 assistant 消息数={1} 最近={2}" -f [string]$r['SessionId'], $r['C'], $tsStr)
	}
	$r.Close()
}
finally {
	$conn.Close()
}
