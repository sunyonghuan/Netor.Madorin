<#
.SYNOPSIS
    一键发布 Netor.Cortana 到 Realases\Cortana 目录。
.DESCRIPTION
    完整发布流程：
    1. 发布 Cortana 主程序（单文件 + SelfContained + ReadyToRun）
    2. 发布 NativeHost 子进程（AOT 原生 EXE）并复制到 Release 目录
    3. 发布 NativeTestPlugin（AOT 原生 DLL）并部署到插件目录

    等效于 VS 中使用 FolderProfile 发布，但额外确保 NativeHost 为 AOT 版本。
.PARAMETER SkipNativePlugin
    跳过 NativeTestPlugin 的发布部署。
.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SkipNativePlugin
#>
param(
    [switch]$SkipNativePlugin
)

$ErrorActionPreference = 'Stop'
$SolutionDir = $PSScriptRoot
$ReleaseDir = Join-Path $SolutionDir 'Realases\Cortana'

$CortanaProj = Join-Path $SolutionDir 'Src\Netor.Cortana.AvaloniaUI\Netor.Cortana.AvaloniaUI.csproj'
$NativeHostProj = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost\Netor.Cortana.NativeHost.csproj'
$NativePluginProj = Join-Path $SolutionDir 'Samples\NativeTestPlugin\NativeTestPlugin.csproj'

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Netor.Cortana 一键发布" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "解决方案目录: $SolutionDir"
Write-Host "发布目录:     $ReleaseDir"
Write-Host ""

# ──────── 1. 发布 Cortana 主程序 ────────
Write-Host "[1/3] 发布 Cortana 主程序（SingleFile + SelfContained + ReadyToRun）..." -ForegroundColor Yellow
dotnet publish $CortanaProj -c Release -p:PublishProfile=FolderProfile --nologo
if ($LASTEXITCODE -ne 0) { throw "Cortana 主程序发布失败" }

$cortanaPublishDir = Join-Path (Split-Path $CortanaProj -Parent) 'bin\publish'
if (-not (Test-Path $cortanaPublishDir)) {
    throw "发布产出目录未找到：$cortanaPublishDir"
}

if (Test-Path $ReleaseDir) {
    Remove-Item $ReleaseDir -Recurse -Force
}

New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null
Copy-Item (Join-Path $cortanaPublishDir '*') $ReleaseDir -Recurse -Force

$cortanaExe = Join-Path $ReleaseDir 'Cortana.exe'
if (Test-Path $cortanaExe) {
    $size = [math]::Round((Get-Item $cortanaExe).Length / 1MB, 1)
    Write-Host "  Cortana.exe: $size MB" -ForegroundColor Gray
} else {
    throw "发布产出 Cortana.exe 未找到：$cortanaExe"
}

# ──────── 2. 发布 NativeHost 子进程（AOT） ────────
Write-Host "`n[2/3] 发布 NativeHost 子进程（AOT）..." -ForegroundColor Yellow
dotnet publish $NativeHostProj -c Release -r win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "NativeHost AOT 发布失败" }

$nativeHostPublishDir = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost\bin\Release\net10.0\win-x64\publish'
$nativeHostExe = Join-Path $nativeHostPublishDir 'Cortana.NativeHost.exe'

if (-not (Test-Path $nativeHostExe)) {
    throw "NativeHost AOT 产出未找到：$nativeHostExe"
}

$nhSize = [math]::Round((Get-Item $nativeHostExe).Length / 1KB, 1)
if ($nhSize -lt 500) {
    throw "NativeHost.exe 仅 $nhSize KB，疑似非 AOT 版本（托管 EXE），请检查 PublishAot 配置"
}

Copy-Item $nativeHostExe (Join-Path $ReleaseDir 'Cortana.NativeHost.exe') -Force
Write-Host "  Cortana.NativeHost.exe: $nhSize KB (AOT)" -ForegroundColor Gray

# ──────── 3. 发布 NativeTestPlugin（AOT） ────────
if ($SkipNativePlugin) {
    Write-Host "`n[3/3] 跳过 NativeTestPlugin 发布" -ForegroundColor DarkGray
} else {
    Write-Host "`n[3/3] 发布 NativeTestPlugin（AOT）..." -ForegroundColor Yellow

    if (-not (Test-Path $NativePluginProj)) {
        Write-Host "  NativeTestPlugin 项目不存在，跳过" -ForegroundColor DarkGray
    } else {
        dotnet publish $NativePluginProj -c Release -r win-x64 --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "NativeTestPlugin AOT 发布失败" }

        $pluginPublishDir = Join-Path $SolutionDir 'Samples\NativeTestPlugin\bin\Release\net10.0\win-x64\publish'
        $pluginTargetDir = Join-Path $ReleaseDir '.cortana\plugins\native-test-plugin'

        if (Test-Path $pluginTargetDir) {
            Remove-Item $pluginTargetDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $pluginTargetDir -Force | Out-Null

        Copy-Item (Join-Path $pluginPublishDir 'NativeTestPlugin.dll') $pluginTargetDir
        Copy-Item (Join-Path $SolutionDir 'Samples\NativeTestPlugin\plugin.json') $pluginTargetDir

        $pluginSize = [math]::Round((Get-Item (Join-Path $pluginTargetDir 'NativeTestPlugin.dll')).Length / 1KB, 1)
        Write-Host "  NativeTestPlugin.dll: $pluginSize KB (AOT)" -ForegroundColor Gray
    }
}

# ──────── 完成 ────────
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host "输出目录: $ReleaseDir"
Write-Host ""
Write-Host "文件清单:" -ForegroundColor Cyan
Get-ChildItem $ReleaseDir -Filter "Cortana*" -File | ForEach-Object {
    $s = if ($_.Length -gt 1MB) { "$([math]::Round($_.Length/1MB,1)) MB" } else { "$([math]::Round($_.Length/1KB,1)) KB" }
    Write-Host "  $($_.Name) — $s"
}

$pluginDir = Join-Path $ReleaseDir '.cortana\plugins'
if (Test-Path $pluginDir) {
    $pluginCount = (Get-ChildItem $pluginDir -Directory).Count
    Write-Host "  .cortana/plugins/ — $pluginCount 个插件目录"
}
