# =============================================================================
# YamaVulkanSandbox 构建脚本
# 在 TRAE 环境外运行：右键 -> 使用 PowerShell 运行
# 或在 PowerShell 中执行: .\build.ps1
# =============================================================================
param(
    [string]$Configuration = "Release",
    [switch]$Run = $false,
    [switch]$Clean = $false
)

$ErrorActionPreference = "Stop"
$projectName = "YamaVulkanSandbox"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $scriptDir "bin\$Configuration\net8.0"
$nugetRoot = "E:\nuget\packages"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  Yama Vulkan Sandbox - 构建脚本" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  项目目录: $scriptDir"
Write-Host "  输出目录: $outputDir"
Write-Host "  配置:     $Configuration"
Write-Host ""

# ── 清理 ──
if ($Clean) {
    Write-Host "[1/5] 清理旧构建..." -ForegroundColor Yellow
    $binDir = Join-Path $scriptDir "bin"
    $objDir = Join-Path $scriptDir "obj"
    if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
    if (Test-Path $objDir) { Remove-Item $objDir -Recurse -Force }
    Write-Host "  已清理 bin 和 obj 目录" -ForegroundColor Green
} else {
    Write-Host "[1/5] 跳过清理（使用 -Clean 启用）" -ForegroundColor DarkGray
}

# ── 确保输出目录存在 ──
Write-Host "[2/5] 准备输出目录..." -ForegroundColor Yellow
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}
Write-Host "  输出目录就绪" -ForegroundColor Green

# ── 编译 ──
Write-Host "[3/5] 编译项目..." -ForegroundColor Yellow

$buildSuccess = $false

# 方法1：尝试 dotnet build
$dotnetExe = Get-Command "dotnet" -ErrorAction SilentlyContinue
if ($dotnetExe) {
    Write-Host "  尝试 dotnet build..." -ForegroundColor DarkGray
    # 临时移除 Directory.Build.props 的影响，直接在项目目录构建
    $env:NUGET_PACKAGES = $nugetRoot

    & dotnet build "$scriptDir\$projectName.csproj" -c $Configuration -p:Platform=x64 --no-restore 2>&1 |
        ForEach-Object {
            $line = $_.ToString()
            if ($line -match "error") {
                Write-Host "  $line" -ForegroundColor Red
            } elseif ($line -match "warning") {
                Write-Host "  $line" -ForegroundColor Yellow
            } else {
                Write-Host "  $line" -ForegroundColor DarkGray
            }
        }

    if ($LASTEXITCODE -eq 0) {
        $buildSuccess = $true
        Write-Host "  dotnet build 成功" -ForegroundColor Green
    } else {
        Write-Host "  dotnet build 失败 (exit $LASTEXITCODE)，尝试 csc 直接编译..." -ForegroundColor Yellow
    }
}

