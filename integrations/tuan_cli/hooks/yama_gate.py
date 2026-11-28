#!/usr/bin/env python3
"""Tuan hook：Yama 门禁的 stdin-JSON / stdout-JSON 决策脚本（纯 stdlib）。

不在 Tuan 进程里跑 .NET（避免硬依赖）；只做可判定的静态契约核查，
把「哪些门禁需要在真机/CI 复核」作为结构化上下文回给 Agent。
"""
from __future__ import annotations
import argparse, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
ROOT = os.path.dirname(ROOT)  # integrations/tuan_cli -> repo root


def checks():
    out = []
    def has(rel, key=None):
        p = os.path.join(ROOT, rel)
        ok = os.path.isfile(p)
        if ok and key:
            try:
                ok = key in open(p, "r", encoding="utf-8").read()
            except OSError:
                ok = False
        out.append({"file": rel, "present": ok})
        return ok
    has("docs/SELF_RESEARCH_BOUNDARY.md", "不借鉴")
    has("docs/THIRD_PARTY_SPX.md", "MIT")
    # 关键词用 ASCII "G0"：en dash 关键词漂移会把「文件存在」误报成「契约缺失」
    has("docs/SOTA_GAP_ANALYSIS.md", "G0")
    has("ide/codeoss/UPSTREAM.lock.json", "1.148.0")
    has("ide/codeoss/product.json", "MIT")
    has(".gitmodules", "microsoft/vscode")
    has("YamaEngine.Tests/RepoContractTests.cs", "TestSelfResearchBoundarySpdx")
    has("YamaEngine/Reality/YrContactSolver.cs", "UseCoupledMassMatrix")
    has("YamaEngine/Reality/YrNarrowPhase.cs", "UseCoupledMassReduction")
    has("docs/THIRD_PARTY_INVENTORY.json", "package_references")
    has("docs/THIRD_PARTY_INVENTORY.json", "optional_physics_backends")
    has("YamaEngine.Tests/RealityPhysicsFifteenthTests.cs", "TestR15")
    has("YamaEngine/Reality/YrContactBlockMatrix.cs", "BuildGlobalK")
    has("YamaEngine/Reality/YrContactSolver.cs", "UseGlobalBlockSolve")
    has("YamaEngine.Tests/RealityPhysicsSeventeenthTests.cs", "TestR17")
    has("YamaEngine/Reality/YrBlockLinearSolvers.cs", "YrBlockSolveMode")
    has("YamaEngine/Reality/YrContactSolver.cs", "GlobalBlockSolveMode")
    has("YamaEngine.Tests/RealityPhysicsEighteenthTests.cs", "TestR18")
    # 第十九轮：Code - OSS 最轻量构建面 + Tuan 真实被调用能力
    has("ide/codeoss/MINIMAL.lock.json", "web-only-minimal")
    has("ide/codeoss/product.json", "yamaMinimal")
    has("scripts/build_codeoss_minimal.py", "--emit-manifest")
    has("ci/codeoss-gate.yml", "codeoss-minimal")
    has("integrations/tuan_cli/yama_bridge.py", "def invoke")
    has("integrations/tuan_cli/yama_actions.json", "callable-actions")
    has("integrations/tuan_cli/yama_cli.py", "subcommand.handle")
    has("YamaEngine.Tests/CodeOssMinimalTests.cs", "TestR19")
    # 第二十轮：Python 门禁进真实 CI + 引擎侧桥接接线 + fetch 口径不回漂
    has("YamaEngine.Tests/CodeOssMinimalBridgeTests.cs", "TestR20")
    has("scripts/fetch_codeoss.py", "本仓库不编译")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--event", default="PostToolUse")
    a = ap.parse_args()
    try:
        payload = json.load(sys.stdin)
    except Exception:
        payload = {}
    c = checks()
    missing = [x["file"] for x in c if not x["present"]]
    result = {
        "event": a.event,
        "decision": "warn" if missing else "allow",
        "reason": ("Yama 契约文件缺失: " + ", ".join(missing)) if missing
                  else "Yama 静态契约完整；真机 GPU / 规模 / 平台门禁仍需 CI 与实机复核",
        "checked": len(c),
        "missing": missing,
        "tool": payload.get("tool_name") or payload.get("tool") or "",
        "note": "绝不宣称已达 SOTA；G4–G8 见 docs/SOTA_GAP_ANALYSIS.md",
    }
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
