#!/usr/bin/env python3
"""Code - OSS 1.148.0 最轻量 web-only 干净 MIT 构建：锁定校验 / 应用 product.json。

纯 stdlib。三种模式：
    --check        只校验 UPSTREAM.lock.json + product.json，不联网、不写文件
    --apply-only   把 product.json 覆盖到 submodule 目录
    (默认)         --check + 提示 submodule 初始化命令 + 打印最小构建步骤

第十九轮起本仓库的口径是「最轻量 web-only 干净 MIT 构建面」：
不编译 1.8GB 上游 workbench、不打包 Electron、不下载内置扩展、
不连 Marketplace。本脚本只负责锁与 product.json，编译步骤见
scripts/build_codeoss_minimal.py --plan（且明确标注本仓库未编译）。
"""
from __future__ import annotations
import argparse, json, os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCK = os.path.join(ROOT, "ide/codeoss/UPSTREAM.lock.json")
PRODUCT = os.path.join(ROOT, "ide/codeoss/product.json")
SUBMODULE = os.path.join(ROOT, "ide/codeoss/upstream")
EXPECT_TAG = "1.148.0"
EXPECT_COMMIT = "661626d0e72f97806550790a98fb87116c9db825"


def load(p):
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


def check():
    errs = []
    lock = load(LOCK)
    if lock.get("tag") != EXPECT_TAG:
        errs.append("tag 不是 1.148.0: " + str(lock.get("tag")))
    if lock.get("commit") != EXPECT_COMMIT:
        errs.append("commit 未锁定: " + str(lock.get("commit")))
    if lock.get("license") != "MIT":
        errs.append("license 不是 MIT")
    if "microsoft/vscode" not in str(lock.get("upstream", "")):
        errs.append("upstream 不是 microsoft/vscode")
    prod = load(PRODUCT)
    lp_path = os.path.join(ROOT, "ide", "codeoss", "language-profile.json")
    if not os.path.isfile(lp_path):
        errs.append("language-profile.json 缺失")
    if not os.path.isfile(os.path.join(ROOT, "schemas", "yama-script.schema.json")):
        errs.append("yama-script.schema.json 缺失")
    else:
        lp = load(lp_path)
        pri = lp.get("primary", {})
        if pri.get("id") != "yama" or pri.get("enforced") is not True:
            errs.append("language-profile 未强制 YAMA 主语言")
        if any(x.get("installed") for x in lp.get("optional_modules", [])):
            errs.append("language-profile 存在默认内置语言模块")
        if lp.get("enforcement", {}).get("strictYamaProject") is not True:
            errs.append("language-profile 未开启严格项目门禁")
        if lp.get("scriptBinding", {}).get("vmImplemented") is not False:
            errs.append("language-profile 谎报 VM 已实现")
    if prod.get("licenseName") != "MIT":
        errs.append("product.json licenseName 不是 MIT")
    if "Visual Studio Code" in str(prod.get("nameShort", "")):
        errs.append("nameShort 含微软官方品牌，去品牌不彻底")
    if prod.get("enableTelemetry") is not False:
        errs.append("遥测未禁用")
    if prod.get("telemetryEndpoint"):
        errs.append("仍配置遥测端点")
    if prod.get("extensionsGallery"):
        errs.append("仍配置 Marketplace gallery")
    if prod.get("version") != EXPECT_TAG or prod.get("commit") != EXPECT_COMMIT:
        errs.append("product.json 版本/commit 与 lock 不一致")
    gm = os.path.join(ROOT, ".gitmodules")
    if os.path.exists(gm):
        g = open(gm, "r", encoding="utf-8").read()
        if "microsoft/vscode" not in g or EXPECT_TAG not in g:
            errs.append(".gitmodules 未锁定 microsoft/vscode @ 1.148.0")
    else:
        errs.append(".gitmodules 缺失")
    return errs, lock, prod


def verify_submodule_commit():
    if not os.path.isdir(SUBMODULE):
        return ["submodule 未初始化: git submodule update --init --depth 1 ide/codeoss/upstream"]
    try:
        sha = subprocess.check_output(["git", "rev-parse", "HEAD"],
                                      cwd=SUBMODULE, text=True).strip()
    except Exception as e:
        return ["无法读取 submodule HEAD: " + str(e)]
    return [] if sha == EXPECT_COMMIT else [
        "submodule HEAD 与锁定 commit 不符: %s != %s" % (sha, EXPECT_COMMIT)]


def apply_only():
    errs, _, _ = check()
    if errs:
        return errs
    dst = os.path.join(SUBMODULE, "product.json")
    if not os.path.isdir(SUBMODULE):
        return ["submodule 未初始化，无法应用 product.json"]
    shutil.copyfile(PRODUCT, dst)
    return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--apply-only", action="store_true")
    a = ap.parse_args()
    errs, lock, prod = check()
    if a.apply_only:
        errs = apply_only()
    print(json.dumps({
        "mode": "apply-only" if a.apply_only else ("check" if a.check else "plan"),
        "tag": lock.get("tag"), "commit": lock.get("commit"),
        "license": lock.get("license"), "clean_mit_build": len(errs) == 0,
        "errors": errs,
        "steps": ([] if a.apply_only else [
            "git submodule update --init --depth 1 ide/codeoss/upstream",
            "python scripts/fetch_codeoss.py --apply-only",
            "python scripts/build_codeoss_minimal.py --check  # 最轻量锁 + 9 文件 SHA-256",
            "python scripts/check_yama_project.py --check  # YAMA 主语言强制 + 项目契约",
            "web-only 最小构建步骤见 scripts/build_codeoss_minimal.py --plan",
            "本仓库不编译 1.8GB 上游 workbench，不宣称产物存在；不得替换为官方 VS Code 二进制/商标",
        ]),
    }, ensure_ascii=False, indent=2))
    if not a.check and not a.apply_only:
        sub = verify_submodule_commit()
        if sub:
            print("submodule 状态提示: " + "; ".join(sub), file=sys.stderr)
    return 0 if not errs else 1


if __name__ == "__main__":
    raise SystemExit(main())
