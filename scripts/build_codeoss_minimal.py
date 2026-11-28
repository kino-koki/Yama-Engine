#!/usr/bin/env python3
"""Code - OSS 1.148.0 最轻量 web-only 干净 MIT 构建面：离线校验 / 计划 / 清单。

纯 stdlib，零 Node、零 Electron、零网络。三种模式：
    --check           离线校验 MINIMAL.lock / product.json / 6 文件 SHA-256
    --plan            输出 web-only 最小构建步骤（明确标注本仓库未编译）
    --emit-manifest   输出可上传的轻量构建清单 JSON 到 stdout
任何校验失败返回非 0（fail-closed）。
"""
from __future__ import annotations
import argparse, hashlib, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def p(rel):
    return os.path.join(ROOT, rel.replace("/", os.sep))

def load(rel):
    with open(p(rel), "r", encoding="utf-8") as f:
        return json.load(f)

def read_bytes(rel):
    with open(p(rel), "rb") as f:
        return f.read()

def digest(rel):
    # GitHub Actions 的 Windows runner 默认 core.autocrlf=true，会把文本文件
    # checkout 成 CRLF。统一先归一化成 LF 再算摘要，保证 Linux / Windows /
    # C# 三端口径逐位一致（生成 MINIMAL.lock.json 时也是按 LF 算的）。
    data = read_bytes(rel).replace(b"\r\n", b"\n")
    return hashlib.sha256(data).hexdigest().upper()

CONFIG_FILES = [
    "ide/codeoss/product.json",
    "ide/codeoss/UPSTREAM.lock.json",
    ".gitmodules",
    "ci/codeoss-gate.yml",
    "scripts/fetch_codeoss.py",
    "scripts/build_codeoss_minimal.py",
]

EXPECTED_EXCLUSIONS = [
    "electron", "native_modules", "marketplace", "telemetry", "branding",
    "builtin_extension_download", "update_service", "extensions_gallery",
]

def check():
    errs = []
    try:
        lock = load("ide/codeoss/MINIMAL.lock.json")
    except Exception as e:
        return ["MINIMAL.lock.json 读取失败: " + str(e)], None
    if lock.get("tag") != "1.148.0":
        errs.append("tag 不是 1.148.0")
    if lock.get("commit") != "661626d0e72f97806550790a98fb87116c9db825":
        errs.append("commit 未精确锁定")
    if lock.get("license") != "MIT":
        errs.append("license 不是 MIT")
    if lock.get("profile") != "web-only-minimal":
        errs.append("profile 不是 web-only-minimal")
    if lock.get("build_target") != "web":
        errs.append("build_target 不是 web")
    if lock.get("compile_in_this_repo") is not False:
        errs.append("compile_in_this_repo 必须为 false（不得谎报编译）")
    ex = set(lock.get("exclusions", []))
    for e in EXPECTED_EXCLUSIONS:
        if e not in ex:
            errs.append("排除项缺失: " + e)
    # 6 个轻量配置文件 SHA-256 逐位比对
    declared = lock.get("config_sha256", {})
    actual = {}
    for rel in CONFIG_FILES:
        try:
            actual[rel] = digest(rel)
        except OSError as e:
            errs.append("无法读取 " + rel + ": " + str(e))
            continue
        if declared.get(rel) != actual[rel]:
            errs.append("SHA-256 不匹配 " + rel)
    if sorted(declared.keys()) != sorted(CONFIG_FILES):
        errs.append("config_sha256 键集合与 CONFIG_FILES 不一致")
    # product.json 干净 MIT + 最轻量声明
    try:
        prod = load("ide/codeoss/product.json")
    except Exception as e:
        errs.append("product.json 读取失败: " + str(e))
        prod = {}
    if prod.get("licenseName") != "MIT":
        errs.append("product.json licenseName 不是 MIT")
    if "Visual Studio Code" in str(prod.get("nameShort", "")):
        errs.append("product.json 含微软官方品牌")
    if prod.get("enableTelemetry") is not False or prod.get("telemetryEndpoint"):
        errs.append("遥测未彻底禁用")
    if prod.get("extensionsGallery"):
        errs.append("仍配置 Marketplace gallery")
    if prod.get("builtInExtensions"):
        errs.append("仍配置内置扩展下载")
    ym = prod.get("yamaMinimal", {})
    if ym.get("profile") != "web-only-minimal" or ym.get("buildTarget") != "web":
        errs.append("yamaMinimal 未声明 web-only-minimal")
    if ym.get("compiledInThisRepo") is not False:
        errs.append("yamaMinimal.compiledInThisRepo 必须为 false")
    for k in ("noElectron", "noNativeModules", "noMarketplace", "noTelemetry",
              "noBranding", "noBuiltinExtensionDownload", "noUpdateService"):
        if ym.get(k) is not True:
            errs.append("yamaMinimal." + k + " 必须为 true")
    # CI 模板不得宣称完整 Electron 编译
    try:
        gate = open(p("ci/codeoss-gate.yml"), "r", encoding="utf-8").read()
    except OSError as e:
        gate = ""
        errs.append("ci/codeoss-gate.yml 读取失败: " + str(e))
    if "npm ci" in gate or "npm run compile" in gate:
        errs.append("轻量门禁不得声明完整 npm/Electron 编译")
    if "build_codeoss_minimal.py --check" not in gate:
        errs.append("轻量门禁未调用离线校验")
    # 反回漂：fetch_codeoss.py 不得再宣称完整 Electron workbench 编译
    try:
        fetch = open(p("scripts/fetch_codeoss.py"), "r", encoding="utf-8").read()
    except OSError as e:
        fetch = ""
        errs.append("fetch_codeoss.py 读取失败: " + str(e))
    if "npm ci && npm run compile" in fetch or "npm run compile" in fetch:
        errs.append("fetch_codeoss.py 口径回漂：仍宣称完整 Electron 编译")
    if "本仓库不编译" not in fetch:
        errs.append("fetch_codeoss.py 未声明本仓库不编译上游")
    if "build_codeoss_minimal.py --check" not in fetch:
        errs.append("fetch_codeoss.py 未指向最轻量离线校验")
    # gitmodules 锁
    try:
        gm = open(p(".gitmodules"), "r", encoding="utf-8").read()
    except OSError as e:
        gm = ""
        errs.append(".gitmodules 读取失败: " + str(e))
    if "microsoft/vscode" not in gm or "1.148.0" not in gm:
        errs.append(".gitmodules 未锁定 microsoft/vscode @ 1.148.0")
    return errs, {"digests": actual, "profile": lock.get("profile")}

