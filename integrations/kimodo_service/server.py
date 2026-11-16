"""Kimodo → YamaEngine 动作 sidecar.

职责：
  1. 调用 NVIDIA Kimodo（CLI 或自定义命令）生成动作；
  2. 把 NPZ / BVH 解码成引擎可直接消费的 JSON 协议（见 KimodoMotionProtocol.cs）；
  3. 无 GPU / 无权重时提供 Mock 后端，保证引擎链路可端到端联调。

运行：
  pip install -r requirements.txt
  uvicorn server:app --host 127.0.0.1 --port 8088

环境变量：
  KIMODO_BACKEND=mock|local       默认 local，未找到 CLI 时自动回退 mock
  KIMODO_CLI_TEMPLATE             自定义 CLI 模板，占位符 {prompt} {duration} {fps} {seed} {output} {model}
  KIMODO_MODEL                    默认 kimodo-soma-rp
"""

from __future__ import annotations

import json
import math
import os
import shlex
import subprocess
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

app = FastAPI(title="Kimodo Motion Sidecar", version="1.0")


# ─────────────────────────── 协议模型 ───────────────────────────


class ConstraintModel(BaseModel):
    type: str = Field(..., alias="type")
    frame_indices: List[int] = Field(default_factory=list)
    smooth_root_2d: Optional[List[List[float]]] = None
    global_root_heading: Optional[List[List[float]]] = None
    root_positions: Optional[List[List[float]]] = None
    local_joints_rot: Optional[List[List[List[float]]]] = None
    joint_names: Optional[List[str]] = None

    model_config = {"populate_by_name": True}


class GenerateRequest(BaseModel):
    prompt: str
    duration_seconds: float = 3.0
    fps: float = 30.0
    model: str = "kimodo-soma-rp"
    skeleton: str = "somaskel77"
    seed: int = -1
    num_samples: int = 1
    constraints: List[ConstraintModel] = Field(default_factory=list)
    metadata: Dict[str, str] = Field(default_factory=dict)


# ─────────────────────────── 后端 ───────────────────────────


class MotionBackend:
    name = "base"

    def generate(self, request: GenerateRequest) -> Dict[str, Any]:
        raise NotImplementedError


class MockBackend(MotionBackend):
    """无模型回退：生成一段确定性的挥手/行走动作，用于验证引擎协议与播放。"""

    name = "mock"

    def generate(self, request: GenerateRequest) -> Dict[str, Any]:
        fps = request.fps
        frames = max(2, int(round(request.duration_seconds * fps)))
        joints = [
            ("Hips", -1, [0.0, 0.0, 0.0]),
            ("Spine", 0, [0.0, 0.15, 0.0]),
            ("Chest", 1, [0.0, 0.20, 0.0]),
            ("Neck", 2, [0.0, 0.15, 0.0]),
            ("Head", 3, [0.0, 0.10, 0.0]),
            ("RightShoulder", 2, [0.18, 0.12, 0.0]),
            ("RightArm", 5, [0.30, 0.0, 0.0]),
            ("RightHand", 6, [0.28, 0.0, 0.0]),
        ]
        skeleton = {
            "name": "MockHumanoid",
            "joints": [
                {
                    "name": name,
                    "parent_index": parent,
                    "rest_position": [0.0, rest[1], 0.0],
                    "rest_rotation": [0.0, 0.0, 0.0, 1.0],
                }
                for name, parent, rest in joints
            ],
        }
        bone_map = {"Hips": "Hips", "RightArm": "RightArm", "RightHand": "RightHand"}

        rotations: List[List[List[float]]] = []
        roots: List[List[float]] = []
        wave = request.model == "mock-wave" or "wave" in request.prompt.lower()
        for f in range(frames):
            t = f / max(1, frames - 1)
            q_wave = _quat_from_axis_angle([0.0, 1.0, 0.0], math.sin(t * math.pi) * 1.1) if wave else _identity()
            frame = [_identity() for _ in joints]
            frame[6] = q_wave  # RightArm
            frame[7] = q_wave
            rotations.append(frame)
            roots.append([0.0, 1.0, 0.0])

        return {
            "id": f"mock-{abs(hash(request.prompt)) % 100000}",
            "fps": fps,
            "duration_seconds": frames / fps,
            "skeleton": skeleton,
            "bone_map": bone_map,
            "rotations": rotations,
            "root_positions": roots,
            "foot_contacts": None,
            "warning": "mock backend: 未加载 Kimodo 模型",
        }


