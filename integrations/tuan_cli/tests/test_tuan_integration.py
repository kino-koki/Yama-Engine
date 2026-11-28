#!/usr/bin/env python3
"""Tuan-CLI 集成契约测试（纯 stdlib unittest，不依赖 tuan_cli / .NET）。"""
from __future__ import annotations
import json, os, re, subprocess, sys, unittest

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.dirname(HERE)
ROOT = os.path.dirname(os.path.dirname(PKG))


def read(rel):
    with open(os.path.join(PKG, rel), "r", encoding="utf-8") as f:
        return f.read()


class TestSkills(unittest.TestCase):
    EXPECTED = ("yama-build", "yama-test", "yama-physics-gate",
                "yama-sota-audit", "yama-coupled-mass")

    def test_all_skills_have_valid_frontmatter(self):
        for s in self.EXPECTED:
            p = os.path.join(PKG, "skills", s, "SKILL.md")
            self.assertTrue(os.path.isfile(p), p)
            with open(p, "r", encoding="utf-8") as fh:
                txt = fh.read()
            self.assertTrue(txt.startswith("---\n"), s)
            fm = txt.split("---", 2)[1]
            for key in ("name:", "description:", "activation:", "triggers:",
                        "provides:", "priority:", "use_count:"):
                self.assertIn(key, fm, s + " missing " + key)
            steps = re.findall(r"^\d+\. ", txt, re.M)
            self.assertGreaterEqual(len(steps), 3, s + " needs >=3 numbered steps")

    def test_coupled_skill_mentions_reduction_switch(self):
        txt = read("skills/yama-coupled-mass/SKILL.md")
        self.assertIn("UseCoupledMassReduction", txt)
        self.assertIn("LastReducedPoints", txt)

    def test_coupled_skill_mentions_global_block_switch(self):
        txt = read("skills/yama-coupled-mass/SKILL.md")
        self.assertIn("UseGlobalBlockSolve", txt)
        self.assertIn("LastGlobalBlockTrace", txt)

    def test_coupled_skill_mentions_solver_spectrum(self):
        txt = read("skills/yama-coupled-mass/SKILL.md")
        self.assertIn("GlobalBlockSolveMode", txt)
        self.assertIn("YrBlockLinearTrace", txt)
        self.assertIn("SolutionDiscrepancy", txt)

    def test_sota_skill_forbids_false_claim(self):
        txt = read("skills/yama-sota-audit/SKILL.md")
        self.assertIn("禁止", txt)
        self.assertIn("G4", txt)
        self.assertIn("G8", txt)


class TestHooks(unittest.TestCase):
    def test_hook_config_uses_tuan_event_names(self):
        txt = read("hooks/yama_hooks.yaml")
        self.assertIn("PostToolUse", txt)
        self.assertIn("Stop", txt)
        self.assertIn("yama_gate.py", txt)

    def test_hook_script_emits_valid_json_decision(self):
        p = os.path.join(PKG, "hooks", "yama_gate.py")
        out = subprocess.check_output(
            [sys.executable, p, "--event", "PostToolUse"],
            input=b'{"tool_name":"fs_write"}', timeout=30)
        d = json.loads(out.decode("utf-8"))
        self.assertEqual(d["event"], "PostToolUse")
        self.assertIn(d["decision"], ("allow", "warn"))
        self.assertIn("checked", d)
        self.assertEqual(d["checked"], 30)
        self.assertIn("SOTA", d["note"])


