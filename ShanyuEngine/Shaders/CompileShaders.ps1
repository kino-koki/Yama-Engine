<#
.SYNOPSIS
    Compiles the Shanyu Engine HLSL shaders into compiled shader object (.cso)
    bytecode files using the DirectX shader compiler fxc.exe.

.DESCRIPTION
    Compiles every entry point in the Shaders directory into a standalone .cso
    file. fxc.exe is located automatically (PATH first, then the installed
    Windows SDK). The script targets shader model 5.0 by default so the output
    runs on every D3D11 feature level 11.0+ device; pass -ShaderModel 6_0 if you
    want to compile with dxc instead (see notes).

    Output files are written to a Compiled/ subdirectory next to the sources by
    default:
        BasicPBRVS.cso, BasicPBRPS.cso,
        FullScreenTriangleVS.cso, FullScreenTrianglePS.cso

.PARAMETER OutputDir
    Directory for the .cso files. May be relative (to this script) or absolute.

.PARAMETER ShaderModel
    Shader model minor version, e.g. "5_0" (default) or "6_0". Note that fxc
    only supports up to 5_1; for 6_0+ use dxc (set -Compiler dxc).

.PARAMETER Compiler
    Which compiler to use: "fxc" (default) or "dxc".

.PARAMETER DebugBuild
    Embed debug information (/Zi) and disable optimization. Off by default.
    (Named -DebugBuild because -Debug is a reserved PowerShell common parameter.)

.EXAMPLE
    .\CompileShaders.ps1
    Compiles all shaders to .\Compiled using fxc at SM 5.0, optimized.

.EXAMPLE
    .\CompileShaders.ps1 -ShaderModel 6_0 -Compiler dxc
    Compiles all shaders using dxc at SM 6.0.

.EXAMPLE
    .\CompileShaders.ps1 -OutputDir C:\Build\Shaders -DebugBuild
    Writes unoptimized .cso files with debug info to an absolute path.
#>

[CmdletBinding()]
param(
    [string]$OutputDir   = "Compiled",
    [ValidatePattern('^\d+_\d+$')]
    [string]$ShaderModel = "5_0",
    [ValidateSet('fxc', 'dxc', IgnoreCase = $true)]
    [string]$Compiler    = "fxc",
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"

# --- Resolve paths ---------------------------------------------------------
# This script lives in ShanyuEngine/Shaders alongside the .hlsl sources.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ShaderDir = $ScriptDir

if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutRoot = $OutputDir
} else {
    $OutRoot = Join-Path $ShaderDir $OutputDir
}

# --- Locate the shader compiler -------------------------------------------
function Find-Compiler {
    param([string]$Name)

    # 1. Already on PATH?
    $cmd = Get-Command "$Name.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 2. Windows SDK bin folders (newest version first).
    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    foreach ($root in $sdkRoots) {
        if (-not (Test-Path $root)) { continue }
        $version = Get-ChildItem $root -Directory |
                   Where-Object { $_.Name -match '^\d+\.' } |
                   Sort-Object Name -Descending |
                   Select-Object -First 1
        if ($version) {
            foreach ($arch in @('x64', 'x86')) {
                $candidate = Join-Path $version.FullName "$arch\$Name.exe"
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }
    return $null
}

$compilerExe = Find-Compiler -Name $Compiler
if (-not $compilerExe) {
    switch ($Compiler.ToLowerInvariant()) {
        'fxc' {
            Write-Error @"
$Compiler.exe was not found. Install the Windows SDK (or the standalone
DirectX SDK / Windows Graphics Tools feature) and ensure $Compiler.exe is on
your PATH, or run this script from a Developer Command Prompt.
"@
        }
        'dxc' {
            Write-Error @"
$Compiler.exe was not found. The DirectX Shader Compiler ships with recent
Windows SDKs or can be installed from https://github.com/microsoft/DirectXShaderCompiler.
"@
        }
    }
    exit 1
}

# --- Validate shader model vs. compiler -----------------------------------
if ($Compiler -eq 'fxc' -and $ShaderModel -match '^6_') {
    Write-Error "fxc.exe cannot compile shader model $ShaderModel. Use -Compiler dxc for SM 6.0+."
    exit 1
}

Write-Host "Compiler : $compilerExe"
Write-Host "Model    : $ShaderModel"
Write-Host "Shaders  : $ShaderDir"
Write-Host "Output   : $OutRoot"
if ($DebugBuild) { Write-Host "Debug    : ON (unoptimized, /Zi)" }

# --- Ensure the output directory exists -----------------------------------
if (-not (Test-Path $OutRoot)) {
    New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null
}

# --- Shader compilation table ---------------------------------------------
# Each entry: Source file, Entry point, Stage (vs/ps), Output file name.
$shaders = @(
    @{ Source = "BasicPBR.hlsl";           Entry = "VSMain"; Stage = "vs"; Out = "BasicPBRVS.cso" },
    @{ Source = "BasicPBR.hlsl";           Entry = "PSMain"; Stage = "ps"; Out = "BasicPBRPS.cso" },
    @{ Source = "FullScreenTriangle.hlsl"; Entry = "VSMain"; Stage = "vs"; Out = "FullScreenTriangleVS.cso" },
    @{ Source = "FullScreenTriangle.hlsl"; Entry = "PSMain"; Stage = "ps"; Out = "FullScreenTrianglePS.cso" }
)

# --- Compile ---------------------------------------------------------------
$failed  = 0
$succeeded = 0

foreach ($s in $shaders) {
    $srcPath = Join-Path $ShaderDir $s.Source
    $outPath = Join-Path $OutRoot  $s.Out
    $profile = "$($s.Stage)_$ShaderModel"

    if (-not (Test-Path $srcPath)) {
        Write-Warning "Skipping $($s.Source): source file not found."
        $failed++
        continue
    }

    Write-Host ""
    Write-Host "Compiling $($s.Entry) ($profile) <- $($s.Source)"

    # Build the argument list. fxc and dxc share most flags.
    $compilerArgs = @(
        "/T", $profile,
        "/E", $s.Entry,
        "/Fo", $outPath,
        "/I", $ShaderDir
    )

    if ($DebugBuild) {
        $compilerArgs += @("/Zi", "/Od", "/Qstrip_reflect")   # debug info, no opt
    } else {
        $compilerArgs += @("/O3")                              # full optimization
    }

    # Always define the include root and disable the shader-as-library warning
    # noise for dxc; harmless for fxc.
    if ($Compiler -eq 'dxc') {
        $compilerArgs += @("-Wno-parentheses-equality")
    }

    $compilerArgs += $srcPath

    & $compilerExe @compilerArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "FAILED: $($s.Source) ($($s.Entry)). Exit code: $LASTEXITCODE"
        $failed++
    } else {
        $size = (Get-Item $outPath).Length
        Write-Host ("  -> {0} ({1} bytes)" -f $s.Out, $size)
        $succeeded++
    }
}

# --- Summary ---------------------------------------------------------------
Write-Host ""
Write-Host ("Done: {0} succeeded, {1} failed." -f $succeeded, $failed)
if ($failed -gt 0) {
    exit 1
}
exit 0
