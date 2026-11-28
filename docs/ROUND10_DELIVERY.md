# 第十轮交付：向 SOTA 方向的算法层深化（已推 main）

## 本轮做了什么

1. **`YrConservativeAdvancement`**：迭代式 CA，用局部推进量 t 的旋转上界
   替代第七轮整帧 dt 膨胀。零角速度逼近解析 TOI；未饱和区更精确（不早报）；
   **并修复第七轮 `min(1,...)` 饱和在高角速下低估真实旋转上界约 7.8 倍、
   从而漏报碰撞的缺陷**（饱和区 CA 更早报 = 更保守、不漏报）。
2. **`YrFaceClipping.BuildMultiFace` / `BuildMultiFaceReduced`**：多参考面流形，
   稳定 Id 按 `(faceId, pointId)` 分段，解决第八轮的多面 Id 冲突局限。
3. **`YrContactReduction`**：farthest-point 几何缩减，>4 点降到 4 点且确定性。
4. **三个默认关闭的开关**（`UseExactCa` / `UseMultiFace` + 既有逐点 warm），
   管线只透传，核心求解路径一行未改。
5. **14 项新回归** → 十轮累计 **121 项**物理断言，云端真实执行。
6. **`docs/SOTA_GAP_ANALYSIS.md`**：明确回答「距离行业 SOTA 还差什么」，
   把差距分成 G0–G8 九道 Gate，标出 G4–G8 在本环境物理上不可达。

## 诊断轮（CI 红灯 → 全绿）

第一次 CI 红灯回读到 3 个失败，全部真修：① CA 在 float32 下
`dist=1.0001e-4` 时 `delta=6.8e-9` 被 `t` 的 ulp 舍回、空转 32 次
→ 加 `nextT<=t` 停滞判定（不放宽 ε、Residual 可观测）；② 管线测试错误地
要求「多面开关开启后与默认路径逐位一致」→ 拆成 CA parity 与多面确定性/回滚两段。

## 诚实结论

**不能宣称达到行业 SOTA。** G0–G3（工程基线/确定性/守恒/音频）已闭合；
G4–G8（真机 GPU 帧、规模压测、多平台导出、生态、外部验证）需要
GPU、主机 SDK、团队与时间，沙盒不具备。本轮缩小的是**算法层可判定差距**。

## 未做 / 不变

- 不动渲染管线、不新增语言、不改 sln/CMake/NuGet/csproj、不新增 Rust 导出。
- 音频侧零改动（第九轮已清零看板）。
- 仍不宣称 GTA6/RAGE 级、Godot 3 全能力、任意 MP3 位级一致。
- GPU 帧 / `0xC0000005` / 百万粒子真机压测 / Rust 原生内核编译仍待新版本。

## 下一步

1. 完整旋转 CA（四元数积分 + 重枚举 15 轴）。
2. 多面流形各自重选入射面；接触缩减改按 Jacobian 线性独立性。
3. 动态树接 GPU 帧循环压测；编译加载 Rust 原生内核跑 parity。
4. 扩 MP3 夹具覆盖（立体声 / MPEG-1 44.1k / 短块 / ID3v2 / CRC）。
