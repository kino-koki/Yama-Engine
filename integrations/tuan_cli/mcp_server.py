#!/usr/bin/env python3
"""自研 Yama MCP Server（JSON-RPC 2.0 over stdio，纯 stdlib）。

对齐 Tuan-CLI 的零依赖 MCP 客户端协议（见其 tests/fixtures/mcp_demo_server.py）：
  initialize / notifications/initialized / tools/list / tools/call
工具只读或返回可执行意图，不在 Agent 进程里偷偷跑危险命令。
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
        text = INTENTS[name]
        if name == "yama_smoke":
            text = text.replace("{frames}", str(args.get("frames", 60)))
        return {"jsonrpc": "2.0", "id": rid,
                "result": {"content": [{"type": "text", "text": text}],
                           "isError": False}}
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
