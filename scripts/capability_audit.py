#!/usr/bin/env python3
"""Yama-Engine 能力审计（无需 .NET SDK / GPU / Windows）。

读取 docs/capability_matrix.csv，逐项核验：
  1. yama_files 列的远端文件在当前仓库是否真实存在
  2. 4 个 JSON Schema 是否合法
  3. 模板 / 导出契约 JSON 是否合法且符合最小字段
  4. 治理文件是否齐全（缺失只 WARN，不阻断，因部分在分支 A）
  5. BUILD_DECISIONS D0 的热重载双轨命名空间证伪
  6. 主机 SDK stub 是否仍在默认编译路径（信息项）
  7. 占位信号台账输出 placeholder_inventory.csv

退出码：出现「非预期缺失」时为 1；仅 WARN 时为 0。
"""
import os, sys, csv, json, glob, re, collections

ROOT = os.getcwd()
errors, warns, info = [], [], []
report = {"total": 0, "verified": 0, "expected_gaps": 0, "unexpected_missing": 0,
          "schemas": {}, "templates": {}, "governance": {}, "dual_track": {}, "console_stub": {}}

# ---- 1. 能力矩阵逐行核验 ----------------------------------------------
MATRIX = os.path.join(ROOT, "docs", "capability_matrix.csv")
if not os.path.exists(MATRIX):
    errors.append("[MATRIX] 缺少 docs/capability_matrix.csv")
