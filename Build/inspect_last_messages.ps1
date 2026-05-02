$ErrorActionPreference = 'Stop'

# 加载 SQLite
$binDir = 'E:\Netor.me\Cortana\artifacts\DbInspect\bin\Release\net10.0'
$sqliteDll = Join-Path $binDir 'Microsoft.Data.Sqlite.dll'
$nativeDir = Join-Path $binDir 'runtimes\win-x64\native'

# 把 native dir 加入 PATH，让 e_sqlite3.dll 能被加载
$env:PATH = $nativeDir + ';' + $env:PATH

# 加载托管依赖
Get-ChildItem -Path $binDir -Filter '*.dll' | ForEach-Object {
	try { Add-Type -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
}
Add-Type -Path $sqliteDll

# 初始化 SQLitePCL 提供器
[SQLitePCL.Batteries_V2]::Init()

$dbPath = 'D:\Contrna\cortana.db'
$cs = "Data Source=$dbPath;Mode=ReadOnly"
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection($cs)
$conn.Open()
try {
	# 1. 表结构
	Write-Host "==== ChatMessages 表结构 ====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = "PRAGMA table_info(ChatMessages);"
	$r = $cmd.ExecuteReader()
	while ($r.Read()) {
		Write-Host ("  {0}: {1} (notnull={2}, pk={3})" -f $r['name'], $r['type'], $r['notnull'], $r['pk'])
	}
	$r.Close()

	# 2. 总条数
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = "SELECT COUNT(*) FROM ChatMessages"
	$total = $cmd.ExecuteScalar()
	Write-Host "`n==== 总消息数: $total ====" -ForegroundColor Cyan

	# 3. 最后 25 条（按 CreatedTimestamp 倒序）
	Write-Host "`n==== 最后 25 条消息（CreatedTimestamp DESC） ====" -ForegroundColor Cyan
	$cmd = $conn.CreateCommand()
	$cmd.CommandText = @"
SELECT Id, SessionId, Role, AuthorName, CreatedTimestamp, UpdatedTimestamp,
	   substr(IFNULL(Content,''), 1, 80) AS ContentSnippet,
	   substr(IFNULL(ContentsJson,''), 1, 200) AS JsonSnippet,
	   length(IFNULL(ContentsJson,'')) AS JsonLen,
	   ModelName
FROM ChatMessages
ORDER BY CreatedTimestamp DESC, rowid DESC
LIMIT 25
"@
	$r = $cmd.ExecuteReader()
	$rows = @()
	while ($r.Read()) {
		$rows += [pscustomobject]@{
			Id = $r['Id']
			SessionId = $r['SessionId']
			Role = $r['Role']
			CreatedTs = $r['CreatedTimestamp']
			UpdatedTs = $r['UpdatedTimestamp']
			ContentSnippet = $r['ContentSnippet']
			JsonLen = $r['JsonLen']
			JsonSnippet = $r['JsonSnippet']
			Model = $r['ModelName']
		}
	}
	$r.Close()
	# 反转为时间正序，便于看链条
	[array]::Reverse($rows)
	$i = 0
	foreach ($row in $rows) {
		$i++
		$tsStr = ''
		try {
			$tsStr = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$row.CreatedTs).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss.fff')
		} catch { $tsStr = "$($row.CreatedTs)" }
		Write-Host ("[{0:00}] {1} | {2} | sess={3} | id={4} | jsonLen={5}" -f $i, $tsStr, $row.Role, $row.SessionId.ToString().Substring(0,[Math]::Min(8,$row.SessionId.ToString().Length)), $row.Id.ToString().Substring(0,[Math]::Min(8,$row.Id.ToString().Length)), $row.JsonLen) -ForegroundColor Yellow
		Write-Host ("     content: {0}" -f $row.ContentSnippet)
		if ($row.JsonLen -gt 0) {
			Write-Host ("     json   : {0}" -f $row.JsonSnippet) -ForegroundColor DarkGray
		}
	}
}
finally {
	$conn.Close()
}
