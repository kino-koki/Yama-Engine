# 真实物理内核 · 第十六轮：耦合质量选点缩减接入窄相（2026-11-28）

延续口径：不新增语言、不动渲染管线、不改 sln/CMake/NuGet/csproj、不宣称 SOTA。
「全部自研」= 引擎功能面代码不借鉴其他开源引擎。

## 1. 接线了什么

第十四轮 `YrContactMassMatrix3x3.ReduceByCoupledMass` 一直是算法原型：
按 K^{-1/2} 白化的 18D 耦合 Jacobian 行做严格 MGS 选点，但没有参与真实接触生成。
本轮把它接进 `YrNarrowPhase.Build`：

- 新开关 `UseCoupledMassReduction`（默认 **false**），关闭时 `Build` 与第十五轮逐字一致。
- 开启时对每对命中按 `YrContactManifold` 同口径算**逐点侵入深度**
  `d = ra - (p - centerA)·n`，用两体质量/惯量/姿态/质心构造 `YrContactMass`，
  再调 `ReducePointsByCoupledMass` 缩到 ≤4 点，回填 `pts/ids`。
- `YrPhysicsPipeline` 转发开关与 `LastReducedPoints`（纯观测量，**不进 Snapshot**）。

诚实边界：缩减仍用单点接触的 3×3 K 近似；逐点深度是顶点侵入近似，不是
Sutherland-Hodgman 面裁剪的精确侵入；`LastReducedPoints` 只记最后一次命中的点数。

## 2. 全仓第三方引用清单（用户「全部自研」口径的机器可判定深化）

新增 `docs/THIRD_PARTY_INVENTORY.json`：13 个 `PackageReference` + 3 个测试包 +
`third_party/dxc` 二进制 + Code - OSS submodule 精确 commit + Rust crate / Python
集成标注 + 跨项目参考署名 + 禁止借鉴的引擎列表。

`RepoContractTests` 新增 2 个 `[Fact]`：
- `TestThirdPartyInventoryMatchesCsprojAndSubmodule`：清单与 csproj 的 13 个包逐项一致、
  每项都有版本与 SPDX、submodule tag/commit 与 `.gitmodules` / lock 一致。
- `TestNoForeignEngineSourceUrlsInWholeSources`：**全文**（不再只扫文件头 40 行）
  扫描 `YamaEngine/**/*.cs` 的 904 个文件，不得出现 Godot / Unreal / Unity / PhysX /
  Bullet / Box2D / Jolt 的源码仓库 URL。README 的「对标 UE5」等描述性文字不参与扫描。

清单边界（诚实）：`integrations/*/requirements.txt` 是独立 Python 集成服务的
工具链运行时依赖，不编译进 `YamaEngine`、不参与引擎功能面；其第三方包许可由
各自发行包 LICENSE 约束，未逐项列入本清单。本清单的可判定门禁只覆盖
`YamaEngine.csproj` 的 13 个 `PackageReference`、3 个测试包、`third_party/dxc`
与 Code - OSS submodule。

## 3. Tuan-CLI 融合（仍只改 Yama 侧）

`yama-coupled-mass` skill 增补「选点缩减是否接进窄相」核查步骤；
`yama_gate.py` hook 契约增加 `UseCoupledMassReduction` 检查；unittest 同步。
**未向 Tuan-CLI 仓库写入**（遵循 `TUAN.md` 逐次确认规则）。

### 推 CI 后的红灯与真修（不放宽门禁）

新 `[Fact]` 首次在云端跑 Test step 红灯。本地按推送后的合并视图精确复现：
`YamaEngine/**/*.cs` 共 **911** 个文件，唯一命中的禁止 URL 是
`YamaEngine/Physics/JoltPhysicsBridge.cs:11` 的 `https://github.com/jrouwe/JoltPhysics`。
其余 3 个 `github.com` URL（assimp releases / ASTC encoder / DirectXShaderCompiler）
是合法工具链依赖，不在禁止列表，**没有**因此放宽容差或删条目。

真修三件事，门禁的禁止列表与扫描范围一字未改：

1. 源码头部注释改为「只写后端名称 + 指向清单」，不再裸写引擎仓库 URL；
2. `docs/THIRD_PARTY_INVENTORY.json` 新增 `optional_physics_backends`，如实登记
   Jolt Physics（MIT / P/Invoke / `YamaJolt.dll`），并明确它**不是自研内核**；
3. `docs/SELF_RESEARCH_BOUNDARY.md` 显式披露这个可选后端，划清
   「用库」与「抄引擎」、以及它与 `YamaEngine/Reality/` 自研内核的边界。

这条红灯不是坏事：它把「仓库里存在第三方可选后端」从隐性事实变成
机器可判定、文档可审计的显式事实，比假装没有更贴合「全部自研」口径。

## 回归门禁

十六轮累计 **222 项**反射式断言（210 + 本轮 12）+ 5 个音频用例，
另有 `RepoContractTests` 的 12 个 xunit `[Fact]` 契约门禁。
无 Skip、无吞异常、无阈值放宽；诊断/凭据残留 0。

## 不能宣称的（不变且不缩小）

G0–G3 已闭合；G4–G8（真机 GPU / 规模 / 平台 SDK / 生态 / 外部验证）本环境
物理上不具备。Code - OSS 完整 Electron workbench 未在现有 CI 编译。
**不宣称已达 SOTA。**
