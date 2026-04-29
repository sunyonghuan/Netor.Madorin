$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
Set-Location $SolutionDir

$sqliteDll = Get-ChildItem -Recurse -Path $SolutionDir -Filter Microsoft.Data.Sqlite.dll |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $sqliteDll) {
    throw 'Microsoft.Data.Sqlite.dll was not found. Build related projects first.'
}

Add-Type -Path $sqliteDll

$dbPath = Join-Path $SolutionDir 'cortana.db'
$connection = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath")
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = 'PRAGMA table_info(ChatMessageEntity);'
    $reader = $command.ExecuteReader()
    Write-Host '--- Schema of ChatMessageEntity ---'
    while ($reader.Read()) {
        Write-Host "$($reader['name']) ($($reader['type']))"
    }
    $reader.Close()

    $command.CommandText = 'SELECT Id, SessionId, Role, Content, CreatedAt FROM ChatMessageEntity ORDER BY CreatedAt DESC LIMIT 10'
    $reader = $command.ExecuteReader()
    Write-Host '--- Latest 10 Messages ---'
    while ($reader.Read()) {
        $content = $reader['Content'].ToString()
        $snippet = if ($content.Length -gt 50) { $content.Substring(0, 50) + '...' } else { $content }
        Write-Host "ID: $($reader['Id']) | S_ID: $($reader['SessionId']) | Role: $($reader['Role']) | Time: $($reader['CreatedAt'])"
        Write-Host "Content: $snippet"
        Write-Host '----------------'
    }
    $reader.Close()
}
finally {
    $connection.Close()
}
