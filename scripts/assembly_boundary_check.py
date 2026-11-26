#!/usr/bin/env python3
"""程序集边界耦合扫描（静态，无需 SDK）。

当前 YamaEngine 是单一巨型 Library（csproj 里 50+ 条 <Compile Include> 全部编进一个程序集），
docs/FOUNDATION_STATUS.md §5 提议按内聚性拆分。本脚本量化各顶层目录之间的
`using YamaEngine.X` 交叉引用，为拆分顺序提供数据，不改动任何代码。
"""
import os, re, glob, json, collections
ROOT = os.getcwd()
ENG = os.path.join(ROOT, "YamaEngine")
if not os.path.isdir(ENG):
    print("未找到 YamaEngine/，退出"); raise SystemExit(0)
dirs = sorted(d for d in os.listdir(ENG) if os.path.isdir(os.path.join(ENG, d)))
refs = collections.defaultdict(collections.Counter)
files = collections.Counter()
for f in glob.glob(ENG + "/**/*.cs", recursive=True):
    rel = os.path.relpath(f, ENG).replace(os.sep, "/")
    if "/" not in rel: continue
    top = rel.split("/")[0]
    files[top] += 1
    s = open(f, encoding="utf-8", errors="replace").read()
    for m in re.findall(r"using\s+YamaEngine\.([A-Za-z0-9_]+)", s):
        for d in dirs:
            if m.lower() == d.lower() and d != top:
                refs[top][d] += 1
out = {"directories": {d: files[d] for d in dirs}, "cross_using": {k: dict(v) for k, v in refs.items()},
       "top_couplers": sorted(((k, sum(v.values())) for k, v in refs.items()), key=lambda x: -x[1])[:12]}
with open(os.path.join(ROOT, "assembly_boundary_report.json"), "w", encoding="utf-8") as fh:
    json.dump(out, fh, ensure_ascii=False, indent=2)
print("目录数:", len(dirs), "| .cs 总数:", sum(files.values()))
print("跨目录 using 引用最多的 12 个目录：")
for k, n in out["top_couplers"]: print(f"   {k:22s} {n:5d} 次外部 using")
print("报告 -> assembly_boundary_report.json（信息项，不阻断 CI）")
