#!/usr/bin/env python3
"""Tuan-CLI 插件式子命令适配层（纯 stdlib，不 import tuan_cli）。

提供 build_parser()/handle()，由 Tuan 的插件加载器注册为 `tuan yama ...`。
第十九轮：`codeoss-minimal` / `invoke` / `capabilities` 真委派到
yama_bridge.invoke()（真实只读动作）；其余命令保留可执行意图并标注
delegated / verified_by。不在本文件里执行任何写操作。
"""
from __future__ import annotations
import argparse, json, os, sys

SUBCOMMANDS = ("build", "test", "smoke", "snapshot", "rollback", "ledger", "sota",
                "coupled", "globalblocks", "solver-spectrum",
                "codeoss-minimal", "invoke", "capabilities")


def build_parser():
    p = argparse.ArgumentParser(prog="tuan yama")
    sub = p.add_subparsers(dest="yama_cmd")
    for c in SUBCOMMANDS:
        sp = sub.add_parser(c)
        if c == "smoke":
            sp.add_argument("--frames", type=int, default=60)
        if c == "rollback":
            sp.add_argument("--tick", type=int, default=0)
        if c == "invoke":
            sp.add_argument("--action", type=str, default="capabilities")
            sp.add_argument("--params", type=str, default="{}")
    return p


def handle(argv):
    a = build_parser().parse_args(argv)
    if not a.yama_cmd:
        return {"ok": False, "error": "missing subcommand", "available": list(SUBCOMMANDS)}
    import os
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from yama_bridge import invoke as bridge_invoke

    if a.yama_cmd == "codeoss-minimal":
        r = bridge_invoke("verify_codeoss_minimal")
        return {"ok": r["ok"], "command": "codeoss-minimal", "delegated": True,
                "action": "verify_codeoss_minimal", "result": r.get("result"),
                "source": r.get("source")}
    if a.yama_cmd == "capabilities":
        r = bridge_invoke("capabilities")
        return {"ok": r["ok"], "command": "capabilities", "delegated": True,
                "action": "capabilities", "result": r.get("result"),
                "source": r.get("source")}
    if a.yama_cmd == "invoke":
        try:
            extra = json.loads(a.params)
        except json.JSONDecodeError as e:
            return {"ok": False, "error": "params must be JSON: " + str(e)}
        r = bridge_invoke(a.action, extra)
        return {"ok": r["ok"], "command": "invoke", "delegated": True,
                "action": a.action, "result": r.get("result"),
                "source": r.get("source"), "error": r.get("error")}
    if a.yama_cmd == "sota":
        r = bridge_invoke("sota_status")
        return {"ok": r["ok"], "command": "sota", "delegated": True,
                "status": "G0-G3 closed; G4-G8 open; NOT SOTA",
                "source": "docs/SOTA_GAP_ANALYSIS.md",
                "verified_by": r.get("source")}
    if a.yama_cmd == "coupled":
        return {"ok": True, "command": "coupled",
                "intent": "YrPhysicsPipeline.UseCoupledMassMatrix + LastCoupledTrace",
                "verified_by": "YamaEngine/Reality/YrContactSolver.cs",
                "trace_fields": ["TotalSteps", "CoupledSteps", "FallbackSteps",
                                 "MaxOffDiagonal", "LastK", "LastUsedCoupling"]}
    if a.yama_cmd == "globalblocks":
        return {"ok": True, "command": "globalblocks",
                "intent": "YrPhysicsPipeline.UseGlobalBlockSolve + LastGlobalBlockTrace",
                "verified_by": "YamaEngine/Reality/YrContactSolver.cs",
                "trace_fields": ["Groups", "MaxPointsPerGroup", "TotalIterations",
                                 "SolvedGroups", "FallbackGroups", "MaxCrossBlock",
                                 "LastUsedGlobal"]}
    if a.yama_cmd == "solver-spectrum":
        return {"ok": True, "command": "solver-spectrum",
                "intent": "YrBlockLinearSolvers + YrBlockLinearTrace",
                "verified_by": "YamaEngine/Reality/YrBlockLinearSolvers.cs",
                "modes": ["Direct", "BlockJacobi", "BlockGaussSeidel",
                          "PreconditionedCG"],
                "trace_fields": ["Mode", "InnerIterations", "InitialResidual",
                                 "FinalResidual", "MaxResidual",
                                 "SolutionDiscrepancy", "Converged",
                                 "UsedFallback", "ResidualMonotonic"],
                "gate": "residual AND solution drift -> safe fallback to Direct"}
    if a.yama_cmd == "smoke" and a.frames <= 0:
        return {"ok": False, "error": "frames must be > 0"}
    if a.yama_cmd == "rollback" and a.tick < 0:
        return {"ok": False, "error": "tick must be >= 0"}
    return {"ok": True, "command": a.yama_cmd, "delegated": True,
            "action": "command_intent",
            "intent": "see integrations/tuan_cli/mcp_server.py INTENTS",
            "args": {k: v for k, v in vars(a).items() if k != "yama_cmd"}}


if __name__ == "__main__":
    import json, sys
    print(json.dumps(handle(sys.argv[1:]), ensure_ascii=False))
