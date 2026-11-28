#!/usr/bin/env python3
"""自研 Yama MCP Server（JSON-RPC 2.0 over stdio，纯 stdlib）。

对齐 Tuan-CLI 的零依赖 MCP 客户端协议：
  initialize / notifications/initialized / tools/list / tools/call
第十九轮：tools/call 从「返回意图字符串」升级为**真实委派**到
yama_bridge.invoke()（真跑离线校验、真读契约、真跑 hook、真读差距文档）。
所有动作只读、fail-closed，不在 Agent 进程里跑危险命令。
"""
from __future__ import annotations
import json, sys

PROTOCOL = "2024-11-05"
TOOLS = [
    {"name": "yama_build", "description": "-warnaserror 构建三工程的可执行意图",
     "inputSchema": {"type": "object", "properties": {"configuration": {"type": "string"}},
                     "required": []}},
    {"name": "yama_test", "description": "xUnit 反射套件 + 无头 smoke 的可执行意图",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_smoke", "description": "YamaCLI smoke 60 帧",
     "inputSchema": {"type": "object", "properties": {"frames": {"type": "integer"}},
                     "required": []}},
    {"name": "yama_snapshot", "description": "物理快照（YrPhysicsPipeline.Snapshot）",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_rollback", "description": "位级回滚（Restore/RewindTo）",
     "inputSchema": {"type": "object", "properties": {"tick": {"type": "integer"}},
                     "required": []}},
    {"name": "yama_ledger", "description": "守恒四本账查询（质量/元素/能量/动量）",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_sota_status", "description": "G0–G8 差距状态（不宣称达标）",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_coupled_mass", "description": "3×3 耦合块求解 trace（只读意图）",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_global_blocks", "description": "3m×3m 跨接触点全局块求解 trace（只读意图）",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_solver_spectrum", "description": "块线性求解器谱系 / 收敛诊断 / 双判据退回（只读意图）",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_codeoss_minimal", "description": "真实执行 Code - OSS 最轻量 web-only 干净 MIT 离线校验",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
    {"name": "yama_invoke", "description": "通用委派：真调用 yama_bridge 的只读动作",
     "inputSchema": {"type": "object", "properties": {"action": {"type": "string"},
                      "params": {"type": "object"}}, "required": []}},
    {"name": "yama_capabilities", "description": "真实回读契约里的 13 工具 / 13 子命令 / 7 动作清单",
     "inputSchema": {"type": "object", "properties": {}, "required": []}},
]

INTENTS = {
    "yama_build": "dotnet build YamaEngine/YamaEngine.csproj -c Debug -p:Platform=x64 -warnaserror",
    "yama_test": "dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj -c Debug -p:Platform=x64 --no-build",
    "yama_smoke": "dotnet run --project YamaCLI/YamaCLI.csproj -c Debug -- smoke {frames}",
    "yama_snapshot": "YrPhysicsPipeline.Snapshot() -> 位级字节",
    "yama_rollback": "YrPhysicsPipeline.RewindTo(tick) -> Restore",
    "yama_ledger": "ConservationLedger: mass/elements/energy/momentum 显式过账",
    "yama_sota_status": "G0-G3 已闭合(云端 CI); G4-G8 未闭合(需真机/SDK/团队/时间)",
    "yama_coupled_mass": ("YrPhysicsPipeline.UseCoupledMassMatrix + LastCoupledTrace -> "
                          "TotalSteps/CoupledSteps/FallbackSteps/MaxOffDiagonal/LastK/LastUsedCoupling"),
    "yama_global_blocks": ("YrPhysicsPipeline.UseGlobalBlockSolve + LastGlobalBlockTrace -> "
                           "Groups/MaxPointsPerGroup/TotalIterations/SolvedGroups/"
                           "FallbackGroups/MaxCrossBlock/LastUsedGlobal"),
    "yama_codeoss_minimal": "scripts/build_codeoss_minimal.py --check (真实离线校验, 非意图字符串)",
    "yama_invoke": "yama_bridge.invoke(action, params) 通用真实委派",
    "yama_capabilities": "contract.json 真实 13/13 能力清单",
    "yama_solver_spectrum": ("YrBlockLinearSolvers(Direct/BlockJacobi/BlockGaussSeidel/"
                             "PreconditionedCG) + YrBlockLinearTrace -> "
                             "InnerIterations/InitialResidual/FinalResidual/MaxResidual/"
                             "SolutionDiscrepancy/Converged/UsedFallback/ResidualMonotonic "
                             "| dual-gate: residual AND solution drift"),
}


def handle(method, params, rid):
    if method == "initialize":
        return {"jsonrpc": "2.0", "id": rid, "result": {
            "protocolVersion": PROTOCOL,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "yama-engine", "version": "1.0.0"}}}
    if method == "notifications/initialized":
        return None
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}}
    if method == "tools/call":
        name = (params or {}).get("name", "")
        args = (params or {}).get("arguments") or {}
        if name not in INTENTS:
            return {"jsonrpc": "2.0", "id": rid,
                    "error": {"code": -32602, "message": "unknown tool: " + name}}
        intent = INTENTS[name]
        if name == "yama_smoke":
            intent = intent.replace("{frames}", str(args.get("frames", 60)))
        # 真实委派：不再是纯意图字符串
        import os
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        from yama_bridge import invoke as bridge_invoke
        if name == "yama_codeoss_minimal":
            action, bparams = "verify_codeoss_minimal", {}
        elif name == "yama_capabilities":
            action, bparams = "capabilities", {}
        elif name == "yama_invoke":
            action = str(args.get("action", "capabilities"))
            bparams = args.get("params") or {}
        else:
            action, bparams = "command_intent", {"tool": name, "arguments": args,
                                                 "intent": intent}
        res = bridge_invoke(action, bparams)
        payload = {"tool": name, "intent": intent, "delegated": True,
                   "action": action, "result": res, "read_only": True}
        return {"jsonrpc": "2.0", "id": rid,
                "result": {"content": [{"type": "text",
                                        "text": json.dumps(payload, ensure_ascii=False)}],
                           "isError": not bool(res.get("ok", False))}}
    if method == "ping":
        return {"jsonrpc": "2.0", "id": rid, "result": {}}
    return {"jsonrpc": "2.0", "id": rid,
            "error": {"code": -32601, "message": "method not found: " + str(method)}}


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue
        resp = handle(req.get("method", ""), req.get("params"), req.get("id"))
        if resp is not None:
            sys.stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
            sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
