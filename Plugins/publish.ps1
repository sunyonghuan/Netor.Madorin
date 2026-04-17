# publish.ps1 - 插件批量发布脚本
# 自动递增版本号 -> 修改 csproj + Startup.cs -> dotnet publish -> 打包 zip
param(
    [switch]$DryRun,
    [ValidateSet("patch", "minor", "major")]
    [string]$Bump = "patch",
    [string]$Runtime = "win-x64"
)

[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$SrcDir = Join-Path $Root "Src"
$ReleasesDir = Join-Path $Root "Releases"

if (-not (Test-Path $SrcDir)) {
    Write-Host "  [ERROR] Src 目录不存在: $SrcDir" -ForegroundColor Red
    exit 1
}

# 确保 Releases 目录存在
if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
}

# -- 版本号递增函数 --
function Step-Version([string]$ver, [string]$part) {
    $parts = $ver.Split('.')
    if ($parts.Length -lt 3) { $parts = @("1", "0", "0") }
    $major = [int]$parts[0]; $minor = [int]$parts[1]; $patch = [int]$parts[2]
    switch ($part) {
        "major" { $major++; $minor = 0; $patch = 0 }
        "minor" { $minor++; $patch = 0 }
        "patch" { $patch++ }
    }
    return "$major.$minor.$patch"
}

# -- 从 csproj 提取当前版本 --
function Get-CsprojVersion([string]$csprojPath) {
    $xml = [xml]([System.IO.File]::ReadAllText($csprojPath))
    $node = $xml.SelectSingleNode("//PropertyGroup/Version")
    if ($node) { return $node.InnerText }
    return "0.0.0"
}

# -- 更新 csproj 版本 --
function Set-CsprojVersion([string]$csprojPath, [string]$newVer) {
    $content = [System.IO.File]::ReadAllText($csprojPath)
    $content = $content -replace '<Version>[^<]+</Version>', "<Version>$newVer</Version>"
    [System.IO.File]::WriteAllText($csprojPath, $content, [System.Text.Encoding]::UTF8)
}

# -- 更新 Startup.cs 中 [Plugin(Version = "x.y.z")] --
function Set-StartupVersion([string]$projectDir, [string]$newVer) {
    $startupFiles = Get-ChildItem -Path $projectDir -Filter "Startup.cs" -Recurse
    foreach ($f in $startupFiles) {
        $content = [System.IO.File]::ReadAllText($f.FullName)
        if ($content -match 'Version\s*=\s*"[^"]*"') {
            $content = $content -replace 'Version\s*=\s*"[^"]*"', "Version = `"$newVer`""
            [System.IO.File]::WriteAllText($f.FullName, $content, [System.Text.Encoding]::UTF8)
            Write-Host "    Startup.cs Version -> $newVer" -ForegroundColor DarkGray
        }
    }
}

# ================================
# 主流程：遍历 Src 下的每个插件项目
# ================================
Write-Host ""
Write-Host "  Cortana Plugin Publisher" -ForegroundColor Cyan
Write-Host "  Bump: $Bump | Runtime: $Runtime | DryRun: $DryRun" -ForegroundColor DarkGray
Write-Host ""

$projects = Get-ChildItem -Path $SrcDir -Directory
$successCount = 0

foreach ($proj in $projects) {
    $csproj = Get-ChildItem -Path $proj.FullName -Filter "*.csproj" | Select-Object -First 1
    if (-not $csproj) {
        Write-Host "  [SKIP] $($proj.Name) - 无 csproj 文件" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  -- $($proj.Name) --" -ForegroundColor Yellow

    # 1. 读取并递增版本
    $oldVer = Get-CsprojVersion $csproj.FullName
    $newVer = Step-Version $oldVer $Bump
    Write-Host "    版本: $oldVer -> $newVer" -ForegroundColor White

    if ($DryRun) {
        Write-Host "    [DRY-RUN] 跳过实际操作" -ForegroundColor DarkYellow
        continue
    }

    # 2. 更新版本号
    Set-CsprojVersion $csproj.FullName $newVer
    Set-StartupVersion $proj.FullName $newVer

    # 3. 提取友好名称（去掉 Cortana.Plugins. 前缀）
    $friendlyName = $proj.Name -replace '^Cortana\.Plugins\.', ''

    # 4. dotnet publish
    $publishDir = Join-Path $proj.FullName "bin\publish"
    Write-Host "    发布中..." -ForegroundColor DarkGray

    $publishOutput = dotnet publish $csproj.FullName `
        -c Release `
        -r $Runtime `
        -o $publishDir `
        2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host "    [FAILED] dotnet publish 失败" -ForegroundColor Red
        Write-Host $publishOutput -ForegroundColor DarkRed
        continue
    }

    # 5. 收集发布文件（DLL + deps.json + plugin.json 等，排除 pdb）
    $stageDir = Join-Path ([System.IO.Path]::GetTempPath()) "cortana_publish_$friendlyName"
    $pluginFolder = Join-Path $stageDir $friendlyName
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $pluginFolder | Out-Null

    Get-ChildItem -Path $publishDir -File | Where-Object {
        $_.Extension -ne ".pdb"
    } | ForEach-Object {
        Copy-Item $_.FullName -Destination $pluginFolder
    }

    # 6. 打包 zip
    $zipName = "$friendlyName.v$newVer.zip"
    $zipPath = Join-Path $ReleasesDir $zipName

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $pluginFolder -DestinationPath $zipPath -CompressionLevel Optimal

    # 7. 清理临时目录
    Remove-Item $stageDir -Recurse -Force
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

    $zipSize = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
    Write-Host "    OK $zipName ($($zipSize) KB)" -ForegroundColor Green
    $successCount++
}

Write-Host ""
Write-Host "  完成: $successCount/$($projects.Count) 个插件已发布到 Releases/" -ForegroundColor Cyan
Write-Host ""
