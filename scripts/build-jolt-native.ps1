# ============================================================================================
#  build-jolt-native.ps1 — 构建 YamaJolt.dll（native Jolt wrapper）。
#
#  步骤：
#    1. 用 vcpkg 安装 joltphysics
#    2. cmake 配置（带 vcpkg toolchain）
#    3. cmake 构建 YamaEngine.Native → YamaJolt.dll
#    4. DLL 输出到 YamaEngine\Native\（C# 运行时加载处）
#
#  前置：Visual Studio 2022（MSVC + Windows SDK）、CMake 3.20+、vcpkg。
#  用法：powershell -ExecutionPolicy Bypass -File scripts\build-jolt-native.ps1
# ============================================================================================
[CmdletBinding()]
param(
    [string]$VcpkgRoot = "",            # vcpkg 根目录；留空则自动探测
    [string]$BuildPreset = "Release",   # Release / Debug
    [switch]$SkipVcpkgInstall           # 跳过 vcpkg install（已装过时用）
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot "YamaEngine.Native"
$buildDir = Join-Path $nativeDir "build"

Write-Host "=== YamaJolt native build ===" -ForegroundColor Cyan
Write-Host "repoRoot : $repoRoot"
Write-Host "nativeDir: $nativeDir"

# --------------------------------------------------------------------------------------------
# 1. 定位 vcpkg
# --------------------------------------------------------------------------------------------
if ([string]::IsNullOrEmpty($VcpkgRoot)) {
    $candidates = @(
        (Join-Path $repoRoot "third_party\vcpkg"),
        "C:\vcpkg",
        "C:\dev\vcpkg",
        "$env:VCPKG_ROOT"
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($candidates.Count -eq 0) {
        throw "未找到 vcpkg。请用 -VcpkgRoot 指定，或设环境变量 VCPKG_ROOT。"
    }
    $VcpkgRoot = $candidates[0]
}
$vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"
if (-not (Test-Path $vcpkgExe)) {
    $vcpkgBat = Join-Path $VcpkgRoot "vcpkg.bat"
    if (-not (Test-Path $vcpkgBat)) { throw "vcpkg 可执行不存在于 $VcpkgRoot" }
    $vcpkgExe = $vcpkgBat
}
$toolchain = Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake"
Write-Host "vcpkg   : $VcpkgRoot" -ForegroundColor Green

# --------------------------------------------------------------------------------------------
# 2. 安装 joltphysics（x64-windows）
# --------------------------------------------------------------------------------------------
if (-not $SkipVcpkgInstall) {
    Write-Host "`n[1/4] vcpkg install joltphysics:x64-windows ..." -ForegroundColor Yellow
    & $vcpkgExe install joltphysics:x64-windows --triplet x64-windows
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install joltphysics 失败" }
} else {
    Write-Host "`n[1/4] 跳过 vcpkg install（-SkipVcpkgInstall）" -ForegroundColor DarkGray
}

# --------------------------------------------------------------------------------------------
# 3. cmake 配置
# --------------------------------------------------------------------------------------------
Write-Host "`n[2/4] cmake configure ..." -ForegroundColor Yellow
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

& cmake -S $nativeDir -B $buildDir `
    -G "Visual Studio 17 2022" -A x64 `
    -DCMAKE_TOOLCHAIN_FILE="$toolchain" `
    -DVCPKG_TARGET_TRIPLET="x64-windows"
if ($LASTEXITCODE -ne 0) { throw "cmake configure 失败" }

# --------------------------------------------------------------------------------------------
# 4. cmake 构建
# --------------------------------------------------------------------------------------------
Write-Host "`n[3/4] cmake build ($BuildPreset) ..." -ForegroundColor Yellow
& cmake --build $buildDir --config $BuildPreset --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build 失败" }

# --------------------------------------------------------------------------------------------
# 5. 验证 DLL
# --------------------------------------------------------------------------------------------
Write-Host "`n[4/4] 验证 DLL ..." -ForegroundColor Yellow
$dllPath = Join-Path $repoRoot "YamaEngine\Native\YamaEngine.Native.dll"
if (-not (Test-Path $dllPath)) {
    # 不同配置子目录回退
    $dllPath = Get-ChildItem -Path (Join-Path $repoRoot "YamaEngine\Native") -Filter "YamaEngine.Native.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $dllPath -or -not (Test-Path $dllPath)) {
    throw "未找到输出的 YamaEngine.Native.dll。检查 CMakeLists 输出目录。"
}
Write-Host "`n=== 构建成功 ===" -ForegroundColor Green
Write-Host "DLL: $dllPath"
Write-Host "`n注意：C# 运行时通过 JoltPhysicsBridge.cs 的 NativeLibrary.Load 加载此 DLL。"
Write-Host "若加载失败，确认 DLL 在 YamaEngine\Native\ 下，或设置动态库搜索路径。"
