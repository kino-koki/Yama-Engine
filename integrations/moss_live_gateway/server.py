"""MOSS-Live → YamaEngine 全双工网关。

引擎侧协议（详见 MossLiveProtocol.cs）：
  client→server: session_start / audio_in / text_in / interrupt / action_result / context
  server→client: ready / state / speech_started / speech_ended / text_delta / text_done /
                 audio / viseme / emotion / action / interrupt / error

后端模式（MOSS_BACKEND）：
  mock       本地可控对话（默认）。用于引擎联调、CI、无外网/无官方端点时。
  passthrough 直接与 MOSS-Live 官方（或自建）WebSocket 端点双向透传，
              要求对端已按本协议说话；官方协议落地后在这里加字段映射即可。
  http       过渡方案：用 OpenAI 兼容 /chat/completions 生成文本，再由网关合成 viseme/action。

运行：
  pip install -r requirements.txt
  uvicorn server:app --host 127.0.0.1 --port 8091
"""

from __future__ import annotations

import asyncio
import json
import os
import random
import re
from typing import Any, Dict, List, Optional

import httpx
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

app = FastAPI(title="MOSS-Live Gateway", version="1.0")

MOSS_BACKEND = os.getenv("MOSS_BACKEND", "mock").lower()
MOSS_LIVE_UPSTREAM = os.getenv("MOSS_LIVE_UPSTREAM", "")
MOSS_HTTP_ENDPOINT = os.getenv("MOSS_HTTP_ENDPOINT", "")
MOSS_HTTP_MODEL = os.getenv("MOSS_HTTP_MODEL", "moss-live")
MOSS_HTTP_KEY = os.getenv("MOSS_HTTP_KEY", "")


class SessionState:
    def __init__(self, socket: WebSocket) -> None:
        self.socket = socket
        self.npc_id = "npc"
        self.name = "NPC"
        self.persona = ""
        self.turn_id = 0
        self.audio_frames = 0
        self.closed = False

    async def send(self, payload: Dict[str, Any]) -> None:
        if self.closed:
            return
        try:
            await self.socket.send_text(json.dumps(payload, ensure_ascii=False))
        except Exception:
            self.closed = True


@app.get("/v1/health")
def health() -> Dict[str, str]:
    return {"status": "ok", "backend": MOSS_BACKEND, "upstream": MOSS_LIVE_UPSTREAM}


@app.websocket("/v1/duplex")
async def duplex(socket: WebSocket) -> None:
    await socket.accept()
    state = SessionState(socket)
    try:
        if MOSS_BACKEND == "passthrough" and MOSS_LIVE_UPSTREAM:
            await _run_passthrough(state)
        else:
            await _run_local(state)
    except WebSocketDisconnect:
        pass
    finally:
        state.closed = True
        try:
            await socket.close()
        except Exception:
            pass


# ─────────────────────────── 本地/HTTP 后端 ───────────────────────────


async def _run_local(state: SessionState) -> None:
    while not state.closed:
        raw = await state.socket.receive_text()
        try:
            message = json.loads(raw)
        except json.JSONDecodeError:
            await state.send({"type": "error", "message": "invalid json"})
            continue

        event = message.get("type", "")
        if event == "session_start":
            state.npc_id = message.get("npc_id", "npc")
            state.name = message.get("name", "NPC")
            state.persona = message.get("persona", "")
            await state.send({"type": "ready", "session_id": f"{state.npc_id}-{random.randint(1, 9999)}"})
            await state.send({"type": "state", "value": "listening"})
        elif event == "text_in":
            await _respond_text(state, message.get("text", ""))
        elif event == "audio_in":
            state.audio_frames += 1
            if state.audio_frames >= 12:
                state.audio_frames = 0
                await _respond_text(state, "（语音输入）")
        elif event == "interrupt":
            await state.send({"type": "state", "value": "listening"})
        elif event == "action_result":
            await state.send({"type": "state", "value": "speaking" if message.get("status") == "completed" else "listening"})
        elif event == "context":
            pass
        else:
            await state.send({"type": "error", "message": f"unknown client event: {event}"})


