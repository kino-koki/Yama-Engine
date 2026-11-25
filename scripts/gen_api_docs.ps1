# 生成内置离线类参考文档（Godot 3.5 编辑器「内置离线类参考」能力，矩阵 G3-020）
# 规格脚本：需在装有 .NET 10 SDK 与 docfx 的真机执行；本分支不自动运行。
# 用法： powershell -ExecutionPolicy Bypass -File scripts/gen_api_docs.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root "docs/api"
Write-Host "[1/4] 生成 XML 文档注释"
dotnet build (Join-Path $root "YamaEngine/YamaEngine.csproj") -c Release -p:GenerateDocumentationFile=true
Write-Host "[2/4] 检查 docfx"
if (-not (Get-Command docfx -ErrorAction SilentlyContinue)) {
    Write-Warning "未安装 docfx：dotnet tool install -g docfx"; exit 0
}
Write-Host "[3/4] 生成站点"
docfx (Join-Path $PSScriptRoot "docfx.json")
Write-Host "[4/4] 完成 -> $out/index.html"