class TestMcpServer(unittest.TestCase):
    def _rpc(self, lines):
        p = os.path.join(PKG, "mcp_server.py")
        out = subprocess.check_output([sys.executable, p],
                                      input=("\n".join(lines) + "\n").encode(),
                                      timeout=30)
        return [json.loads(x) for x in out.decode().splitlines() if x.strip()]

    def test_initialize_and_tools_list(self):
        rs = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}),
            json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
            json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}),
        ])
        self.assertEqual(len(rs), 2)
        self.assertEqual(rs[0]["result"]["serverInfo"]["name"], "yama-engine")
        self.assertEqual(len(rs[1]["result"]["tools"]), 13)

    def test_coupled_mass_tool_is_exposed(self):
        rs = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                        "params": {"name": "yama_coupled_mass", "arguments": {}}}),
        ])
        self.assertIn("LastCoupledTrace", rs[0]["result"]["content"][0]["text"])

    def test_global_blocks_tool_is_exposed(self):
        rs = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                        "params": {"name": "yama_global_blocks", "arguments": {}}}),
        ])
        self.assertIn("LastGlobalBlockTrace", rs[0]["result"]["content"][0]["text"])

    def test_solver_spectrum_tool_is_exposed(self):
        rs = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                        "params": {"name": "yama_solver_spectrum", "arguments": {}}}),
        ])
        self.assertIn("YrBlockLinearTrace", rs[0]["result"]["content"][0]["text"])

    def test_codeoss_minimal_tool_really_delegates(self):
        rs = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                        "params": {"name": "yama_codeoss_minimal", "arguments": {}}}),
        ])
        body = json.loads(rs[0]["result"]["content"][0]["text"])
        self.assertTrue(body["delegated"])
        self.assertTrue(body["read_only"])
        self.assertEqual(body["action"], "verify_codeoss_minimal")
        self.assertTrue(body["result"]["ok"])
        self.assertTrue(body["result"]["result"]["clean_mit_build"])
        self.assertEqual(body["result"]["result"]["errors"], [])
        self.assertIn("ide/codeoss/product.json",
                      body["result"]["result"]["config_sha256"])

    def test_yama_invoke_round_trip_and_fail_closed(self):
        ok = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                        "params": {"name": "yama_invoke",
                                   "arguments": {"action": "capabilities"}}}),
        ])
        body = json.loads(ok[0]["result"]["content"][0]["text"])
        self.assertTrue(body["result"]["ok"])
        self.assertEqual(body["result"]["result"]["tools"], 13)
        bad = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                        "params": {"name": "yama_invoke",
                                   "arguments": {"action": "no_such_action"}}}),
        ])
        self.assertTrue(bad[0]["result"]["isError"])
        self.assertFalse(json.loads(bad[0]["result"]["content"][0]["text"])["result"]["ok"])

    def test_bridge_direct_and_cli_entrypoint(self):
        sys.path.insert(0, PKG)
        import yama_bridge
        r = yama_bridge.invoke("codeoss_manifest")
        self.assertTrue(r["ok"])
        self.assertEqual(r["result"]["tag"], "1.148.0")
        self.assertEqual(r["result"]["profile"], "web-only-minimal")
        self.assertFalse(r["result"]["compile_in_this_repo"])
        cli = os.path.join(PKG, "yama_cli.py")
        out = subprocess.check_output([sys.executable, cli, "capabilities"], timeout=30)
        d = json.loads(out.decode("utf-8"))
        self.assertTrue(d["ok"] and d["delegated"])
        self.assertEqual(d["result"]["tools"], 13)

    def test_tools_call_smoke_and_unknown(self):
        rs = self._rpc([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                        "params": {"name": "yama_smoke", "arguments": {"frames": 120}}}),
            json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                        "params": {"name": "nope"}}),
        ])
        self.assertIn("smoke 120", rs[0]["result"]["content"][0]["text"])
        self.assertEqual(rs[1]["error"]["code"], -32602)


class TestSubcommand(unittest.TestCase):
    def test_routes_and_validation(self):
        sys.path.insert(0, PKG)
        import subcommand
        self.assertTrue(subcommand.handle(["build"])["ok"])
        self.assertEqual(subcommand.handle(["smoke", "--frames", "0"])["ok"], False)
        self.assertEqual(subcommand.handle(["rollback", "--tick", "-1"])["ok"], False)
        self.assertIn("NOT SOTA", subcommand.handle(["sota"])["status"])
        self.assertIn("CoupledSteps", subcommand.handle(["coupled"])["trace_fields"])
        self.assertIn("MaxCrossBlock",
                      subcommand.handle(["globalblocks"])["trace_fields"])
        r = subcommand.handle(["solver-spectrum"])
        self.assertIn("SolutionDiscrepancy", r["trace_fields"])
        self.assertEqual(len(r["modes"]), 4)
        cm = subcommand.handle(["codeoss-minimal"])
        self.assertTrue(cm["ok"] and cm["delegated"])
        cap = subcommand.handle(["capabilities"])
        self.assertEqual(cap["result"]["tools"], 13)
        inv = subcommand.handle(["invoke", "--action", "sota_status"])
        self.assertTrue(inv["ok"] and inv["result"]["not_sota"])
        bad = subcommand.handle(["invoke", "--action", "nope"])
        self.assertFalse(bad["ok"])


class TestContract(unittest.TestCase):
    def test_contract_json_matches_files(self):
        c = json.loads(read("contract.json"))
        self.assertEqual(c["codeoss_version"], "1.148.0")
        self.assertEqual(c["integration_mode"], "yama-side-only")
        self.assertEqual(len(c["mcp_tools"]), 13)
        self.assertEqual(len(c["subcommands"]), 13)
        self.assertEqual(c["codeoss_minimal"]["profile"], "web-only-minimal")
        self.assertFalse(c["codeoss_minimal"]["compile_in_this_repo"])
        self.assertEqual(len(c["callable_actions"]["actions"]), 7)
        self.assertEqual(c["codeoss_bridge_commands"], 12)
        self.assertEqual(c["language_profile"]["primary"], "yama")
        self.assertEqual(c["language_profile"]["optional_modules_installed_by_default"], 0)
        self.assertFalse(c["ci_gate_enabled"])
        self.assertIn("NOT enabled", c["ci_gate"])
        self.assertIn("CodeOssMinimalBridgeTests.cs", c["csharp_equivalent_gate"])
        reg = json.loads(read("yama_actions.json"))
        self.assertEqual(len(reg["actions"]), 7)
        self.assertEqual(reg["tools"], c["mcp_tools"])
        for rel in c["files"]:
            self.assertTrue(os.path.isfile(os.path.join(PKG, rel)), rel)
        # 轻量锁的 6 个 SHA-256 必须与仓库真实文件逐位一致
        import hashlib
        lock = json.loads(open(os.path.join(ROOT, "ide", "codeoss", "MINIMAL.lock.json"),
                              "r", encoding="utf-8").read())
        for rel, want in lock["config_sha256"].items():
            got = hashlib.sha256(open(os.path.join(ROOT, rel), "rb").read()
                                .replace(b"\r\n", b"\n"))
            self.assertEqual(got.hexdigest().upper(), want, rel)


if __name__ == "__main__":
    unittest.main(verbosity=2)