async def _respond_text(state: SessionState, text: str) -> None:
    if not text.strip():
        return
    state.turn_id += 1
    await state.send({"type": "speech_started", "turn_id": state.turn_id})
    await state.send({"type": "state", "value": "thinking"})

    reply = await _generate_reply(state, text)
    await state.send({"type": "state", "value": "speaking"})
    for chunk in _chunk(reply, 8):
        await state.send({"type": "text_delta", "text": chunk, "turn_id": state.turn_id})
        await asyncio.sleep(0.03)
    await state.send({"type": "text_done", "text": reply, "turn_id": state.turn_id})

    # viseme 流：中文按字均匀分布开口度。
    for ch in re.findall(r"\S", reply)[:40]:
        viseme = "AA" if ch in "啊呀哈" else random.choice(["AA", "EE", "OO", "MM"])
        await state.send({
            "type": "viseme",
            "viseme": viseme,
            "weight": 0.75 if viseme == "AA" else 0.45,
            "duration_ms": 60,
        })
        await asyncio.sleep(0.02)

    action = _infer_action(text, reply)
    if action:
        await state.send({
            "type": "action",
            "name": action["name"],
            "prompt": action["prompt"],
            "duration_seconds": 2.5,
            "wait_for_completion": True,
            "metadata": {"npc": state.npc_id},
        })
    await state.send({"type": "emotion", "label": action["emotion"] if action else "neutral", "intensity": 0.7})
    await state.send({"type": "speech_ended", "turn_id": state.turn_id})
    await state.send({"type": "state", "value": "listening"})


async def _generate_reply(state: SessionState, text: str) -> str:
    if MOSS_BACKEND == "http" and MOSS_HTTP_ENDPOINT:
        try:
            async with httpx.AsyncClient(timeout=25) as client:
                response = await client.post(
                    MOSS_HTTP_ENDPOINT,
                    headers={"Authorization": f"Bearer {MOSS_HTTP_KEY}"} if MOSS_HTTP_KEY else {},
                    json={
                        "model": MOSS_HTTP_MODEL,
                        "messages": [
                            {"role": "system", "content": state.persona or "你是一个游戏 NPC"},
                            {"role": "user", "content": text},
                        ],
                        "max_tokens": 160,
                    },
                )
                response.raise_for_status()
                data = response.json()
                return data["choices"][0]["message"]["content"].strip()
        except Exception as exc:
            return f"（MOSS-Live 端点不可用，降级应答）我听到了：{text[:24]}。原因：{exc}"
    return f"{state.name}：我听到了「{text[:24]}」。现在就照做。"


def _infer_action(player_text: str, reply: str) -> Optional[Dict[str, str]]:
    text = player_text + reply
    if any(word in text for word in ("挥手", "告别", "wave", "打招呼", "你好")):
        return {"name": "wave", "prompt": "NPC 面向玩家自然挥手致意，脚部稳定", "emotion": "joy"}
    if any(word in text for word in ("走", "过去", "go", "靠近", "前往")):
        return {"name": "walk", "prompt": "NPC 朝目标方向自然行走三步后停下", "emotion": "neutral"}
    if any(word in text for word in ("坐下", "sit", "休息")):
        return {"name": "sit", "prompt": "NPC 屈膝坐到地面并调整上身", "emotion": "calm"}
    return None


def _chunk(text: str, size: int) -> List[str]:
    return [text[i : i + size] for i in range(0, len(text), size)] or [""]


# ─────────────────────────── 官方端点透传 ───────────────────────────


async def _run_passthrough(state: SessionState) -> None:
    """双向透传。官方 MOSS-Live 协议若与本协议不同，请在此处做字段映射。"""
    import websockets

    async with websockets.connect(MOSS_LIVE_UPSTREAM) as upstream:
        async def client_to_upstream() -> None:
            while not state.closed:
                raw = await state.socket.receive_text()
                await upstream.send(raw)

        async def upstream_to_client() -> None:
            while not state.closed:
                raw = await upstream.recv()
                await state.send(json.loads(raw))

        await asyncio.gather(client_to_upstream(), upstream_to_client())