def plan():
    return {
        "profile": "web-only-minimal",
        "tag": "1.148.0",
        "commit": COMMIT,
        "compiled_in_this_repo": False,
        "note": "本仓库不编译 1.8GB 上游 workbench；以下是带 Node 24 的机器上的手动 web-only 最小构建步骤，不宣称已执行。具体 npm 脚本名以该 tag 的上游构建文档为准，本仓库未核实、不写死。",
        "steps": [
            "git submodule update --init --depth 1 ide/codeoss/upstream",
            "python scripts/fetch_codeoss.py --apply-only   # 覆盖干净 MIT product.json",
            "python scripts/build_codeoss_minimal.py --check # 离线锁 + 摘要校验",
            "# web-only 最小面：只装 web 构建所需依赖，跳过 Electron 桌面壳与原生模块",
            "cd ide/codeoss/upstream && npm ci --omit=optional",
            "按该 tag 的上游 web 构建文档执行对应编译脚本（本仓库未核实脚本名，故不写死）",
            "# 产物仅为静态 web workbench 资源；不打包 Electron、不下载内置扩展、不连 Marketplace",
        ],
    }

COMMIT = "661626d0e72f97806550790a98fb87116c9db825"

def manifest():
    errs, info = check()
    lock = load("ide/codeoss/MINIMAL.lock.json")
    return {
        "name": "yama-codeoss-minimal-build-manifest",
        "schema": "yama.codeoss.minimal.v1",
        "profile": lock.get("profile"),
        "upstream": lock.get("upstream"),
        "tag": lock.get("tag"),
        "commit": lock.get("commit"),
        "license": "MIT",
        "build_target": lock.get("build_target"),
        "compile_in_this_repo": False,
        "exclusions": lock.get("exclusions", []),
        "config_sha256": info["digests"] if info else {},
        "clean_mit_build": len(errs) == 0,
        "errors": errs,
        "note": "轻量构建清单，不是已编译的 workbench 产物；不宣称 G4 闭合。",
    }

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--plan", action="store_true")
    ap.add_argument("--emit-manifest", action="store_true")
    a = ap.parse_args()
    if a.plan:
        print(json.dumps(plan(), ensure_ascii=False, indent=2))
        return 0
    if a.emit_manifest:
        m = manifest()
        print(json.dumps(m, ensure_ascii=False, indent=2))
        return 0 if m["clean_mit_build"] else 1
    errs, info = check()
    print(json.dumps({
        "mode": "check",
        "profile": info["profile"] if info else None,
        "tag": "1.148.0",
        "commit": COMMIT,
        "license": "MIT",
        "config_files": CONFIG_FILES,
        "config_sha256": info["digests"] if info else {},
        "clean_mit_build": len(errs) == 0,
        "errors": errs,
    }, ensure_ascii=False, indent=2))
    return 0 if not errs else 1

if __name__ == "__main__":
    raise SystemExit(main())
