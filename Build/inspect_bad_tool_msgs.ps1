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
	# 找出所有 role='tool' 但 ContentsJson 不含 functionResult 的消息
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT Id, SessionId, CreatedTimestamp,
	   length(IFNULL(Content,'')) AS ContentLen,
	   length(IFNULL(ContentsJson,'')) AS JsonLen,
	   IFNULL(Content,'') AS Content,
	   IFNULL(ContentsJson,'') AS Cj
FROM ChatMessages
WHERE Role = 'tool'
  AND (IFNULL(ContentsJson,'') = ''
	   OR ContentsJson NOT LIKE '%functionResult%')
ORDER BY CreatedTimestamp DESC
LIMIT 20
"@
	$r = $cmd.ExecuteReader()
	$cnt = 0
	while ($r.Read()) {
		$cnt++
		$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$r['CreatedTimestamp']).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss.fff')
		Write-Host ("[{0:00}] {1} sess={2} id={3}" -f $cnt, $tsStr, [string]$r['SessionId'], [string]$r['Id']) -ForegroundColor Yellow
		Write-Host ("     ContentLen={0}  JsonLen={1}" -f $r['ContentLen'], $r['JsonLen']) -ForegroundColor Cyan
		$content = [string]$r['Content']
		$cj = [string]$r['Cj']
		if ($content.Length -gt 0) {
			$s = if ($content.Length -gt 200) { $content.Substring(0,200) + '...' } else { $content }
			Write-Host ("     Content   : {0}" -f $s) -ForegroundColor White
		}
		if ($cj.Length -gt 0) {
			$s = if ($cj.Length -gt 300) { $cj.Substring(0,300) + '...' } else { $cj }
			Write-Host ("     ContentsJson: {0}" -f $s) -ForegroundColor DarkGray
		} else {
			Write-Host "     ContentsJson: <空>" -ForegroundColor DarkRed
		}
	}
	$r.Close()
	Write-Host "`n总计：$cnt 条 tool 消息没有 functionResult 标记" -ForegroundColor Magenta

	# 统计：全库范围内 tool 消息总数 vs 异常数
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = "SELECT COUNT(*) FROM ChatMessages WHERE Role='tool'"
	$allTool = $cmd.ExecuteScalar()
	$cmd.CommandText = "SELECT COUNT(*) FROM ChatMessages WHERE Role='tool' AND (IFNULL(ContentsJson,'')='' OR ContentsJson NOT LIKE '%functionResult%')"
	$badTool = $cmd.ExecuteScalar()
	Write-Host "全库 tool 消息: $allTool 条; 其中无 functionResult 的: $badTool 条 ($([math]::Round(100.0*$badTool/[math]::Max($allTool,1),1))%)" -ForegroundColor Magenta
}
finally {
	$conn.Close()
}
