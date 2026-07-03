Set-StrictMode -Version Latest

function Get-ServerMaintainerWorkspaceRoot {
    param([string]$ScriptRoot)
    return "E:\Workspace\ServerMaintainer"
}

function Get-ServersRoot {
    param([string]$ScriptRoot)
    return (Join-Path (Get-ServerMaintainerWorkspaceRoot -ScriptRoot $ScriptRoot) 'Servers')
}

function Get-ReportsRoot {
    param([string]$ScriptRoot)
    return (Join-Path (Get-ServerMaintainerWorkspaceRoot -ScriptRoot $ScriptRoot) 'Reports')
}

function Get-ServerDirectory {
    param([string]$ScriptRoot, [string]$ServerId)
    $serversRoot = Get-ServersRoot -ScriptRoot $ScriptRoot
    $directPath = Join-Path $serversRoot $ServerId
    if (Test-Path $directPath) { return $directPath }
    
    $directories = Get-ChildItem -Path $serversRoot -Directory -ErrorAction SilentlyContinue
    foreach ($directory in $directories) {
        $serverFile = Join-Path $directory.FullName 'server.md'
        if (-not (Test-Path $serverFile)) { continue }
        $server = Read-ServerRecord -ServerFile $serverFile
        if ($server.Name -eq $ServerId -or $server.IP -eq $ServerId) { return $directory.FullName }
    }
    return $null
}

function Read-ServerRecord {
    param([string]$ServerFile)

    $content = Get-Content $ServerFile -Raw -Encoding UTF8
    $name = ''
    $ip = ''
    $port = 22
    $username = 'root'
    
    # 1. 解析服务器名称
    # 格式1: # 服务器信息档案 - hz.a.cn
    if ($content -match '(?m)^#\s+.*?-\s*(.+)$') { 
        $name = $matches[1].Trim() 
    }
    # 格式2: **服务器名称**: site
    if ($content -match '(?m)^\*\*(服务器名称|Server Name)\*\*:\s*(.+)$') { 
        $name = $matches[2].Trim() 
    }
    # 格式3: | **服务器名称** | hz.a.cn | (表格)
    if ($content -match '(?m)^\|\s*\*\*(服务器名称|Server Name)\s*\*\*\s*\|\s*(.+?)\s*\|') { 
        $name = $matches[2].Trim() 
    }
    
    # 2. 解析 IP 地址
    # 格式1: **IP 地址**: 10.10.10.1
    if ($content -match '(?m)^\*\*(IP 地址|IP Address)\*\*:\s*(.+)$') { 
        $ip = $matches[2].Trim() 
    }
    # 格式2: | **IP 地址** | 10.10.10.120 | (表格)
    if ($content -match '(?m)^\|\s*\*\*(IP 地址|IP Address)\s*\*\*\s*\|\s*(.+?)\s*\|') { 
        $ip = $matches[2].Trim() 
    }
    # 格式3: | **IP 地址 (业务)** | 10.10.10.2 | (表格)
    if ($content -match '(?m)^\|\s*\*\*(IP 地址 \(业务\)|IP Address \(Business\))\s*\*\*\s*\|\s*(.+?)\s*\|') { 
        $ip = $matches[2].Trim() 
    }
    
    # 3. 解析端口
    if ($content -match '(?m)^\*\*(端口|Port)\*\*:\s*(\d+)$') { 
        $port = [int]$matches[2] 
    }
    
    # 4. 解析用户名
    if ($content -match '(?m)^\*\*(用户名|Username)\*\*:\s*(.+)$') { 
        $username = $matches[2].Trim() 
    }
    if ($content -match '(?m)^\|\s*\*\*(用户名|Username)\s*\*\*\s*\|\s*(.+?)\s*\|') { 
        $username = $matches[2].Trim() 
    }

    # 如果名称为空，使用目录名
    if (-not $name) {
        $name = Split-Path (Split-Path $ServerFile -Parent) -Leaf
    }
    
    # 如果 IP 为空，也使用目录名（可能是 IP 格式）
    if (-not $ip) {
        $ip = Split-Path (Split-Path $ServerFile -Parent) -Leaf
    }

    return [PSCustomObject]@{
        Name = $name
        IP = $ip
        Port = $port
        Username = $username
        ServerFile = $ServerFile
        ServerDirectory = Split-Path $ServerFile -Parent
        KeyPath = Join-Path (Split-Path $ServerFile -Parent) 'id_rsa'
    }
}

function Test-ServerKey {
    param([pscustomobject]$ServerRecord)
    return (Test-Path $ServerRecord.KeyPath)
}