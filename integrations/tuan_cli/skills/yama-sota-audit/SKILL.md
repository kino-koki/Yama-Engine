---
name: Yama SOTA 差距审计
description: 按 G0–G8 判定表核对可验证层与不可达层，拒绝把未验证项包装成已达标
activation: auto
triggers: sota, 差距, 审计, G4, G8, 达标
provides: yama-sota-audit
suggests: yama-physics-gate, yama-sota-audit
priority: 86
updated_at: 0
use_count: 0
---
# Yama SOTA 差距审计

1. 读 `docs/SOTA_GAP_ANALYSIS.md`，逐 Gate 判定：G0 工程基线 / G1 确定性 /
   G2 守恒 / G3 音频 / G4 GPU 帧 / G5 规模 / G6 平台 / G7 生态 / G8 外部验证。
2. G0–G3 必须给出云端 CI 证据（run id + step conclusion）；
   G4–G8 在本环境（无 GPU / 无平台 SDK / 无团队 / 无外部用户）**一律标未闭合**。
3. 读 `docs/SELF_RESEARCH_BOUNDARY.md` 与 `docs/THIRD_PARTY_SPX.md`，
   核对「自研=不借鉴其他引擎代码」与 SPDX 全部署名。
4. 跑 `python scripts/audit_no_engine_copy.py .`，clean 必须为 true。
5. 结论里**禁止**写「已达 SOTA」「GTA6/RAGE/Chaos 级」「Godot 全能力」；
   只能写「本轮缩小了算法层可判定差距，G4–G8 未变化」。

约束：专有实现未公开，连量化对照口径都不存在，不得虚构对标结论。
