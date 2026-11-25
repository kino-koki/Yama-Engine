#!/usr/bin/env python3
"""Yama-Engine 静态仓库守卫（无需 .NET SDK）。

检查项：
  1. 个人 / 机器相关绝对路径（error）
  2. DCC 自动探测里的 Windows 常见安装路径（warn，允许但需可配置覆盖）
  3. csproj <Compile Include> 未覆盖的孤儿 .cs（error）
  4. 必需治理文件存在（error）
  5. 占位 / 未完成信号台账（仅输出 CSV，不阻断）

退出码：有 error 则 1；仅 warn 则 0。
"""
import os, re, sys, glob, csv

ROOT = os.getcwd()
ENGINE = os.path.join(ROOT, "YamaEngine")
errors, warns = [], []

# ---- 1. 个人 / 机器相关绝对路径 ------------------------------------
# 这些路径会在别人机器上直接抛异常，或泄露开发者本机信息。
PERSONAL = [
    (r"李恩", "开发者姓名"),
    (r"AppData[/\\]", "用户级 AppData 目录"),
    (r"/Users/[^/\s]+/", "macOS 用户目录"),
    (r"/home/[^/\s]+/", "Linux 用户主目录"),
    (r"[A-Za-z]:[\\/]+Users[\\/]", "Windows 用户目录"),
    (r"[A-Za-z]:[\\/]+Windows[\\/]+System32", "系统目录写入建议"),
    (r"E:[\\/]ccc", "历史构建缓存盘符"),
]
DCC_FALLBACK = re.compile(r"C:[\\/]+Program Files", re.I)

for f in glob.glob(ROOT + "/**/*.cs", recursive=True):
    if "/artifacts/" in f or "/obj/" in f or "/bin/" in f:
        continue
    try:
        lines = open(f, encoding="utf-8", errors="replace").read().splitlines()
    except OSError:
        continue
    rel = os.path.relpath(f, ROOT).replace(os.sep, "/")
    for i, line in enumerate(lines, 1):
        for pat, desc in PERSONAL:
            if re.search(pat, line):
                errors.append(f"[PATH] {rel}:{i}  含{desc}: {line.strip()[:120]}")
        if DCC_FALLBACK.search(line):
            warns.append(f"[DCC ] {rel}:{i}  Windows 默认安装路径（应可由环境变量覆盖）: {line.strip()[:100]}")

# ---- 2. 孤儿 .cs 检查 ------------------------------------------------
# YamaEngine.csproj 设了 EnableDefaultCompileItems=false，新建目录若忘了加
# <Compile Include="目录\**\*.cs" />，里面的代码会静默不参与编译。
CSPROJ = os.path.join(ENGINE, "YamaEngine.csproj")
if os.path.exists(CSPROJ):
    s = open(CSPROJ, encoding="utf-8", errors="replace").read()
    incs = [x.replace("\\", "/") for x in re.findall(r'<Compile\s+Include="([^"]+)"', s)]
    rems = [x.replace("\\", "/") for x in re.findall(r'<Compile\s+Remove="([^"]+)"', s)]
    wild = sorted(set(x.split("/")[0] for x in incs if "**" in x))
    singles = set(x for x in incs if "**" not in x)
    removed = set()
    for r in rems:
        removed |= {os.path.relpath(p, ENGINE).replace(os.sep, "/")
                    for p in glob.glob(os.path.join(ENGINE, r), recursive=True)}
    for f in glob.glob(ENGINE + "/**/*.cs", recursive=True):
        rel = os.path.relpath(f, ENGINE).replace(os.sep, "/")
        if rel in removed or rel in singles:
            continue
        top = rel.split("/")[0]
        if "/" in rel and top not in wild:
            errors.append(f"[ORPHAN] YamaEngine/{rel}  顶层目录 {top} 未被 csproj <Compile Include> 覆盖")
else:
    errors.append("[ORPHAN] 找不到 YamaEngine/YamaEngine.csproj")

# ---- 3. 必需治理文件 -------------------------------------------------
REQUIRED = ["README.md", "LICENSE", ".gitignore", ".editorconfig", "CONTRIBUTING.md",
            "SECURITY.md", "CHANGELOG.md", "CODE_OF_CONDUCT.md", "NOTICE"]
for r in REQUIRED:
    if not os.path.exists(os.path.join(ROOT, r)):
        errors.append(f"[GOV  ] 缺少治理文件: {r}")

# ---- 4. 占位信号台账（不阻断） --------------------------------------
SIGS = ["TODO", "FIXME", "Stub", "stub", "Mock", "mock", "Placeholder", "placeholder",
        "NotImplementedException", "NotSupportedException", "临时", "待实现", "原型", "prototype", "未实现"]
rows = []
for f in glob.glob(ROOT + "/**/*.cs", recursive=True):
    if "/artifacts/" in f or "/obj/" in f or "/bin/" in f:
        continue
    try:
        lines = open(f, encoding="utf-8", errors="replace").read().splitlines()
    except OSError:
        continue
    rel = os.path.relpath(f, ROOT).replace(os.sep, "/")
    for i, line in enumerate(lines, 1):
        hit = [k for k in SIGS if k in line]
        if hit and line.strip() and not line.strip().startswith("// ///"):
            rows.append((rel, i, "|".join(hit), line.strip()[:160]))
with open(os.path.join(ROOT, "placeholder_inventory.csv"), "w", encoding="utf-8", newline="") as fh:
    w = csv.writer(fh)
    w.writerow(["file", "line", "signals", "text"])
    w.writerows(rows)

# ---- 输出 ------------------------------------------------------------
print(f"扫描完成：error={len(errors)}  warn={len(warns)}  占位台账={len(rows)} 行 -> placeholder_inventory.csv")
for w in warns[:15]:
    print("  WARN  " + w)
if len(warns) > 15:
    print(f"  ... 另有 {len(warns)-15} 条 WARN")
for e in errors[:40]:
    print("  ERROR " + e)
if len(errors) > 40:
    print(f"  ... 另有 {len(errors)-40} 条 ERROR")
print("结果：" + ("FAIL（存在阻断项）" if errors else "PASS"))
sys.exit(1 if errors else 0)
