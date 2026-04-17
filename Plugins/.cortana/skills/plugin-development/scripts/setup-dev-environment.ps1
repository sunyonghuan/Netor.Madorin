<#
.SYNOPSIS
    检测并安装 Cortana 插件开发所需的环境（.NET SDK + AOT 编译工具链）。
.DESCRIPTION
    1. 检测 .NET SDK 是否已安装且版本 >= 10.0
    2. 未安装则自动安装最新稳定版 .NET SDK
    3. 检测 AOT 编译所需的 C++ 构建工具（MSVC 链接器）
    4. 未安装则自动安装 Visual Studio Build Tools（仅 C++ 桌面组件，不装 VS/VSCode）
    5. 验证环境就绪
.PARAMETER SkipAot
    跳过 AOT 工具链检测（仅开发 Dotnet 托管插件时可跳过）。
.PARAMETER DotnetChannel
    .NET SDK 安装通道，默认 10.0（可改为 STS 或其他版本号）。
.NOTES
    需要管理员权限安装 Build Tools。
    .NET SDK 安装不需要管理员权限。
#>
param(
    [switch]$SkipAot,
    [string]$DotnetChannel = "10.0"
)

$ErrorActionPreference = 'Stop'

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Cortana 插件开发环境检测" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$AllGood = $true

# ============================================================
# 1. 检测 .NET SDK
# ============================================================
Write-Host "[1/3] 检测 .NET SDK..." -ForegroundColor Yellow

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$SdkInstalled = $false
$SdkVersion = $null

if ($dotnetCmd) {
    try {
        $sdkList = dotnet --list-sdks 2>$null
        # 查找 >= 10.0 的 SDK
        $matchingSdk = $sdkList | Where-Object { $_ -match "^(\d+)\." -and [int]$Matches[1] -ge 10 }
        if ($matchingSdk) {
            $SdkVersion = ($matchingSdk | Select-Object -Last 1) -replace '\s*\[.*$', ''
            $SdkInstalled = $true
            Write-Host "  ✓ .NET SDK $SdkVersion 已安装" -ForegroundColor Green
        } else {
            Write-Host "  ⚠ 已安装 .NET SDK，但版本过低（需要 >= 10.0）" -ForegroundColor Yellow
            if ($sdkList) {
                Write-Host "    当前版本: $($sdkList | Select-Object -Last 1)" -ForegroundColor Gray
            }
        }
    } catch {
        Write-Host "  ⚠ dotnet 命令存在但无法获取版本" -ForegroundColor Yellow
    }
}