class BvhBackend(MotionBackend):
    """调用 Kimodo CLI，解析其 BVH 输出（SOMA 77 关节），必要时叠加 NPZ 根轨迹。"""

    name = "kimodo-cli"
    DEFAULT_TEMPLATE = (
        'kimodo generate --prompt "{prompt}" --duration {duration} --fps {fps} '
        "--seed {seed} --model {model} --output {output} --bvh"
    )

    def __init__(self) -> None:
        self.template = os.getenv("KIMODO_CLI_TEMPLATE", self.DEFAULT_TEMPLATE)
        self.timeout = int(os.getenv("KIMODO_TIMEOUT", "180"))

    def generate(self, request: GenerateRequest) -> Dict[str, Any]:
        with tempfile.TemporaryDirectory(prefix="kimodo_") as tmp:
            output = Path(tmp)
            constraints_path = self._write_constraints(output, request)
            command = self.template.format(
                prompt=request.prompt.replace('"', "'"),
                duration=request.duration_seconds,
                fps=request.fps,
                seed=request.seed,
                model=request.model,
                output=str(output),
            )
            if constraints_path:
                command += f' --constraints "{constraints_path}"'
            if request.seed < 0:
                command = command.replace("--seed -1", "")

            try:
                completed = subprocess.run(
                    shlex.split(command), capture_output=True, text=True, timeout=self.timeout
                )
            except FileNotFoundError as exc:
                raise RuntimeError(f"Kimodo CLI 不可用: {exc}") from exc
            except subprocess.TimeoutExpired as exc:
                raise RuntimeError(f"Kimodo 生成超时: {exc}") from exc

            if completed.returncode != 0:
                raise RuntimeError(
                    f"Kimodo CLI 失败({completed.returncode}): {completed.stderr[-800:]}"
                )

            bvh_files = sorted(output.glob("**/*.bvh"))
            if not bvh_files:
                raise RuntimeError("Kimodo 未输出 BVH（请确认 CLI 支持 --bvh 且使用 SOMA 模型）")

            motion = parse_bvh(bvh_files[0])
            npz_files = sorted(output.glob("**/*.npz"))
            if npz_files:
                _apply_npz_root(motion, npz_files[0])

            motion["id"] = f"kimodo-{abs(hash(request.prompt)) % 100000}"
            motion["bone_map"] = _default_soma_bone_map()
            return motion

    @staticmethod
    def _write_constraints(output: Path, request: GenerateRequest) -> Optional[str]:
        if not request.constraints:
            return None
        path = output / "constraints.json"
        path.write_text(
            json.dumps([c.model_dump(by_alias=True, exclude_none=True) for c in request.constraints]),
            encoding="utf-8",
        )
        return str(path)


def _default_soma_bone_map() -> Dict[str, str]:
    return {
        "Hips": "Hips",
        "Chest": "Chest",
        "Head": "Head",
        "LeftHand": "LeftHand",
        "RightHand": "RightHand",
        "LeftFoot": "LeftFoot",
        "RightFoot": "RightFoot",
    }


# ─────────────────────────── BVH 解析 ───────────────────────────


def _identity() -> List[float]:
    return [0.0, 0.0, 0.0, 1.0]


def _quat_from_axis_angle(axis: List[float], angle: float) -> List[float]:
    norm = math.sqrt(sum(v * v for v in axis)) or 1.0
    half = angle * 0.5
    s = math.sin(half) / norm
    return [axis[0] * s, axis[1] * s, axis[2] * s, math.cos(half)]


def _mat_to_quat(m: np.ndarray) -> List[float]:
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        w, x, y, z = 0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s
    return [x, y, z, w]


def _euler_to_quat(channels: List[str], values: List[float]) -> List[float]:
    m = np.eye(3)
    for channel, value in zip(channels, values):
        angle = math.radians(value)
        if channel == "Zrotation":
            m = m @ _rot_z(angle)
        elif channel == "Xrotation":
            m = m @ _rot_x(angle)
        elif channel == "Yrotation":
            m = m @ _rot_y(angle)
    return _mat_to_quat(m)


def _rot_x(a: float) -> np.ndarray:
    return np.array([[1, 0, 0], [0, math.cos(a), -math.sin(a)], [0, math.sin(a), math.cos(a)]])


def _rot_y(a: float) -> np.ndarray:
    return np.array([[math.cos(a), 0, math.sin(a)], [0, 1, 0], [-math.sin(a), 0, math.cos(a)]])


def _rot_z(a: float) -> np.ndarray:
    return np.array([[math.cos(a), -math.sin(a), 0], [math.sin(a), math.cos(a), 0], [0, 0, 1]])


