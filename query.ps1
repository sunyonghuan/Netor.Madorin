Add-Type -Path (Get-ChildItem -Recurse -Filter Microsoft.Data.Sqlite.dll | Select-Object -First 1 -ExpandProperty FullName)
$connection = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=cortana.db")
$connection.Open()
$command = $connection.CreateCommand()
$command.CommandText = "SELECT id, SessionId, Role, Content, CreatedAt FROM ChatMessageEntity ORDER BY CreatedAt DESC LIMIT 10"
$reader = $command.ExecuteReader()
while ($reader.Read()) {
    Write-Output "ID: $($reader['Id']) | Session: $($reader['SessionId']) | Role: $($reader['Role']) | Content: $($reader['Content'].ToString().Substring(0, [Math]::Min($reader['Content'].ToString().Length, 50))) | Time: $($reader['CreatedAt'])"
}
$connection.Close()
