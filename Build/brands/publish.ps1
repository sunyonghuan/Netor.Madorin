#!/usr/bin/env pwsh
<#
.SYNOPSIS
    按品牌名称发布桌面端程序。
.DESCRIPTION
    入口脚本：传入品牌名称后，自动定位品牌子文件夹、同步品牌资产（图标/Logo），
    然后调用 ui.brand.publish.ps1 执行发布，发布完成后恢复默认资产。

    品牌子文件夹约定：
      brands/{brand}/
        {brand}.json   — 品牌配置
        brand.ico      — 应用图标（覆盖 UI / NativeHost 中的默认图标）
        brand.png      — 应用 Logo（覆盖 UI Assets 中的默认 Logo）

.PARAMETER Brand
    品牌标识符，对应 brands 目录下的子文件夹名称，例如 madorin 或 sreamx。
.PARAMETER Version
    打包时使用的版本号。只在 -Package 时传给 ui.package.ps1。
    不传则由 ui.package.ps1 从 UI 项目文件读取 Version。
.PARAMETER Package
    发布完成后立即打包 zip 和 sha256。
.PARAMETER UseVsWhereFix
    使用 ui.publish.fix.ps1 发布，修复部分机器 Native AOT 找不到 vswhere.exe 的问题。
.PARAMETER ValidateOnly
    只读取并打印品牌配置，不同步资产，不执行发布。

.EXAMPLE
    # 校验 Madorin 品牌配置，不发布
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand madorin -ValidateOnly

.EXAMPLE
    # 发布 Madorin
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand madorin -UseVsWhereFix

.EXAMPLE
    # 发布并打包 SreamX
    powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand sreamx -UseVsWhereFix -Package
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Brand,

    [string]$Version,
    [switch]$Package,
    [switch]$UseVsWhereFix,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

$BrandsDir    = $PSScriptRoot
$BuildDir     = Split-Path $BrandsDir -Parent
$SolutionDir  = (Resolve-Path (Join-Path $BuildDir '..')).Path

$BrandDir     = Join-Path $BrandsDir $Brand
$BrandJson    = Join-Path $BrandDir "$Brand.json"

if (-not (Test-Path $BrandDir)) {
    throw "Brand folder not found: $BrandDir"
}
if (-not (Test-Path $BrandJson)) {
    throw "Brand config not found: $BrandJson"
}

# ── 资产目标路径 ─────────────────────────────────────────────────────────────
$UiAssetsDir    = Join-Path $SolutionDir 'Src\Netor.Cortana.UI\Assets'
$NativeHostDir  = Join-Path $SolutionDir 'Src\Plugins\Netor.Cortana.NativeHost'

$uiIconDest     = Join-Path $UiAssetsDir   'brand.ico'
$uiLogoDest     = Join-Path $UiAssetsDir   'brand.png'
$hostIconDest   = Join-Path $NativeHostDir 'brand.ico'

# ── 品牌资产源文件 ────────────────────────────────────────────────────────────
$srcIcon = Join-Path $BrandDir 'brand.ico'
$srcLogo = Join-Path $BrandDir 'brand.png'

# ── 备份当前默认资产（字节级，保证恢复精确） ────────────────────────────────
$backupUiIcon    = if (Test-Path $uiIconDest)   { [IO.File]::ReadAllBytes($uiIconDest)   } else { $null }
$backupUiLogo    = if (Test-Path $uiLogoDest)   { [IO.File]::ReadAllBytes($uiLogoDest)   } else { $null }
$backupHostIcon  = if (Test-Path $hostIconDest) { [IO.File]::ReadAllBytes($hostIconDest) } else { $null }

# ── 执行发布 ─────────────────────────────────────────────────────────────────
try {
    if (-not $ValidateOnly) {
        Write-Host "Syncing brand assets: '$Brand'..." -ForegroundColor Cyan
        if (Test-Path $srcIcon) {
            Copy-Item $srcIcon $uiIconDest   -Force
            Copy-Item $srcIcon $hostIconDest -Force
            Write-Host "  brand.ico → UI/Assets + NativeHost" -ForegroundColor DarkCyan
        } else {
            Write-Warning "brand.ico not found in '$BrandDir', using project default."
        }
        if (Test-Path $srcLogo) {
            Copy-Item $srcLogo $uiLogoDest -Force
            Write-Host "  brand.png → UI/Assets" -ForegroundColor DarkCyan
        } else {
            Write-Warning "brand.png not found in '$BrandDir', using project default."
        }
    }

    $publishScript = Join-Path $BuildDir 'ui.brand.publish.ps1'
    $passArgs = @{ BrandConfigPath = $BrandJson }
    if ($Version)       { $passArgs.Version       = $Version }
    if ($Package)       { $passArgs.Package        = $true   }
    if ($UseVsWhereFix) { $passArgs.UseVsWhereFix  = $true   }
    if ($ValidateOnly)  { $passArgs.ValidateOnly   = $true   }

    & $publishScript @passArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed, exit code: $LASTEXITCODE"
    }
} finally {
    if (-not $ValidateOnly) {
        Write-Host "Restoring default brand assets..." -ForegroundColor Yellow
        if ($null -ne $backupUiIcon)   { [IO.File]::WriteAllBytes($uiIconDest,   $backupUiIcon)   }
        if ($null -ne $backupUiLogo)   { [IO.File]::WriteAllBytes($uiLogoDest,   $backupUiLogo)   }
        if ($null -ne $backupHostIcon) { [IO.File]::WriteAllBytes($hostIconDest, $backupHostIcon) }
        Write-Host "Brand assets restored." -ForegroundColor Green
    }
}