if (-not $SdkInstalled) {
    $AllGood = $false
    Write-Host "  ✗ 未检测到 .NET 10+ SDK，准备安装..." -ForegroundColor Red
    Write-Host ""

    # 下载 dotnet-install.ps1
    $installScript = Join-Path $env:TEMP "dotnet-install.ps1"
    Write-Host "    下载 dotnet-install.ps1..." -ForegroundColor Gray
    try {
        Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript -UseBasicParsing
    } catch {
        Write-Host "  ✗ 下载失败: $_" -ForegroundColor Red
        Write-Host "  请手动安装: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
        exit 1
    }

    # 安装 .NET SDK
    Write-Host "    安装 .NET SDK (channel: $DotnetChannel)..." -ForegroundColor Gray
    try {
        & $installScript -Channel $DotnetChannel -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
    } catch {
        Write-Host "  ✗ 安装失败: $_" -ForegroundColor Red
        exit 1
    }

    # 确保 PATH 包含 dotnet
    $dotnetPath = "$env:LOCALAPPDATA\Microsoft\dotnet"
    if ($env:PATH -notlike "*$dotnetPath*") {
        $env:PATH = "$dotnetPath;$env:PATH"
        # 持久化到用户环境变量
        [Environment]::SetEnvironmentVariable("PATH", "$dotnetPath;$([Environment]::GetEnvironmentVariable('PATH', 'User'))", "User")
        Write-Host "    已添加到 PATH: $dotnetPath" -ForegroundColor Gray
    }

    # 验证
    try {
        $newVersion = dotnet --version 2>$null
        Write-Host "  ✓ .NET SDK $newVersion 安装成功" -ForegroundColor Green
        $SdkInstalled = $true
    } catch {
        Write-Host "  ✗ 安装后验证失败，请重新打开终端再试" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# ============================================================
# 2. 检测 AOT 编译工具链（C++ 构建工具）
# ============================================================
if ($SkipAot) {
    Write-Host "[2/3] AOT 工具链检测（已跳过 -SkipAot）" -ForegroundColor Yellow
} else {
    Write-Host "[2/3] 检测 AOT 编译工具链（C++ 构建工具）..." -ForegroundColor Yellow

    $AotReady = $false

    # 检测方式：查找 link.exe（MSVC 链接器）
    # 通常在: C:\Program Files\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe
    # 或 Build Tools 同路径
    $vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vsWhere) {
        $vsPath = & $vsWhere -latest -property installationPath -requires Microsoft.VisualCpp.Tools.HostX64.TargetX64 2>$null
        if ($vsPath) {
            $AotReady = $true
            Write-Host "  ✓ C++ 构建工具已安装" -ForegroundColor Green
            Write-Host "    位置: $vsPath" -ForegroundColor Gray
        }
    }

    if (-not $AotReady) {
        # 备选检测：直接搜索 link.exe
        $linkPaths = @(
            "${env:ProgramFiles}\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe",
            "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe"
        )
        foreach ($pattern in $linkPaths) {
            if (Get-Item $pattern -ErrorAction SilentlyContinue) {
                $AotReady = $true
                Write-Host "  ✓ C++ 构建工具已安装（通过 link.exe 检测）" -ForegroundColor Green
                break
            }
        }
    }

    if (-not $AotReady) {
        $AllGood = $false
        Write-Host "  ✗ 未检测到 C++ 构建工具（AOT 编译必需）" -ForegroundColor Red
        Write-Host ""
        Write-Host "    Native 插件的 AOT 发布需要 MSVC 链接器。" -ForegroundColor Yellow
        Write-Host "    需要安装 Visual Studio Build Tools（仅 C++ 组件，约 3-5 GB）。" -ForegroundColor Yellow
        Write-Host ""

        # 确认是否安装
        $answer = Read-Host "    是否现在下载并安装 Build Tools？(Y/n)"
        if ($answer -eq '' -or $answer -match '^[Yy]') {
            Write-Host ""
            Write-Host "    下载 Visual Studio Build Tools 安装器..." -ForegroundColor Gray

            $vsbtInstaller = Join-Path $env:TEMP "vs_BuildTools.exe"
            try {
                Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vs_BuildTools.exe" -OutFile $vsbtInstaller -UseBasicParsing
            } catch {
                Write-Host "  ✗ 下载失败: $_" -ForegroundColor Red
                Write-Host "  请手动下载: https://aka.ms/vs/17/release/vs_BuildTools.exe" -ForegroundColor Yellow
                exit 1
            }

            Write-Host "    启动安装（仅安装 C++ 桌面开发组件）..." -ForegroundColor Gray
            Write-Host "    ⚠ 需要管理员权限，可能弹出 UAC 提示" -ForegroundColor Yellow
            Write-Host ""

            # 静默安装只需要的组件
            $installArgs = @(
                "--add", "Microsoft.VisualStudio.Workload.VCTools",
                "--add", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "--add", "Microsoft.VisualStudio.Component.Windows11SDK.26100",
                "--includeRecommended",
                "--passive",
                "--wait",
                "--norestart"
            )

            try {
                $process = Start-Process -FilePath $vsbtInstaller -ArgumentList $installArgs -Wait -PassThru
                if ($process.ExitCode -eq 0 -or $process.ExitCode -eq 3010) {
                    Write-Host "  ✓ Build Tools 安装成功" -ForegroundColor Green
                    if ($process.ExitCode -eq 3010) {
                        Write-Host "    ⚠ 可能需要重启电脑才能生效" -ForegroundColor Yellow
                    }
                } else {
                    Write-Host "  ⚠ 安装退出码: $($process.ExitCode)，请检查安装是否成功" -ForegroundColor Yellow
                }
            } catch {
                Write-Host "  ✗ 安装启动失败: $_" -ForegroundColor Red
                Write-Host "  请手动运行: $vsbtInstaller --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended" -ForegroundColor Yellow
            }
        } else {
            Write-Host "    跳过安装。Native AOT 发布将不可用。" -ForegroundColor Yellow
            Write-Host "    如果只开发 Dotnet 托管插件，可忽略此项。" -ForegroundColor Gray
        }
    }
}

Write-Host ""

# ============================================================
# 3. 环境总结
# ============================================================
Write-Host "[3/3] 环境总结" -ForegroundColor Yellow
Write-Host ""

# .NET SDK
$finalVersion = dotnet --version 2>$null
if ($finalVersion) {
    Write-Host "  .NET SDK:       $finalVersion ✓" -ForegroundColor Green
} else {
    Write-Host "  .NET SDK:       未安装 ✗" -ForegroundColor Red
}

# AOT 工具链
if (-not $SkipAot) {
    $vsWhereCheck = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $aotOk = $false
    if (Test-Path $vsWhereCheck) {
        $vsPathCheck = & $vsWhereCheck -latest -property installationPath -requires Microsoft.VisualCpp.Tools.HostX64.TargetX64 2>$null
        if ($vsPathCheck) { $aotOk = $true }
    }
    if ($aotOk) {
        Write-Host "  AOT 工具链:     已就绪 ✓" -ForegroundColor Green
    } else {
        Write-Host "  AOT 工具链:     未就绪 ✗（Native 插件 AOT 发布不可用）" -ForegroundColor Red
    }
} else {
    Write-Host "  AOT 工具链:     已跳过" -ForegroundColor Gray
}

Write-Host ""

if ($AllGood) {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  ✓ 开发环境已就绪，可以开始插件开发" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host "  ⚠ 部分环境已安装，请检查上方输出" -ForegroundColor Yellow
    Write-Host "========================================" -ForegroundColor Yellow
}
