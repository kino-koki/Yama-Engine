---
name: Yama 物理守恒门禁
description: 核查质量/元素/能量/动量/角动量守恒、位级快照回滚与 CCD 不漏报
activation: auto
triggers: 物理门禁, 守恒, 回滚, CCD, TOI, 物理断言
provides: yama-physics-gate
suggests: yama-physics-gate, yama-sota-audit
priority: 84
updated_at: 0
use_count: 0
---
# Yama 物理守恒门禁

1. 确认 `ConservationLedger` 四本账全部过账，无隐式产生能量。
2. 跑 `RealityPhysics*Tests.cs` 全套：角动量 L 作状态量、2000 步逐位守恒、
   XPBD/PBF、15 轴 SAT + TOI 子步、动态 AABB 树 parity、面裁剪流形。
3. 快照 `Snapshot/Restore/RewindTo` 逐位一致；warm 缓存必须进快照。
4. CCD：核对「保守膨胀报接触 → L 版/割线 CA 不漏报」，float32 停滞不返回 −1。
5. 3×3 接触质量矩阵：`C·K·C = I` 白化恒等式、对角退化 parity、非对角差异。

约束：不把 float64 本地预演倍率当成 float32 云端保证；
加速类断言以机制级（TOI parity / 不漏报 / 迭代有界 / AcceleratedSteps）为准。