else:
    with open(MATRIX, encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    report["total"] = len(rows)
    # 这些状态本来就没有对应文件，属预期缺口
    EXPECTED = ("缺失", "N/A", "占位·NDA", "未验证·分支B", "阻塞·真机", "分支A", "分支B")
    for r in rows:
        files = [x.strip() for x in r["yama_files"].split("|") if x.strip() and x.strip() != "—"]
        status = r["status"]
        if not files:
            continue
        hit = [p for p in files if os.path.exists(os.path.join(ROOT, p))]
        if hit:
            report["verified"] += 1
        elif any(k in status for k in EXPECTED):
            report["expected_gaps"] += 1
            info.append(f"[EXPECTED-GAP] {r['id']} {r['godot35_capability']} -> {status}")
        else:
            report["unexpected_missing"] += 1
            errors.append(f"[MISSING] {r['id']} {r['godot35_capability']} 声明的文件一个都不存在: {files}")

# ---- 2. JSON Schema 合法性 ---------------------------------------------
for p in sorted(glob.glob(os.path.join(ROOT, "schemas", "*.json"))):
    rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
    try:
        j = json.load(open(p, encoding="utf-8"))
        ok = isinstance(j, dict) and "$schema" in j and "type" in j
        report["schemas"][rel] = "OK" if ok else "INVALID"
        if not ok: errors.append(f"[SCHEMA] {rel} 缺少 $schema/type")
    except Exception as e:
        report["schemas"][rel] = "PARSE-FAIL"
        errors.append(f"[SCHEMA] {rel} 不是合法 JSON: {e}")

# ---- 3. 模板 / 导出契约 ------------------------------------------------
for p in sorted(glob.glob(os.path.join(ROOT, "Templates", "**", "*.json"), recursive=True)):
    rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
    if os.path.getsize(p) == 0:
        report["templates"][rel] = "EMPTY"
        warns.append(f"[TEMPLATE] {rel} 为空文件（浅克隆/占位），跳过内容校验")
        continue
    try:
        j = json.load(open(p, encoding="utf-8"))
        report["templates"][rel] = "OK"
        if rel.endswith("yama.project.json"):
            for k in ("name", "engine", "formatVersion", "startupScene", "folders"):
                if k not in j: errors.append(f"[TEMPLATE] {rel} 缺字段 {k}")
    except Exception as e:
        report["templates"][rel] = "PARSE-FAIL"
        errors.append(f"[TEMPLATE] {rel} 不是合法 JSON: {e}")
for p in sorted(glob.glob(os.path.join(ROOT, "Templates", "**", "Main.yama"), recursive=True)):
    rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
    if os.path.getsize(p) == 0:
        report["templates"][rel] = "EMPTY"
        warns.append(f"[SCENE] {rel} 为空文件（浅克隆/占位），跳过内容校验")
        continue
    try:
        j = json.load(open(p, encoding="utf-8"))
        ok = j.get("Version") == 1 and isinstance(j.get("Objects"), list)
        report["templates"][rel] = "OK" if ok else "INVALID"
        if not ok: errors.append(f"[SCENE] {rel} 不满足 yscene 最小结构")
    except Exception as e:
        report["templates"][rel] = "PARSE-FAIL"
        errors.append(f"[SCENE] {rel} 不是合法 JSON: {e}")

# ---- 4. 治理文件（WARN，不阻断）---------------------------------------
GOV = ["README.md", "LICENSE", ".gitignore", ".editorconfig", "CONTRIBUTING.md",
       "SECURITY.md", "CHANGELOG.md", "CODE_OF_CONDUCT.md", "NOTICE"]
for g in GOV:
    ok = os.path.exists(os.path.join(ROOT, g))
    report["governance"][g] = "OK" if ok else "MISSING"
    if not ok: warns.append(f"[GOV] {g} 缺失（分支 feat/repo-gap-closure 已补齐，合并后即 OK）")

# ---- 5. D0 热重载双轨命名空间证伪 -------------------------------------
a = os.path.join(ROOT, "YamaEngine", "Scripting", "ScriptHotReload.cs")
b = os.path.join(ROOT, "YamaEngine", "PluginSystem", "ScriptHotReloadSystem.cs")
def ns_of(p):
    if not os.path.exists(p): return None
    s = open(p, encoding="utf-8", errors="replace").read()
    m = re.findall(r"namespace\s+([\w\.]+)", s)
    return m[0] if m else None
na, nb = ns_of(a), ns_of(b)
report["dual_track"] = {"scripting_ns": na, "pluginsystem_ns": nb,
                        "same_namespace": (na is not None and na == nb),
                        "cs0101_risk": False}
if na and nb and na != nb:
    info.append("[D0] 两套热重载位于不同命名空间，恢复 Scripting 版不构成 CS0101；真问题是功能双轨（见 BUILD_DECISIONS.md D1）")
elif na and na == nb:
    errors.append("[D0] 两套热重载命名空间相同，恢复前必须消歧")

# ---- 6. 主机 SDK stub（信息项）----------------------------------------
c = os.path.join(ROOT, "YamaEngine", "Platforms", "ConsoleSDKAbstraction.cs")
if os.path.exists(c):
    s = open(c, encoding="utf-8", errors="replace").read()
    stubs = len(re.findall(r"class\s+\w*SDKStub", s))
    guarded = "#if YAMA_WITH_CONSOLE_SDK" in s
    report["console_stub"] = {"stub_classes": stubs, "conditionally_compiled": guarded}
    if stubs and not guarded:
        warns.append(f"[CONSOLE] {stubs} 个 *SDKStub 仍在默认编译路径，建议用 #if YAMA_WITH_CONSOLE_SDK 隔离")

# ---- 7. 占位台账 ------------------------------------------------------
SIGS = ["TODO", "FIXME", "Stub", "Mock", "Placeholder", "NotImplementedException",
        "NotSupportedException", "临时", "待实现", "原型", "prototype", "未实现"]
inv = []
for f in glob.glob(os.path.join(ROOT, "**", "*.cs"), recursive=True):
    if any(x in f for x in ("/artifacts/", "/obj/", "/bin/")): continue
    try: lines = open(f, encoding="utf-8", errors="replace").read().splitlines()
    except OSError: continue
    rel = os.path.relpath(f, ROOT).replace(os.sep, "/")
    is_test = rel.startswith("YamaEngine.Tests/") or "/Testing/" in rel or rel.endswith("Tests.cs")
    for i, line in enumerate(lines, 1):
        hit = [k for k in SIGS if k in line]
        if hit and line.strip():
            inv.append((rel, i, "|".join(hit), "test" if is_test else "production", line.strip()[:150]))
with open(os.path.join(ROOT, "placeholder_inventory.csv"), "w", encoding="utf-8", newline="") as fh:
    w = csv.writer(fh); w.writerow(["file", "line", "signals", "class", "text"]); w.writerows(inv)
report["placeholder_inventory"] = {"rows": len(inv),
    "production": sum(1 for x in inv if x[3] == "production"),
    "test": sum(1 for x in inv if x[3] == "test")}

# ---- 输出 --------------------------------------------------------------
report["errors"] = errors; report["warns"] = warns; report["info"] = info[:20]
report["result"] = "FAIL" if errors else "PASS"
with open(os.path.join(ROOT, "capability_audit_report.json"), "w", encoding="utf-8") as fh:
    json.dump(report, fh, ensure_ascii=False, indent=2)
print(f"能力审计：{report['total']} 项 / 已核验 {report['verified']} / 预期缺口 {report['expected_gaps']} / 非预期缺失 {report['unexpected_missing']}")
print(f"Schema {len(report['schemas'])} 个 / 模板 {len(report['templates'])} 个 / 占位台账 {len(inv)} 行 -> placeholder_inventory.csv")
for w in warns[:12]: print("  WARN  " + w)
for e in errors[:25]: print("  ERROR " + e)
print("结果：" + report["result"])
sys.exit(1 if errors else 0)
