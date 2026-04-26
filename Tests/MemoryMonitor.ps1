# Monitor Memory plugin database row count
param(
    [int]$IntervalSeconds = 1,
    [int]$DurationSeconds = 60
)

$dbPath = Join-Path $env:USERPROFILE ".cortana\plugins\memory_engine\data\memory.db"

# Try alternate path if not found
if (-not (Test-Path $dbPath)) {
    $dbPath = ".\bin\Debug\net10.0\.cortana\plugins\memory_engine\data\memory.db"
}

Write-Host "Monitor DB: $dbPath" -ForegroundColor Green
Write-Host "Duration: ${DurationSeconds}s, Interval: ${IntervalSeconds}s" -ForegroundColor Green
Write-Host ""

$startTime = Get-Date
$lastCount = 0

while (([datetime]::Now - $startTime).TotalSeconds -lt $DurationSeconds) {
    if (Test-Path $dbPath) {
        try {
            # Query row count
            $result = & sqlite3 $dbPath "SELECT COUNT(1) FROM observation_records" 2>$null
            $count = [int]$result
            $timestamp = (Get-Date).ToString("HH:mm:ss.fff")
            
            $delta = $count - $lastCount
            if ($delta -gt 0) {
                Write-Host "$timestamp | observation_records: $count [+$delta]" -ForegroundColor Green
            } else {
                Write-Host "$timestamp | observation_records: $count" -ForegroundColor Cyan
            }
            
            $lastCount = $count
        }
        catch {
            Write-Host "Query failed, retrying..." -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "DB not found: $dbPath" -ForegroundColor Red
        break
    }
    
    Start-Sleep -Seconds $IntervalSeconds
}

Write-Host ""
Write-Host "Monitor ended. Final count: $lastCount" -ForegroundColor Green