# 方法2：回退到 csc 直接编译
if (-not $buildSuccess) {
    Write-Host "  使用 Roslyn csc 直接编译..." -ForegroundColor DarkGray

    $cscPath = Get-ChildItem "C:\Program Files\dotnet\sdk\*\Roslyn\bincore\csc.dll" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1

    if (-not $cscPath) {
        Write-Host "  错误: 未找到 csc.dll" -ForegroundColor Red
        exit 1
    }

    Write-Host "  使用编译器: $($cscPath.FullName)" -ForegroundColor DarkGray

    $netcoreDir = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.28"
    if (-not (Test-Path $netcoreDir)) {
        $netcoreDir = (Get-ChildItem "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.*" |
            Sort-Object Name -Descending | Select-Object -First 1).FullName
    }

    $refs = @(
        "-reference:`"$nugetRoot\silk.net.core\2.22.0\lib\net6.0\Silk.NET.Core.dll`""
        "-reference:`"$nugetRoot\silk.net.maths\2.22.0\lib\netstandard2.0\Silk.NET.Maths.dll`""
        "-reference:`"$nugetRoot\silk.net.vulkan\2.22.0\lib\netstandard2.0\Silk.NET.Vulkan.dll`""
        "-reference:`"$nugetRoot\silk.net.vulkan.extensions.ext\2.22.0\lib\netstandard2.0\Silk.NET.Vulkan.Extensions.EXT.dll`""
        "-reference:`"$nugetRoot\silk.net.vulkan.extensions.khr\2.22.0\lib\netstandard2.0\Silk.NET.Vulkan.Extensions.KHR.dll`""
        "-reference:`"$nugetRoot\silk.net.windowing.common\2.22.0\lib\netstandard2.0\Silk.NET.Windowing.Common.dll`""
        "-reference:`"$nugetRoot\silk.net.windowing.glfw\2.22.0\lib\netstandard2.0\Silk.NET.Windowing.Glfw.dll`""
        "-reference:`"$nugetRoot\silk.net.glfw\2.22.0\lib\netstandard2.0\Silk.NET.GLFW.dll`""
        "-reference:`"$netcoreDir\System.Runtime.dll`""
        "-reference:`"$netcoreDir\System.Console.dll`""
        "-reference:`"$netcoreDir\System.Threading.dll`""
        "-reference:`"$netcoreDir\System.Runtime.InteropServices.dll`""
        "-reference:`"$netcoreDir\netstandard.dll`""
        "-reference:`"$netcoreDir\System.Private.CoreLib.dll`""
        "-reference:`"$netcoreDir\System.Memory.dll`""
        "-reference:`"$netcoreDir\System.Buffers.dll`""
        "-reference:`"$netcoreDir\System.Numerics.Vectors.dll`""
        "-reference:`"$netcoreDir\System.Collections.dll`""
        "-reference:`"$netcoreDir\System.Resources.ResourceManager.dll`""
    )

    $sourceFiles = @(
        "`"$scriptDir\GlobalUsings.cs`""
        "`"$scriptDir\Program.cs`""
        "`"$scriptDir\VulkanTriangleRenderer.cs`""
    )

    $outputDll = Join-Path $outputDir "$projectName.dll"
    $args = @(
        "-target:exe"
        "-platform:x64"
        "-unsafe"
        "-nologo"
        "-nullable:enable"
        "-langversion:latest"
        "-out:`"$outputDll`""
    ) + $refs + $sourceFiles

    & dotnet $cscPath.FullName @args 2>&1 |
        ForEach-Object {
            $line = $_.ToString()
            if ($line -match "error") {
                Write-Host "  $line" -ForegroundColor Red
            } elseif ($line -match "warning") {
                Write-Host "  $line" -ForegroundColor Yellow
            } else {
                Write-Host "  $line" -ForegroundColor DarkGray
            }
        }

    if ($LASTEXITCODE -eq 0) {
        $buildSuccess = $true
        Write-Host "  csc 编译成功" -ForegroundColor Green
    } else {
        Write-Host "  csc 编译失败 (exit $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
}

if (-not $buildSuccess) {
    Write-Host "  构建失败" -ForegroundColor Red
    exit 1
}

# ── 复制依赖 DLL ──
Write-Host "[4/5] 复制依赖项..." -ForegroundColor Yellow

# Silk.NET 托管 DLL
$silkDlls = @(
    @{Src="$nugetRoot\silk.net.core\2.22.0\lib\net6.0\Silk.NET.Core.dll"; Dst="Silk.NET.Core.dll"}
    @{Src="$nugetRoot\silk.net.maths\2.22.0\lib\netstandard2.0\Silk.NET.Maths.dll"; Dst="Silk.NET.Maths.dll"}
    @{Src="$nugetRoot\silk.net.vulkan\2.22.0\lib\netstandard2.0\Silk.NET.Vulkan.dll"; Dst="Silk.NET.Vulkan.dll"}
    @{Src="$nugetRoot\silk.net.vulkan.extensions.ext\2.22.0\lib\netstandard2.0\Silk.NET.Vulkan.Extensions.EXT.dll"; Dst="Silk.NET.Vulkan.Extensions.EXT.dll"}
    @{Src="$nugetRoot\silk.net.vulkan.extensions.khr\2.22.0\lib\netstandard2.0\Silk.NET.Vulkan.Extensions.KHR.dll"; Dst="Silk.NET.Vulkan.Extensions.KHR.dll"}
    @{Src="$nugetRoot\silk.net.windowing.common\2.22.0\lib\netstandard2.0\Silk.NET.Windowing.Common.dll"; Dst="Silk.NET.Windowing.Common.dll"}
    @{Src="$nugetRoot\silk.net.windowing.glfw\2.22.0\lib\netstandard2.0\Silk.NET.Windowing.Glfw.dll"; Dst="Silk.NET.Windowing.Glfw.dll"}
    @{Src="$nugetRoot\silk.net.glfw\2.22.0\lib\netstandard2.0\Silk.NET.GLFW.dll"; Dst="Silk.NET.GLFW.dll"}
)

foreach ($dll in $silkDlls) {
    $srcPath = $dll.Src
    $dstPath = Join-Path $outputDir $dll.Dst
    if (Test-Path $srcPath) {
        Copy-Item $srcPath $dstPath -Force
        Write-Host "  复制: $($dll.Dst)" -ForegroundColor DarkGray
    } else {
        Write-Host "  警告: 未找到 $($dll.Dst) 源文件" -ForegroundColor Yellow
    }
}

# GLFW 原生 DLL
$glfwNativePath = "$nugetRoot\ultz.native.glfw\3.4.0\runtimes\win-x64\native\glfw3.dll"
if (Test-Path $glfwNativePath) {
    Copy-Item $glfwNativePath (Join-Path $outputDir "glfw3.dll") -Force
    Write-Host "  复制: glfw3.dll (原生)" -ForegroundColor DarkGray
} else {
    Write-Host "  警告: 未找到 glfw3.dll 原生库" -ForegroundColor Yellow
}

# ── 创建 runtimeconfig.json ──
Write-Host "[5/5] 生成 runtimeconfig.json..." -ForegroundColor Yellow

$runtimeconfig = @{
    runtimeOptions = @{
        tfm = "net8.0"
        framework = @{
            name = "Microsoft.NETCore.App"
            version = "8.0.0"
        }
        configProperties = @{
            System.Runtime.Loader.UseRidGraph = $true
        }
    }
}

$runtimeconfigPath = Join-Path $outputDir "$projectName.runtimeconfig.json"
$runtimeconfig | ConvertTo-Json -Depth 5 | Set-Content $runtimeconfigPath -Encoding UTF8
Write-Host "  已生成 $projectName.runtimeconfig.json" -ForegroundColor Green

# ── 完成 ──
Write-Host ""
Write-Host "================================================" -ForegroundColor Green
Write-Host "  构建成功!" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Green
Write-Host "  输出目录: $outputDir"
Write-Host "  可执行文件: $projectName.dll"
Write-Host ""
Write-Host "  运行方式: dotnet `"$outputDir\$projectName.dll`""
Write-Host "================================================" -ForegroundColor Green

# ── 可选: 直接运行 ──
if ($Run) {
    Write-Host ""
    Write-Host "启动应用..." -ForegroundColor Cyan
    & dotnet (Join-Path $outputDir "$projectName.dll")
}