def parse_bvh(path: Path) -> Dict[str, Any]:
    """解析 Kimodo SOMA BVH；BVH 导出为厘米，此处统一转米。"""
    lines = path.read_text(encoding="utf-8", errors="ignore").splitlines()
    joints: List[Dict[str, Any]] = []
    parents: Dict[str, int] = {}
    stack: List[int] = []
    channels: List[str] = []
    frame_time = 1.0 / 30.0

    idx = 0
    while idx < len(lines):
        tokens = lines[idx].strip().split()
        if not tokens:
            idx += 1
            continue
        keyword = tokens[0].upper()
        if keyword in ("ROOT", "JOINT"):
            name = tokens[1]
            parents[name] = stack[-1] if stack else -1
            joint_index = len(joints)
            joints.append({"name": name, "parent_index": parents[name], "offset": [0.0, 0.0, 0.0], "channels": []})
            stack.append(joint_index)
        elif keyword == "OFFSET":
            joints[stack[-1]]["offset"] = [float(v) / 100.0 for v in tokens[1:4]]
        elif keyword == "CHANNELS":
            count = int(tokens[1])
            joint_channels = tokens[2 : 2 + count]
            joints[stack[-1]]["channels"] = joint_channels
            if joint_channels:
                channels.extend(joint_channels)
        elif keyword == "}" and stack:
            stack.pop()
        elif keyword == "Frames:":
            frame_count = int(tokens[1])
            frame_time = float(lines[idx + 1].split()[1]) if idx + 1 < len(lines) else frame_time
            motion_lines = [ln for ln in lines[idx + 2 :] if ln.strip()]
            break
        idx += 1
    else:
        raise ValueError("BVH 缺少 MOTION 段")

    fps = 1.0 / frame_time if frame_time > 0 else 30.0
    values = np.array([[float(v) for v in line.split()] for line in motion_lines], dtype=np.float64)
    if values.shape[0] == 0:
        raise ValueError("BVH 没有帧数据")

    n_frames = min(frame_count, values.shape[0])
    rotations: List[List[List[float]]] = []
    roots = values[:, 0:3].copy() / 100.0  # BVH 厘米 → 米
    cursor = 3  # 根关节前三个通道视为位置
    root_joint = 0
    for f in range(n_frames):
        frame_rots = [[0.0, 0.0, 0.0, 1.0] for _ in joints]
        cursor = 3
        for j, joint in enumerate(joints):
            rot_channels = [c for c in joint["channels"] if c.endswith("rotation")]
            rot_values = values[f, cursor : cursor + len(rot_channels)]
            cursor += len(rot_channels)
            if rot_channels:
                frame_rots[j] = _euler_to_quat(rot_channels, list(rot_values))
        rotations.append(frame_rots)

    return {
        "fps": fps,
        "duration_seconds": n_frames / fps,
        "skeleton": {
            "name": "somaskel77",
            "joints": [
                {
                    "name": j["name"],
                    "parent_index": j["parent_index"],
                    "rest_position": j["offset"],
                    "rest_rotation": [0.0, 0.0, 0.0, 1.0],
                }
                for j in joints
            ],
        },
        "rotations": rotations,
        "root_positions": roots.tolist(),
        "foot_contacts": None,
        "_root_joint_index": root_joint,
    }


def _apply_npz_root(motion: Dict[str, Any], npz_path: Path) -> None:
    data = np.load(npz_path)
    if "root_positions" in data:
        motion["root_positions"] = data["root_positions"].astype(float).tolist()
    if "foot_contacts" in data:
        motion["foot_contacts"] = data["foot_contacts"].astype(float).tolist()


# ─────────────────────────── 选择后端 ───────────────────────────


def _select_backend() -> MotionBackend:
    mode = os.getenv("KIMODO_BACKEND", "local").lower()
    if mode == "mock":
        return MockBackend()
    backend = BvhBackend()
    return backend


_backend_holder: List[MotionBackend] = []


def get_backend() -> MotionBackend:
    if not _backend_holder:
        _backend_holder.append(_select_backend())
    return _backend_holder[0]


@app.get("/v1/health")
def health() -> Dict[str, Any]:
    backend = get_backend()
    return {
        "status": "ok",
        "backend": backend.name,
        "model": os.getenv("KIMODO_MODEL", "kimodo-soma-rp"),
        "skeleton": "somaskel77",
        "gpu": os.getenv("KIMODO_GPU", ""),
    }


@app.post("/v1/motion/generate")
def generate(request: GenerateRequest) -> Dict[str, Any]:
    if not request.prompt.strip():
        raise HTTPException(status_code=400, detail="prompt 不能为空")

    backend = get_backend()
    try:
        motion = backend.generate(request)
    except Exception as exc:  # 后端异常统一降级，保证引擎侧拿到可用动作
        if backend.name != "mock":
            fallback = MockBackend().generate(request)
            fallback["warning"] = f"Kimodo 调用失败，已降级 mock: {exc}"
            return fallback
        raise HTTPException(status_code=500, detail=str(exc)) from exc

    motion.pop("_root_joint_index", None)
    return motion
