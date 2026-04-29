<#
.SYNOPSIS
    Publish Netor.Cortana to Realases\Cortana.
.DESCRIPTION
    Steps:
    1. Publish Cortana main app.
    2. Publish NativeHost AOT executable and copy it to release directory.
    3. Publish NativeTestPlugin AOT plugin and deploy it to plugin directory.
.PARAMETER SkipNativePlugin
    Skip NativeTestPlugin publishing.
#>
param(
    [switch]$SkipNativePlugin
)

$ErrorActionPreference = 'Stop'

$BuildDir = $PSScriptRoot
$SolutionDir = (Resolve-Path (Join-Path $BuildDir '..')).Path
$ReleaseDir = Join-Path $SolutionDir 'Realases\Cortana'

$CortanaProj = Join-Path $SolutionDir 'Src\Netor.Cortana.AvaloniaUI\Netor.Cortana.AvaloniaUI.csproj'
$NativeHostProj = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost\Netor.Cortana.NativeHost.csproj'
$NativePluginProj = Join-Path $SolutionDir 'Plugins\Samples\NativeTestPlugin\NativeTestPlugin.csproj'

Write-Host '============================================' -ForegroundColor Cyan
Write-Host '  Netor.Cortana Publish' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host "SolutionDir: $SolutionDir"
Write-Host "ReleaseDir:  $ReleaseDir"
Write-Host ''

Write-Host '[1/3] Publishing Cortana main app...' -ForegroundColor Yellow
dotnet publish $CortanaProj -c Release -p:PublishProfile=FolderProfile --nologo
if ($LASTEXITCODE -ne 0) { throw 'Cortana main app publish failed.' }

$cortanaExe = Join-Path $ReleaseDir 'Cortana.exe'
if (Test-Path $cortanaExe) {
    $size = [math]::Round((Get-Item $cortanaExe).Length / 1MB, 1)
    Write-Host "  Cortana.exe: $size MB" -ForegroundColor Gray
} else {
    throw "Cortana.exe was not found: $cortanaExe"
}

Write-Host "`n[2/3] Publishing NativeHost AOT executable..." -ForegroundColor Yellow
dotnet publish $NativeHostProj -c Release -r win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'NativeHost AOT publish failed.' }

$nativeHostPublishDir = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost\bin\Release\net10.0\win-x64\publish'
$nativeHostExe = Join-Path $nativeHostPublishDir 'Cortana.NativeHost.exe'
if (-not (Test-Path $nativeHostExe)) {
    throw "NativeHost AOT output was not found: $nativeHostExe"
}

$nhSize = [math]::Round((Get-Item $nativeHostExe).Length / 1KB, 1)
if ($nhSize -lt 500) {
    throw "NativeHost.exe is only $nhSize KB. It may not be an AOT binary. Please check PublishAot settings."
}

New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null
Copy-Item $nativeHostExe (Join-Path $ReleaseDir 'Cortana.NativeHost.exe') -Force
Write-Host "  Cortana.NativeHost.exe: $nhSize KB (AOT)" -ForegroundColor Gray

if ($SkipNativePlugin) {
    Write-Host "`n[3/3] Skipping NativeTestPlugin publish." -ForegroundColor DarkGray
} else {
    Write-Host "`n[3/3] Publishing NativeTestPlugin AOT plugin..." -ForegroundColor Yellow
    if (-not (Test-Path $NativePluginProj)) {
        Write-Host '  NativeTestPlugin project was not found. Skipped.' -ForegroundColor DarkGray
    } else {
        dotnet publish $NativePluginProj -c Release -r win-x64 --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'NativeTestPlugin AOT publish failed.' }

        $pluginPublishDir = Join-Path $SolutionDir 'Plugins\Samples\NativeTestPlugin\bin\Release\net10.0\win-x64\publish'
        $pluginTargetDir = Join-Path $ReleaseDir '.cortana\plugins\native-test-plugin'

        if (Test-Path $pluginTargetDir) {
            Remove-Item $pluginTargetDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $pluginTargetDir -Force | Out-Null

        Copy-Item (Join-Path $pluginPublishDir 'NativeTestPlugin.dll') $pluginTargetDir -Force
        Copy-Item (Join-Path $SolutionDir 'Plugins\Samples\NativeTestPlugin\plugin.json') $pluginTargetDir -Force

        $pluginSize = [math]::Round((Get-Item (Join-Path $pluginTargetDir 'NativeTestPlugin.dll')).Length / 1KB, 1)
        Write-Host "  NativeTestPlugin.dll: $pluginSize KB (AOT)" -ForegroundColor Gray
    }
}

Write-Host ''
Write-Host '============================================' -ForegroundColor Green
Write-Host '  Publish completed!' -ForegroundColor Green
Write-Host '============================================' -ForegroundColor Green
Write-Host "Output: $ReleaseDir"
