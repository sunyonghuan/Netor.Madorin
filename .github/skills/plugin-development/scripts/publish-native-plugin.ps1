<#
.SYNOPSIS
    AOT 发布 Native 插件并部署到插件目录。
.DESCRIPTION
    1. AOT 编译 Native 插件
    2. 部署 DLL + plugin.json 到 Cortana 插件目录
.PARAMETER ProjectDir
    插件项目相对于解决方案根目录的路径，如 Samples\MyPlugin。
.PARAMETER PluginName
    插件部署目录名（默认使用项目名的 kebab-case）。
.PARAMETER PluginRoot
    自定义插件部署根目录（默认部署到 Debug 输出 + 解决方案根目录两处）。
#>
param(
    [Parameter(Mandatory=$true)][string]$ProjectDir,
    [string]$PluginName,
    [string]$PluginRoot
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot = <repo>\.github\skills\plugin-development\scripts；向上 4 级 = 仓库根
$SolutionDir = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$FullProjectDir = Join-Path $SolutionDir $ProjectDir

if (-not (Test-Path $FullProjectDir)) {
    Write-Host "❌ 项目目录不存在：$FullProjectDir" -ForegroundColor Red
    exit 1
}

# 查找 csproj
$csproj = Get-ChildItem -Path $FullProjectDir -Filter "*.csproj" | Select-Object -First 1
if (-not $csproj) {
    Write-Host "❌ 未找到 .csproj 文件" -ForegroundColor Red
    exit 1
}

$ProjectName = $csproj.BaseName
if (-not $PluginName) {
    # PascalCase -> kebab-case
    $PluginName = ($ProjectName -creplace '([A-Z])', '-$1').Trim('-').ToLower()
}

$PublishDir = Join-Path $FullProjectDir "bin\Release\net10.0\win-x64\publish"

# 部署目标：开发环境部署到 Cortana Debug 输出目录
# 运行时插件目录为 {WorkspaceDirectory}\.cortana\plugins\，由软件自行管理
$DeployTargets = @()
if ($PluginRoot) {
    $DeployTargets += $PluginRoot
} else {
    $DeployTargets += Join-Path $SolutionDir "Src\Netor.Cortana\bin\Debug\net10.0-windows\.cortana\plugins"
}

Write-Host "=== Native 插件 AOT 发布: $ProjectName ===" -ForegroundColor Cyan
Write-Host "项目: $($csproj.FullName)"

# ────────────────────────────────────────────
# ⚠️ AOT 编译需要 Visual Studio「使用 C++ 的桌面开发」工作负载
# 如果报 NETSDK1099，请打开 VS Installer 安装该工作负载
# ────────────────────────────────────────────

# 1. AOT 发布
Write-Host "`n[1/2] AOT 编译（可能耗时较长）..." -ForegroundColor Yellow
dotnet publish $csproj.FullName -c Release -r win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ AOT 编译失败" -ForegroundColor Red
    Write-Host "  常见原因：" -ForegroundColor Yellow
    Write-Host "  - 没装 VS「使用 C++ 的桌面开发」工作负载 → NETSDK1099" -ForegroundColor Yellow
    Write-Host "  - csproj 缺少 PublishAot/OutputType/RuntimeIdentifier" -ForegroundColor Yellow
    exit 1
}

# 2. 部署
Write-Host "[2/2] 部署文件..." -ForegroundColor Yellow
$dllPath = Join-Path $PublishDir "$ProjectName.dll"
$jsonPath = Join-Path $PublishDir "plugin.json"

if (-not (Test-Path $dllPath)) {
    Write-Host "❌ 发布产出不存在：$dllPath" -ForegroundColor Red
    exit 1
}

foreach ($target in $DeployTargets) {
    $targetDir = Join-Path $target $PluginName

    if (Test-Path $targetDir) {
        Remove-Item $targetDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    # 只复制 .dll 和 plugin.json，排除 .xml/.pdb 等无用文件
    Copy-Item $dllPath $targetDir
    if (Test-Path $jsonPath) {
        Copy-Item $jsonPath $targetDir
    } else {
        $projJson = Join-Path $FullProjectDir "plugin.json"
        if (Test-Path $projJson) {
            Copy-Item $projJson $targetDir
        }
    }
    Write-Host "  ✓ $targetDir" -ForegroundColor Green
}

$dllSize = (Get-Item $dllPath).Length / 1KB
Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Write-Host "DLL 大小: $([math]::Round($dllSize, 1)) KB"
Write-Host "部署到 $($DeployTargets.Count) 个目录"
