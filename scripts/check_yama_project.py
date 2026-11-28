#!/usr/bin/env python3
"""YAMA 主语言项目契约门禁（纯 stdlib / 离线 / fail-closed）。

    --check              校验 language-profile.json 自身 + 三个内置模板
    --project DIR        校验指定项目目录
    --emit               输出 JSON 摘要（含全部错误）
    (默认)              --check

不联网、不调用 workbench、不读 VS Code 源码。
"""
from __future__ import annotations
import argparse, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROFILE = os.path.join(ROOT, "ide", "codeoss", "language-profile.json")
TEMPLATES = ["Templates/Basic2D", "Templates/Basic3D", "Templates/NpcRealityDemo"]


def load(p):
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


def check_profile(prof):
    errs = []
    if prof.get("schema") != "yama.codeoss.language-profile.v1":
        errs.append("profile schema 不正确")
    pri = prof.get("primary", {})
    if pri.get("id") != "yama":
        errs.append("主语言不是 yama")
    if pri.get("enforced") is not True:
        errs.append("主语言未强制")
    if ".yama" not in pri.get("extensions", []):
        errs.append("主语言未绑定 .yama")
    if pri.get("projectContract") != "yama.project.json":
        errs.append("项目契约文件名不对")
    mods = prof.get("optional_modules", [])
    if not mods:
        errs.append("未声明可插拔语言模块")
    for m in mods:
        if m.get("installed") is not False:
            errs.append("可选模块默认不应内置: " + str(m.get("id")))
        if m.get("removable") is not True:
            errs.append("可选模块应可卸载: " + str(m.get("id")))
    fd = prof.get("forbidden_defaults", [])
    for k in ["builtin_language_packs", "marketplace_auto_install", "builtInExtensions"]:
        if k not in fd:
            errs.append("未禁止默认项: " + k)
    enf = prof.get("enforcement", {})
    if enf.get("strictYamaProject") is not True:
        errs.append("未开启严格 YAMA 项目门禁")
    if "不宣称" not in str(enf.get("boundary", "")):
        errs.append("强制边界缺少不宣称声明")
    return errs


def check_project(root, project_dir, prof):
    errs = []
    pri = prof.get("primary", {})
    key = pri.get("startupSceneKey", "startupScene")
    contract = pri.get("projectContract", "yama.project.json")
    pj = os.path.join(project_dir, contract)
    if not os.path.isfile(pj):
        return ["缺项目契约: " + pj]
    p = load(pj)
    if p.get("primaryLanguage") != "yama":
        errs.append("项目未声明 primaryLanguage=yama: " + project_dir)
    scene = str(p.get(key, ""))
    if not scene.endswith(".yama"):
        errs.append("startupScene 不是 .yama: " + scene)
    scene_path = os.path.join(project_dir, scene.replace("/", os.sep))
    if not os.path.isfile(scene_path):
        errs.append("startupScene 文件不存在: " + scene_path)
    return errs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--emit", action="store_true")
    ap.add_argument("--project", default="")
    a = ap.parse_args()
    errors = []
    prof = None
    try:
        prof = load(PROFILE)
        errors += check_profile(prof)
    except Exception as e:
        errors.append("language-profile.json 读取失败: " + str(e))
    targets = []
    if a.project:
        targets = [a.project]
    else:
        targets = [os.path.join(ROOT, t.replace("/", os.sep)) for t in TEMPLATES]
    if prof:
        for t in targets:
            errors += check_project(ROOT, t, prof)
    out = {
        "mode": "project" if a.project else ("emit" if a.emit else "check"),
        "primary": (prof or {}).get("primary", {}).get("id"),
        "enforced": (prof or {}).get("primary", {}).get("enforced"),
        "strict_yama_project": (prof or {}).get("enforcement", {}).get("strictYamaProject"),
        "optional_modules": len((prof or {}).get("optional_modules", [])),
        "optional_modules_installed": sum(
            1 for m in (prof or {}).get("optional_modules", []) if m.get("installed")),
        "projects_checked": len(targets),
        "clean": len(errors) == 0,
        "errors": errors,
        "boundary": (prof or {}).get("enforcement", {}).get("boundary", ""),
    }
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0 if not errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
