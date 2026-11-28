#!/usr/bin/env python3
"""自研边界审计：源码里不得出现其他开源/商业引擎的版权/许可/路径搬运特征。

纯 stdlib，运行：
    python scripts/audit_no_engine_copy.py [repo_root]
输出 JSON 到 stdout；发现命中返回非零退出码。

口径见 docs/SELF_RESEARCH_BOUNDARY.md：这是「不借鉴引擎代码」的机器可判定
证明，不是文本关键词黑名单 —— 只匹配源码文件**头部**的版权/许可/路径特征，
不误报 README 的「对标 UE5」等描述性文字。
"""
from __future__ import annotations
import json, os, re, sys

# 只扫源码；文档/表格/描述性文字不参与
EXTS = (".cs", ".cpp", ".h", ".hpp", ".rs", ".c", ".cc")
SKIP_DIRS = {"third_party", "artifacts", "node_modules", ".git",
             "TestResults", "ide/codeoss/upstream", "packages"}
# 只看每个源文件前 HEAD_LINES 行（版权头 / include 区）
HEAD_LINES = 40
# 「引擎源码搬运」特征（显式版权主体 / 源码路径 / 引擎命名空间）
PATTERNS = [
    (r"copyright\s*\(c\).{0,80}(godot\s+engine|godot\s+contributors)", "godot-copyright"),
    (r"copyright\s*\(c\).{0,80}(epic\s+games|unity\s+technologies|nvidia\s+physx)", "commercial-engine-copyright"),
    (r"copyright\s*\(c\).{0,80}(bullet\s+physics|box2d|jolt\s+physics|havok)", "physics-engine-copyright"),
    (r"spdx-license-identifier.{0,60}(godot|unreal|physx|box2d)", "foreign-engine-spdx"),
    (r'#include\s*[<"](?:godot|physx|Px|bullet|Box2D|Urho3D|o3de)/', "foreign-engine-include"),
    (r"namespace\s+(?:godot|physx|Urho3D|O3DE)\b", "foreign-engine-namespace"),
    (r"github\.com/(?:godotengine|EpicGames|Unity-Technologies|NVIDIAGameWorks)/(?:godot|UnrealEngine|PhysX)", "foreign-engine-src-url"),
]
COMPILED = [(re.compile(p, re.I), tag) for p, tag in PATTERNS]


def scan(root: str):
    hits, scanned = [], 0
    for dp, dns, fns in os.walk(root):
        dns[:] = [d for d in dns if d not in SKIP_DIRS and not d.startswith(".")]
        rel_dir = os.path.relpath(dp, root).replace(os.sep, "/")
        if any(rel_dir == s or rel_dir.startswith(s + "/") for s in SKIP_DIRS):
            continue
        for fn in fns:
            if not fn.endswith(EXTS):
                continue
            fp = os.path.join(dp, fn)
            scanned += 1
            try:
                with open(fp, "r", encoding="utf-8", errors="replace") as f:
                    head = "".join([next(f, "") for _ in range(HEAD_LINES)])
            except OSError:
                continue
            for rx, tag in COMPILED:
                m = rx.search(head)
                if m:
                    line = head[:m.start()].count("\n") + 1
                    hits.append({"file": os.path.relpath(fp, root).replace(os.sep, "/"),
                                 "line": line, "tag": tag,
                                 "snippet": m.group(0)[:120]})
    return {"scanned_source_files": scanned, "hits": hits,
            "clean": len(hits) == 0,
            "rule": "no foreign game-engine copyright/license/source-path headers"}


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    r = scan(root)
    print(json.dumps(r, ensure_ascii=False, indent=2))
    return 0 if r["clean"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
