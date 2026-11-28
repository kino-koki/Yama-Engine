# 第十六轮交付（连续推进第三轮）

## 实质增量

1. `ReduceByCoupledMass` 从算法原型**接进** `YrNarrowPhase.Build` 与
   `YrPhysicsPipeline`（默认关闭 → 210 项旧断言与位级快照逐位零回归）。
2. 12 项新物理断言：静态入口有界/确定性/最深点保留/max 参数/奇异安全/空输入、
   耦合 vs 标量选点差异、窄相默认关闭逐位一致、开关接线、管线转发、
   快照格式不变、默认管线零回归。
3. `docs/THIRD_PARTY_INVENTORY.json` 全仓第三方引用清单 + 2 个新 `[Fact]`：
   与 csproj 逐项一致、全文无其他引擎源码 URL。
4. Tuan 侧 skill / hook / unittest 同步，仍未写 Tuan-CLI 仓库。

## CI 红灯真修留痕

第十六轮首次推 develop 时 Test step 红灯。本地复现定位到
`JoltPhysicsBridge.cs` 头部裸写了 Jolt 的源码仓库 URL，被本轮新增的全文门禁
正确拦下。真修是把 URL 移出源码、在第三方清单与自研边界文档里如实登记这个
可选后端（MIT / P/Invoke / 不参与自研 Reality 内核），**没有**删门禁条目、
没有缩扫描范围、没有放宽容差。全仓 911 个 `.cs` 文件全文扫描通过。

## 不能翻转的

- **不宣称已达 SOTA**：G4–G8 未闭合；Code - OSS Electron 完整构建未在现有 CI 编译。
- 不宣称零第三方；不宣称跨接触全局 LCP；不宣称任意 MP3 位级一致。
- 不改 sln/CMake/NuGet/csproj、不动渲染管线、不删断言、不放宽容差。

## 下一步（继续推进，不停）

1. 把 `ReduceByCoupledMass` 与第十五轮的 `UseCoupledMassMatrix` 做联动验证
   （同一帧同时开启，观测选点与求解是否互相一致）。
2. 跨接触点的块 Jacobi / Gauss-Seidel 全局求解（自研、默认关闭、零回归）。
3. 带 Node 20 的专用 job 编译 Code - OSS 1.148.0 干净 MIT workbench 并验收。
4. 按 `TUAN.md` 逐次授权后把适配层合并进 Tuan-CLI 仓库。
5. 扩音频夹具、Rust 原生内核编译加载、真机 GPU 帧循环（需硬件/SDK）。
