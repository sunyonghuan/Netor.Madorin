# ============================================================================
# backfill_message_agent.ps1
# ----------------------------------------------------------------------------
# 把 ChatMessages 表里历史上没填 AgentId / AgentName 的记录补齐。
# 策略：
#   1. 优先按 SessionId -> ChatSessions.AgentId -> Agents.Name 反查（最准确）；
#   2. 经第 1 步仍空的记录，按用户要求"随机抽一个智能体"作为兜底；
#   3. 已经有 AgentId（IFNULL(AgentId,'') <> ''）的消息一律跳过；
#   4. 默认是 DryRun，加 -Apply 才真正写库；
#   5. 写入前会自动备份数据库。
# ----------------------------------------------------------------------------
# 用法：
#   pwsh -NoProfile -ExecutionPolicy Bypass -File Build\backfill_message_agent.ps1
#   pwsh -NoProfile -ExecutionPolicy Bypass -File Build\backfill_message_agent.ps1 -Apply
# ============================================================================

[CmdletBinding()]
param(
	[string]$DbPath = 'D:\Contrna\cortana.db',
	[switch]$Apply
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $DbPath)) {
	throw "数据库文件不存在: $DbPath"
}

# ---------- 加载 Microsoft.Data.Sqlite ----------
$sqliteDll = 'E:\Netor.me\Cortana\artifacts\DbInspect\bin\Release\net10.0\Microsoft.Data.Sqlite.dll'
if (-not (Test-Path $sqliteDll)) {
	throw "找不到 Microsoft.Data.Sqlite.dll：$sqliteDll，请先 Release 构建一次解决方案。"
}
$nativeDir = Split-Path $sqliteDll -Parent
if ($env:PATH -notlike "*$nativeDir*") {
	$env:PATH = "$nativeDir;$env:PATH"
}
Add-Type -Path $sqliteDll

# ---------- 备份 ----------
if ($Apply) {
	$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
	$backup = "$DbPath.bak.$stamp"
	Copy-Item $DbPath $backup -Force
	Write-Host "[备份] $backup" -ForegroundColor Cyan
}

$cs = "Data Source=$DbPath"
$conn = [Microsoft.Data.Sqlite.SqliteConnection]::new($cs)
$conn.Open()

function Invoke-Scalar {
	param([Microsoft.Data.Sqlite.SqliteConnection]$Conn, [string]$Sql)
	$cmd = $Conn.CreateCommand()
	$cmd.CommandText = $Sql
	$r = $cmd.ExecuteScalar()
	if ($null -eq $r -or $r -is [System.DBNull]) { return $null }
	return $r
}

function Invoke-Reader {
	param([Microsoft.Data.Sqlite.SqliteConnection]$Conn, [string]$Sql)
	$cmd = $Conn.CreateCommand()
	$cmd.CommandText = $Sql
	# 用 , 阻止 PowerShell 把 reader 当集合展开
	return ,$cmd.ExecuteReader()
}

