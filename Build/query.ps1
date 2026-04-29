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
    $command.CommandText = 'SELECT id, SessionId, Role, Content, CreatedAt FROM ChatMessageEntity ORDER BY CreatedAt DESC LIMIT 10'
    $reader = $command.ExecuteReader()
    while ($reader.Read()) {
        $content = $reader['Content'].ToString()
        $snippet = $content.Substring(0, [Math]::Min($content.Length, 50))
        Write-Output "ID: $($reader['Id']) | Session: $($reader['SessionId']) | Role: $($reader['Role']) | Content: $snippet | Time: $($reader['CreatedAt'])"
    }
    $reader.Close()
}
finally {
    $connection.Close()
}
