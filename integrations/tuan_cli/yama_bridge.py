#!/usr/bin/env python3
# Yama 真实被调用能力 dispatcher（纯 stdlib，只读，fail-closed）。
# 三入口共用：mcp_server.py tools/call / subcommand.py handle() / yama_cli.py。
# 与「上一版只返回意图字符串」不同：这里的动作真跑校验脚本、真读契约、
# 真跑 hook、真读差距文档，返回结构化结果。
from __future__ import annotations
import json, os, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))

def root():
    env = os.environ.get("YAMA_REPO_ROOT")
    if env and os.path.isfile(os.path.join(env, "ide", "codeoss", "MINIMAL.lock.json")):
        return env
    d = HERE
    for _ in range(8):
        d = os.path.dirname(d)
        if os.path.isfile(os.path.join(d, "ide", "codeoss", "MINIMAL.lock.json")):
            return d
    return os.path.dirname(os.path.dirname(HERE))

ROOT = root()

def rp(rel):
    return os.path.join(ROOT, rel.replace("/", os.sep))

def read_json(rel):
    with open(rp(rel), "r", encoding="utf-8") as f:
        return json.load(f)

def read_text(rel):
    with open(rp(rel), "r", encoding="utf-8", errors="replace") as f:
        return f.read()

def run_py(args, stdin=b""):
    cmd = [sys.executable] + args
    p = subprocess.run(cmd, cwd=ROOT, input=stdin,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    return p.returncode, p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")

ACTIONS = {
    "verify_codeoss_minimal": None,
    "capabilities": None,
    "read_contract": None,
    "codeoss_manifest": None,
    "sota_status": None,
    "hook_gate": None,
    "command_intent": None,
}

def _registry_consistent():
    try:
        reg = read_json("integrations/tuan_cli/yama_actions.json")
        names = [a["name"] for a in reg.get("actions", [])]
        return sorted(names) == sorted(ACTIONS.keys()), reg
    except Exception:
        return False, None

def _verify_codeoss_minimal():
    code, out, err = run_py(["scripts/build_codeoss_minimal.py", "--check"])
    try:
        d = json.loads(out)
    except Exception:
        return False, {"parse_error": out[:600], "stderr": err[:300], "exit": code}
    return (code == 0 and d.get("clean_mit_build") is True), d

def _capabilities():
    c = read_json("integrations/tuan_cli/contract.json")
    reg = read_json("integrations/tuan_cli/yama_actions.json")
    return True, {
        "tools": len(c.get("mcp_tools", [])),
        "subcommands": len(c.get("subcommands", [])),
        "skills": len(c.get("skills", [])),
        "actions": len(reg.get("actions", [])),
        "tool_names": c.get("mcp_tools", []),
        "subcommand_names": c.get("subcommands", []),
        "read_only": True,
    }

def _codeoss_manifest():
    m = read_json("ide/codeoss/MINIMAL.lock.json")
    p = read_json("ide/codeoss/product.json")
    return True, {
        "profile": m.get("profile"), "tag": m.get("tag"), "commit": m.get("commit"),
        "license": m.get("license"), "build_target": m.get("build_target"),
        "compile_in_this_repo": m.get("compile_in_this_repo"),
        "exclusions": m.get("exclusions", []),
        "product_minimal": p.get("yamaMinimal", {}),
    }

def _sota_status():
    t = read_text("docs/SOTA_GAP_ANALYSIS.md")
    g0g3 = "G0" in t and "G3" in t
    g4g8 = "G4" in t and "G8" in t
    return g0g3 and g4g8, {
        "g0_g3_closed": g0g3,
        "g4_g8_open": g4g8,
        "not_sota": True,
        "source": "docs/SOTA_GAP_ANALYSIS.md",
        "note": "不宣称已达 SOTA；真机 GPU / 规模 / 平台 SDK / 生态 / 外部验证需硬件与团队。",
    }

def _hook_gate():
    code, out, err = run_py(["integrations/tuan_cli/hooks/yama_gate.py", "--event", "PostToolUse"],
                            stdin=b'{"tool_name":"yama_invoke"}')
    try:
        d = json.loads(out)
    except Exception:
        return False, {"parse_error": out[:400], "stderr": err[:200]}
    return (code == 0 and d.get("decision") == "allow"), d

INTENTS = {
    "yama_build": "dotnet build YamaEngine/YamaEngine.csproj -c Debug -p:Platform=x64 -warnaserror",
    "yama_test": "dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj -c Debug -p:Platform=x64 --no-build",
    "yama_smoke": "dotnet run --project YamaCLI/YamaCLI.csproj -c Debug -- smoke {frames}",
    "yama_snapshot": "YrPhysicsPipeline.Snapshot() -> 位级字节",
    "yama_rollback": "YrPhysicsPipeline.RewindTo(tick) -> Restore",
    "yama_ledger": "ConservationLedger: mass/elements/energy/momentum 显式过账",
    "yama_sota_status": "G0-G3 已闭合(云端 CI); G4-G8 未闭合(需真机/SDK/团队/时间)",
    "yama_coupled_mass": "YrPhysicsPipeline.UseCoupledMassMatrix + LastCoupledTrace",
    "yama_global_blocks": "YrPhysicsPipeline.UseGlobalBlockSolve + LastGlobalBlockTrace",
    "yama_solver_spectrum": "YrBlockLinearSolvers + YrBlockLinearTrace | dual-gate: residual AND solution drift",
    "yama_codeoss_minimal": "scripts/build_codeoss_minimal.py --check (真实离线校验)",
    "yama_invoke": "yama_bridge.invoke(action, params) 通用委派",
    "yama_capabilities": "contract.json 真实 13/13 能力清单",
}

def _command_intent(params):
    tool = str(params.get("tool", ""))
    t = INTENTS.get(tool, "")
    args = params.get("arguments") or {}
    if tool == "yama_smoke":
        t = t.replace("{frames}", str(args.get("frames", 60)))
    return bool(t), {"tool": tool, "intent": t, "arguments": args}

def invoke(action, params=None):
    """统一 dispatcher。返回 {ok, action, result, source, read_only, elapsed_ms}。"""
    t0 = time.time()
    params = params or {}
    ok_reg, reg = _registry_consistent()
    if not ok_reg:
        return {"ok": False, "action": action, "error": "动作注册表与 dispatcher 不一致（fail-closed）",
                "read_only": True, "elapsed_ms": int((time.time()-t0)*1000)}
    if action not in ACTIONS:
        return {"ok": False, "action": action, "error": "unknown action: " + str(action),
                "available": sorted(ACTIONS.keys()), "read_only": True,
                "elapsed_ms": int((time.time()-t0)*1000)}
    try:
        if action == "verify_codeoss_minimal":
            ok, res = _verify_codeoss_minimal(); src = "scripts/build_codeoss_minimal.py --check"
        elif action == "capabilities":
            ok, res = _capabilities(); src = "integrations/tuan_cli/contract.json"
        elif action == "read_contract":
            ok, res = True, read_json("integrations/tuan_cli/contract.json"); src = "contract.json"
        elif action == "codeoss_manifest":
            ok, res = _codeoss_manifest(); src = "MINIMAL.lock.json + product.json"
        elif action == "sota_status":
            ok, res = _sota_status(); src = "docs/SOTA_GAP_ANALYSIS.md"
        elif action == "hook_gate":
            ok, res = _hook_gate(); src = "hooks/yama_gate.py --event PostToolUse"
        else:
            ok, res = _command_intent(params); src = "mcp_server.py INTENTS"
    except Exception as e:
        return {"ok": False, "action": action, "error": type(e).__name__ + ": " + str(e),
                "source": "yama_bridge.dispatch", "read_only": True,
                "elapsed_ms": int((time.time()-t0)*1000)}
    return {"ok": bool(ok), "action": action, "result": res, "source": src,
            "read_only": True, "elapsed_ms": int((time.time()-t0)*1000)}

def main():
    a = sys.argv[1:]
    action = a[0] if a else "capabilities"
    params = json.loads(a[1]) if len(a) > 1 else {}
    print(json.dumps(invoke(action, params), ensure_ascii=False))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
