# 第十三轮交付：算法层继续深化（已推 main）

## 本轮三件实质改进

1. **`YrSecantCa`**：把 CA 的固定保守步长换成「实测割线斜率外推 + 4×保守上限 +
   dt 截断」，保留不漏报骨架（`lo` 永远经 `dist>ε` 实测验证）。本地 float64
   预演：真实固定步 24 组标准 CA 最大 34 次 → 割线 9 次（**3.78×**），
   TOI 偏差 `1.4e-17`；L 版带角动量组 23 → 5 次（**4.6×**）。零漏报/误报。
2. **`YrContactEffectiveMass`**：从两体惯量张量算 `K(d)=invM_a+invM_b+
   (r_a×d)·I_a⁻¹(r_a×d)+(r_b×d)·I_b⁻¹(r_b×d)`，替代第十二轮的外部标量权重；
   加库仑摩擦锥 `InsideFrictionCone` / `ProjectToFrictionCone`。
   `ApplyWorldInvInertia` 与 `RigidBody3D` 逐位一致。
3. **`ReduceByRows` / `ReduceByEffectiveMass`**：矩阵构造与严格 MGS 选点解耦，
   前三轮缩减 API 语义一字未改；有效质量与 μ 都能可判定地改变选点。

## 诊断轮（CI 两次红灯 → 全绿）

- 第一次 2 个失败都是测试写法：① 力臂用例 B 质心放 (4,0,0) 导致 p=0 力臂
  反而更大；② 割线迭代写死 `<=16`（来自 float64 预演），float32 下走到 17。
- **第二、三次仍红，暴露两个真算法缺陷**：某组割线 17 次 vs 标准 9 次。
  根因 ① 实测斜率 `measured > motion` 时步长比标准保守步还短 → `slope`
  夹到 `(MinSlope, motion)`；② `pred` 被 dt 截断后又被 `minPred` 拉回
  dt 之外 → 改为循环开头用标准 CA 的严格分离判据提前退出。
- **方法论决定**：float32 下「迭代数一定更少」不稳定，不把它当硬断言，
  改为机制级（TOI parity / 不漏报 / 迭代有界 / `AcceleratedSteps ≥ 2`）。
  **不宣称云端实测加速**；float64 预演 3.78~4.6× 只是预演。

## 14 项新回归

十三轮累计 **164 项**物理断言（9 + 14×10 + 15），云端真实执行。

## 诚实结论

**仍不能宣称行业 SOTA。** 本轮缩小的是算法层可判定差距（CCD 收敛速度、
接触约束选点的物理保真），`docs/SOTA_GAP_ANALYSIS.md` 的 G4–G8 没有变化。
割线 CA 的非单调风险仅被 cap 缓解、K 只取对角项、摩擦锥是一阶近似、
有效质量缩减**尚未接入窄相**（缺两体惯量上下文）。

## 未做 / 不变

不动渲染管线、不新增语言、不改 sln/CMake/NuGet/csproj、不新增 Rust 导出。
音频侧零改动（看板 0 条）。GPU 帧 / `0xC0000005` / 百万粒子真机压测 /
Rust 原生内核编译仍待新版本。

## 下一步

1. 把有效质量缩减接进 `YrContactSolver`，加入完整 3×3 K 非对角耦合。
2. 割线 CA 自适应 cap（按 dist 二阶差分判非单调）。
3. 动态树接 GPU 帧循环压测；Rust 原生内核编译加载；扩 MP3 夹具。
