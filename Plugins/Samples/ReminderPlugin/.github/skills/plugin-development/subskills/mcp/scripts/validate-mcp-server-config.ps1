param(
    [Parameter(Mandatory = $true)][ValidateSet('stdio', 'sse', 'streamable-http')][string]$TransportType,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Command,
    [string[]]$Arguments = @(),
    [string]$Url,
    [string]$ApiKey,
    [hashtable]$EnvironmentVariables = @{}
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Name)) {
    throw 'Name 不能为空。'
}

switch ($TransportType) {
    'stdio' {
        if ([string]::IsNullOrWhiteSpace($Command)) {
            throw 'stdio 模式必须提供 Command。'
        }
    }
    'sse' {
        if ([string]::IsNullOrWhiteSpace($Url)) {
            throw 'sse 模式必须提供 Url。'
        }
    }
    'streamable-http' {
        if ([string]::IsNullOrWhiteSpace($Url)) {
            throw 'streamable-http 模式必须提供 Url。'
        }
    }
}

$summary = [ordered]@{
    Name = $Name
    TransportType = $TransportType
    Command = $Command
    Arguments = $Arguments
    Url = $Url
    HasApiKey = -not [string]::IsNullOrWhiteSpace($ApiKey)
    EnvironmentVariableKeys = @($EnvironmentVariables.Keys)
}

$summary | ConvertTo-Json -Depth 4 | Write-Output