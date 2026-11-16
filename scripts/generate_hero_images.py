# -*- coding: utf-8 -*-
"""SenseNova U1.5-lite 批量生成官网页首图（4K），暖色日落系+引擎感融合，避免黑色单调。

用法：
  export SENSENOVA_API_KEY=sk-xxx
  python -I scripts/generate_hero_images.py
"""
import base64
import json
import os
import sys
import time
import urllib.request

API = "https://token.sensenova.cn/v1/images/generations"
KEY = os.environ.get("SENSENOVA_API_KEY", "")
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "website", "assets", "heroes")
os.makedirs(OUT, exist_ok=True)

# 统一风格后缀：Mistral 日落色系 + 引擎科技感融合，禁止大面积黑色
STYLE = (
    "editorial flat illustration with subtle 3D render accents, warm sunset palette: "
    "cream #FFFAEB, amber #FFAE00, ochre #FF8205, flame orange #FA500F, deep red #E00400, "
    "soft golden light, clean minimal composition with generous negative space at top for headline text, "
    "no pure black areas, no dark moody lighting, bright and optimistic, high detail, 4k quality"
)

PAGES = {
    # 首页 hero：引擎全景
    "index": "panoramic view of a fantasy game engine workshop at golden hour: floating geometric islands carrying low-poly mountains, glowing orange voxel cubes and wireframe spheres orbiting, distant sunset mountain range in warm cream and amber sky, " + STYLE,
    # 为什么做
    "why": "a lone explorer standing on a sunlit cliff edge overlooking a vast valley of giant transparent glass cubes and scaffolding structures, sunrise warm light, sense of freedom and openness, " + STYLE,
    # 能力
    "capabilities": "an organized floating armory of glowing game-dev artifacts: orange energy sword, amber shield emblem, hovering camera rig, music notes ribbon, neural network orb, arranged in a bright gallery hall with cream walls, " + STYLE,
    # 场景
    "scenarios": "three floating vignette dioramas in warm sunset light: a tiny RPG village, a sculpting table with terrain tools, a DCC software window bridging into the 3D world with orange light beams, " + STYLE,
    # 架构
    "architecture": "elegant cross-section diagram of a six-layer engine tower made of translucent amber glass, each layer glowing softly orange, supported by Vulkan-style geometric pillar, warm cream sky background, " + STYLE,
    # 真实状态
    "status": "a bright construction site of a monumental engine statue half-finished, warm sunset light, scaffolding, honest blueprint banners in orange and cream, hopeful atmosphere, " + STYLE,
    # FAQ
    "faq": "a friendly row of floating question-mark blocks in warm amber and orange, one block open revealing a glowing sun, cream sky with soft clouds, playful editorial illustration, " + STYLE,
    # 诗
    "poem": "classical chinese tower on an ancient city wall at dusk, rain clouds gathering, wind bending reeds and willows, warm amber sunset breaking through clouds over a river valley, poetic tang-dynasty atmosphere, " + STYLE,
}

# 4K：2720x1536 是 16:9 2K；4K 用 3840x2160（均 32 倍数，16:9）
SIZE = os.environ.get("HERO_SIZE", "4096x2304")


def gen(name: str, prompt: str, retries: int = 3) -> str:
    body = json.dumps({
        "model": "sensenova-u1.5-lite",
        "prompt": prompt,
        "n": 1,
        "size": SIZE,
        "output_format": "png",
        "response_format": "url",
        "watermark": False,
    }).encode()
    last = ""
    for i in range(retries):
        try:
            req = urllib.request.Request(API, data=body, headers={
                "Authorization": "Bearer " + KEY,
                "Content-Type": "application/json",
            })
            with urllib.request.urlopen(req, timeout=300) as r:
                data = json.loads(r.read())
            url = data["data"][0]["url"]
            # 立即转存（url 24h 失效）
            with urllib.request.urlopen(url, timeout=120) as r:
                img = r.read()
            path = os.path.join(OUT, name + ".png")
            with open(path, "wb") as f:
                f.write(img)
            return path
        except Exception as e:  # noqa: BLE001
            last = repr(e)
            print(f"  retry {i+1} for {name}: {last}", flush=True)
            time.sleep(2 ** i)
    raise RuntimeError(f"{name} failed: {last}")


def main() -> int:
    if not KEY:
        print("SENSENOVA_API_KEY not set", file=sys.stderr)
        return 1
    only = sys.argv[1:] or list(PAGES)
    for name in only:
        if name not in PAGES:
            print("unknown page:", name, file=sys.stderr)
            continue
        t0 = time.time()
        path = gen(name, PAGES[name])
        print(f"[ok] {name} -> {path} ({time.time()-t0:.0f}s)", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
