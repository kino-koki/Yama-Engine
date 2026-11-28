#!/usr/bin/env python3
"""Tuan-CLI 插件式子命令适配层（纯 stdlib，不 import tuan_cli）。

提供 build_parser()/handle()，由 Tuan 的插件加载器注册为 `tuan yama ...`。
不在本文件里执行任何写操作；所有命令返回可执行意图或只读状态。
"""
from __future__ import annotations
import argparse

SUBCOMMANDS = ("build", "test", "smoke", "snapshot", "rollback", "ledger", "sota",
                "coupled", "globalblocks", "solver-spectrum")


def build_parser():
    p = argparse.ArgumentParser(prog="tuan yama")
    sub = p.add_subparsers(dest="yama_cmd")
    for c in SUBCOMMANDS:
        sp = sub.add_parser(c)
        if c == "smoke":
            sp.add_argument("--frames", type=int, default=60)
        if c == "rollback":
            sp.add_argument("--tick", type=int, default=0)
    return p


def handle(argv):
    a = build_parser().parse_args(argv)
    if not a.yama_cmd:
        return {"ok": False, "error": "missing subcommand", "available": list(SUBCOMMANDS)}
    if a.yama_cmd == "sota":
        return {"ok": True, "command": "sota",
                "status": "G0-G3 closed; G4-G8 open; NOT SOTA",
                "source": "docs/SOTA_GAP_ANALYSIS.md"}
    if a.yama_cmd == "coupled":
        return {"ok": True, "command": "coupled",
                "intent": "YrPhysicsPipeline.UseCoupledMassMatrix + LastCoupledTrace",
                "trace_fields": ["TotalSteps", "CoupledSteps", "FallbackSteps",
                                 "MaxOffDiagonal", "LastK", "LastUsedCoupling"]}
    if a.yama_cmd == "globalblocks":
        return {"ok": True, "command": "globalblocks",
                "intent": "YrPhysicsPipeline.UseGlobalBlockSolve + LastGlobalBlockTrace",
                "trace_fields": ["Groups", "MaxPointsPerGroup", "TotalIterations",
                                 "SolvedGroups", "FallbackGroups", "MaxCrossBlock",
                                 "LastUsedGlobal"]}
    if a.yama_cmd == "solver-spectrum":
        return {"ok": True, "command": "solver-spectrum",
                "intent": "YrBlockLinearSolvers + YrBlockLinearTrace",
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
    return {"ok": True, "command": a.yama_cmd,
            "intent": "see integrations/tuan_cli/mcp_server.py INTENTS",
            "args": {k: v for k, v in vars(a).items() if k != "yama_cmd"}}


if __name__ == "__main__":
    import json, sys
    print(json.dumps(handle(sys.argv[1:]), ensure_ascii=False))
