param(
    [string]$InventoryPath,
    [string]$Action = "status"
)

# Load inventory
if (-not (Test-Path $InventoryPath)) {
    Write-Error "Inventory file not found: $InventoryPath"
    exit 1
}

$servers = Get-Content $InventoryPath | ConvertFrom-Json
$results = @()

foreach ($srv in $servers) {
    Write-Host "Processing: $($srv.name) ($($srv.ip))..."
    
    $result = @{
        server_ip = $srv.ip
        name = $srv.name
        status = "pending"
        data = @{}
        error = $null
    }

    # Validate SSH key
    $keyPath = Join-Path (Split-Path $InventoryPath -Parent) $srv.ssh_key_path
    if (-not (Test-Path $keyPath)) {
        $result.status = "error"
        $result.error = "SSH key not found: $($srv.ssh_key_path)"
        $results += $result
        continue
    }

    try {
        if ($Action -eq "status") {
            # Dispatch to OS health check
            $scriptPath = Join-Path $PSScriptRoot "check_health.ps1"
            $output = & $scriptPath -Ip $srv.ip -User $srv.ssh_user -KeyPath $keyPath
            $result.data = $output | ConvertFrom-Json
            $result.status = "success"
        }
        elseif ($Action -eq "baota") {
            if ([string]::IsNullOrWhiteSpace($srv.baota_api_sk)) {
                $result.status = "skipped"
                $result.error = "No Baota panel configured"
            } else {
                # Dispatch to Baota sync
                $scriptPath = Join-Path $PSScriptRoot "baota_sync.ps1"
                $output = & $scriptPath -PanelUrl $srv.baota_panel_url -ApiSk $srv.baota_api_sk
                $result.data = $output | ConvertFrom-Json
                $result.status = "success"
            }
        }
    }
    catch {
        $result.status = "error"
        $result.error = $_.Exception.Message
    }

    $results += $result
}

# Output aggregated results
$results | ConvertTo-Json -Depth 3