try {
	# ---------- 0. 校验列存在（应已被代码迁移自动建好）----------
	$hasAgentId = $false
	$hasAgentName = $false
	$reader = Invoke-Reader -Conn $conn -Sql "PRAGMA table_info(ChatMessages);"
	while ($reader.Read()) {
		$col = $reader.GetString(1)
		if ($col -eq 'AgentId') { $hasAgentId = $true }
		if ($col -eq 'AgentName') { $hasAgentName = $true }
	}
	$reader.Close()

	if (-not $hasAgentId -or -not $hasAgentName) {
		Write-Host "[警告] ChatMessages 表缺少 AgentId / AgentName 列。请先启动一次应用让代码自动迁移，或手工执行：" -ForegroundColor Yellow
		Write-Host "       ALTER TABLE ChatMessages ADD COLUMN AgentId TEXT NOT NULL DEFAULT '';" -ForegroundColor Yellow
		Write-Host "       ALTER TABLE ChatMessages ADD COLUMN AgentName TEXT NOT NULL DEFAULT '';" -ForegroundColor Yellow
		return
	}

	# ---------- 1. 统计 ----------
	$totalMsg     = [int64](Invoke-Scalar -Conn $conn -Sql "SELECT COUNT(*) FROM ChatMessages;")
	$needBackfill = [int64](Invoke-Scalar -Conn $conn -Sql "SELECT COUNT(*) FROM ChatMessages WHERE IFNULL(AgentId,'') = '';")
	$alreadyOk    = $totalMsg - $needBackfill

	Write-Host ""
	Write-Host "===== 状态 =====" -ForegroundColor Green
	Write-Host "总消息数        : $totalMsg"
	Write-Host "已经有 AgentId  : $alreadyOk (跳过)"
	Write-Host "需要回填        : $needBackfill"

	if ($needBackfill -eq 0) {
		Write-Host "无需回填，退出。" -ForegroundColor Green
		return
	}

	# ---------- 2. 拉取所有可用智能体作为兜底候选 ----------
	$agents = @()
	$reader = Invoke-Reader -Conn $conn -Sql "SELECT Id, Name FROM Agents;"
	while ($reader.Read()) {
		$agents += [pscustomobject]@{
			Id   = $reader.GetString(0)
			Name = $reader.GetString(1)
		}
	}
	$reader.Close()

	if ($agents.Count -eq 0) {
		Write-Host "[错误] Agents 表为空，无法做兜底回填。" -ForegroundColor Red
		return
	}

	Write-Host "可用智能体共 $($agents.Count) 个：" -ForegroundColor Cyan
	foreach ($a in $agents) {
		Write-Host "  - $($a.Id.Substring(0, [Math]::Min(8, $a.Id.Length))) | $($a.Name)"
	}

	# ---------- 3. 第一遍：按 Session 反查 ----------
	Write-Host ""
	Write-Host "===== 第一遍：按 SessionId 反查 ChatSessions.AgentId =====" -ForegroundColor Green

	$resolveSql = @"
SELECT COUNT(*) FROM ChatMessages m
WHERE IFNULL(m.AgentId,'') = ''
  AND EXISTS (
	SELECT 1 FROM ChatSessions s
	JOIN Agents a ON a.Id = s.AgentId
	WHERE s.Id = m.SessionId
	  AND IFNULL(s.AgentId,'') <> ''
  );
"@
	$resolvableBySession = [int64](Invoke-Scalar -Conn $conn -Sql $resolveSql)
	Write-Host "可由 Session 解析的消息：$resolvableBySession"

	$remainAfterSessionSql = "SELECT COUNT(*) FROM ChatMessages WHERE IFNULL(AgentId,'') = '' AND NOT EXISTS (SELECT 1 FROM ChatSessions s JOIN Agents a ON a.Id=s.AgentId WHERE s.Id = ChatMessages.SessionId AND IFNULL(s.AgentId,'') <> '');"
	$remainAfterSession = [int64](Invoke-Scalar -Conn $conn -Sql $remainAfterSessionSql)
	Write-Host "Session 解析后仍空（将走随机兜底）：$remainAfterSession"

	if (-not $Apply) {
		Write-Host ""
		Write-Host "[DryRun] 未加 -Apply，未执行任何 UPDATE。" -ForegroundColor Yellow
		return
	}

	$tx = $conn.BeginTransaction()
	try {
		# 3.1 用 Session 反查回填
		$updateBySession = @"
UPDATE ChatMessages
SET AgentId   = (SELECT s.AgentId FROM ChatSessions s WHERE s.Id = ChatMessages.SessionId),
	AgentName = (SELECT a.Name FROM ChatSessions s JOIN Agents a ON a.Id = s.AgentId WHERE s.Id = ChatMessages.SessionId)
WHERE IFNULL(ChatMessages.AgentId, '') = ''
  AND EXISTS (
	SELECT 1 FROM ChatSessions s2
	JOIN Agents a2 ON a2.Id = s2.AgentId
	WHERE s2.Id = ChatMessages.SessionId
	  AND IFNULL(s2.AgentId, '') <> ''
  );
"@
		$cmd = $conn.CreateCommand()
		$cmd.Transaction = $tx
		$cmd.CommandText = $updateBySession
		$rows1 = $cmd.ExecuteNonQuery()
		Write-Host "  [Session 回填] 更新 $rows1 行" -ForegroundColor Green

		# 3.2 兜底：每条消息独立随机抽一个智能体
		Write-Host "  [兜底策略] 每条消息独立随机抽取" -ForegroundColor Cyan

		# 先把所有还为空的消息 rowid 取出来
		$emptyIds = New-Object System.Collections.Generic.List[int64]
		$cmdSel = $conn.CreateCommand()
		$cmdSel.Transaction = $tx
		$cmdSel.CommandText = "SELECT rowid FROM ChatMessages WHERE IFNULL(AgentId,'') = '';"
		$rd = $cmdSel.ExecuteReader()
		try {
			while ($rd.Read()) { [void]$emptyIds.Add($rd.GetInt64(0)) }
		} finally { $rd.Close() }

		# 预编译 UPDATE
		$cmd2 = $conn.CreateCommand()
		$cmd2.Transaction = $tx
		$cmd2.CommandText = "UPDATE ChatMessages SET AgentId = @AId, AgentName = @AName WHERE rowid = @Rid;"
		$pAId   = $cmd2.Parameters.Add("@AId",   [Microsoft.Data.Sqlite.SqliteType]::Text)
		$pAName = $cmd2.Parameters.Add("@AName", [Microsoft.Data.Sqlite.SqliteType]::Text)
		$pRid   = $cmd2.Parameters.Add("@Rid",   [Microsoft.Data.Sqlite.SqliteType]::Integer)

		# 统计每个智能体被抽中的次数
		$stats = @{}
		foreach ($a in $agents) { $stats[$a.Id] = 0 }

		$rows2 = 0
		$rng = [System.Random]::new()
		foreach ($rid in $emptyIds) {
			$pick = $agents[$rng.Next(0, $agents.Count)]
			$pAId.Value   = $pick.Id
			$pAName.Value = $pick.Name
			$pRid.Value   = $rid
			$rows2 += $cmd2.ExecuteNonQuery()
			$stats[$pick.Id] = $stats[$pick.Id] + 1

			if (($rows2 % 1000) -eq 0) {
				Write-Host "    已回填 $rows2 行..." -ForegroundColor DarkGray
			}
		}
		Write-Host "  [随机兜底] 更新 $rows2 行" -ForegroundColor Green
		Write-Host "  [分布]" -ForegroundColor Cyan
		foreach ($a in $agents) {
			Write-Host ("    {0} | {1}  ->  {2}" -f $a.Id.Substring(0,[Math]::Min(8,$a.Id.Length)), $a.Name, $stats[$a.Id])
		}

		$tx.Commit()
		Write-Host ""
		Write-Host "===== 完成 =====" -ForegroundColor Green
		Write-Host "Session 反查回填: $rows1"
		Write-Host "随机兜底回填    : $rows2"
		Write-Host "总计            : $($rows1 + $rows2)"
	}
	catch {
		$tx.Rollback()
		throw
	}

	# ---------- 4. 复核 ----------
	$stillEmpty = [int64](Invoke-Scalar -Conn $conn -Sql "SELECT COUNT(*) FROM ChatMessages WHERE IFNULL(AgentId,'') = '';")
	Write-Host "回填后仍为空的消息：$stillEmpty"
}
finally {
	$conn.Close()
	$conn.Dispose()
}
