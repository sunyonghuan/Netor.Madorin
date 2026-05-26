#!/usr/bin/env pwsh
# 修复版发布脚本：自动添加 vswhere.exe 所在目录到 PATH，确保 AOT 链接成功

$ErrorActionPreference = 'Stop'

$vswhereDir = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $vswhereDir 'vswhere.exe')) {
    $env:PATH = $vswhereDir + ';' + $env:PATH
    Write-Host "[+] 已添加 vswhere.exe 到 PATH" -ForegroundColor Green
} else {
    Write-Host "[!] 未找到 vswhere.exe，AOT 链接可能失败" -ForegroundColor Yellow
}

$ProjectFile = 'E:\Netor.me\Cortana\Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj'
$NativeHostProjectFile = 'E:\Netor.me\Cortana\Src\Plugins\Netor.Cortana.NativeHost\Netor.Cortana.NativeHost.csproj'
$OutputDir = 'E:\Netor.me\Cortana\Realases\Cortana'

# 先终止正在运行的 Cortana 进程
$running = Get-Process -Name 'Cortana' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[*] Stopping running Cortana process..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# 清理旧文件
if (Test-Path $OutputDir) {
    $cleanExts = @('*.exe', '*.dll', '*.json', '*.config', '*.manifest', '*.pdb')
    foreach ($ext in $cleanExts) {
        Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }
}

Write-Host "[1/2] Publishing UI (AOT)..." -ForegroundColor Cyan
dotnet publish $ProjectFile -c Release -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "UI publish FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "[2/2] Publishing NativeHost (AOT)..." -ForegroundColor Cyan
dotnet publish $NativeHostProjectFile -c Release -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "NativeHost publish FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 清理调试符号
$junkExts = @('*.pdb', '*.xml')
foreach ($ext in $junkExts) {
    Get-ChildItem -Path $OutputDir -Filter $ext -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

$exePath = Join-Path $OutputDir 'Cortana.exe'
if (Test-Path $exePath) {
    $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Publish completed!" -ForegroundColor Green
    Write-Host "  Path: $exePath"
    Write-Host "  Size: ${size} MB"
} else {
    Write-Host "WARNING: Cortana.exe not found in output dir" -ForegroundColor Yellow
}
