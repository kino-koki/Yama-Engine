<#
.SYNOPSIS
  yama引擎：可复现构建 + 验证脚本（地基阶段）
.DESCRIPTION
  1) 检查 .NET 8 SDK
  2) 还原 NuGet
  3) 构建解决方案 (Debug | x64)
  4) 运行测试
  5) 生成真实构建报告 artifacts/build_report.txt
  6) 汇总 csproj 中仍被 <Compile Remove> 排除的文件（技术债清单）
  前提：本机需安装 .NET 8 SDK (8.0.422) 与 Windows App SDK 1.8。
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$sln  = Join-Path $root 'YamaEditor.sln'
$reportDir = Join-Path $root 'artifacts'
$report = Join-Path $reportDir 'build_report.txt'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

function Log($msg) {
    $t = Get-Date -Format 'HH:mm:ss'
    "$t $msg" | Tee-Object -FilePath $report -Append
}

Log "===== YamaEngine build_verify ====="
Log "Root : $root"

# 0. 前置检查
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Log "ERROR: 未找到 dotnet。请安装 .NET 8 SDK (8.0.422)。"
    exit 1
}
Log "dotnet: $(dotnet --version)"

# 1. 还原
Log "--- dotnet restore ---"
dotnet restore $sln *>&1 | Tee-Object -FilePath $report -Append
if ($LASTEXITCODE -ne 0) { Log "RESTORE FAILED (exit $LASTEXITCODE)"; exit $LASTEXITCODE }

# 2. 构建
Log "--- dotnet build (Debug|x64) ---"
dotnet build $sln -c Debug -p:Platform=x64 --no-restore *>&1 | Tee-Object -FilePath $report -Append
$buildExit = $LASTEXITCODE
Log "build exit code: $buildExit"

# 3. 测试
if ($buildExit -eq 0) {
    Log "--- dotnet test ---"
    dotnet test (Join-Path $root 'YamaEngine.Tests/YamaEngine.Tests.csproj') -c Debug --no-build *>&1 | Tee-Object -FilePath $report -Append
    Log "test exit code: $LASTEXITCODE"
}

# 4. 技术债清单：仍被 <Compile Remove> 排除的文件
Log "--- 技术债：csproj 中 <Compile Remove> 项（未经编译验证的文件） ---"
Get-ChildItem -Path $root -Recurse -Filter *.csproj | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    [regex]::Matches($content, '<Compile Remove="([^"]+)"') | ForEach-Object {
        Log "  [$($_.Groups[1].Value)] <- $($_.Name)"
    }
}

Log "===== DONE (build exit: $buildExit) ====="
Log "报告已写入: $report"
exit $buildExit
