# 自研边界定义（澄清版，2026-11-27）

> 用户澄清：**「全部自研」= 引擎功能面代码不借鉴其他开源/商业引擎的实现**，
> 不是「零第三方依赖」。本文件把这条口径写成可审计的机器可判定规则。

## 1. 定义

| 词 | 含义 |
|---|---|
| **自研（本仓库口径）** | 引擎功能面的**代码**由本项目独立编写：不复制、不移植、不改写 Godot / Unreal / Unity / PhysX / Havok / Bullet / Box2D / Jolt / Bevy / O3DE 等引擎的源码、类结构、算法实现或注释。 |
| **第三方基础设施** | 允许作为**依赖库 / 工具 / 运行时**引入，但必须锁定版本、逐项署名 SPDX、且不得把其功能面实现拷贝进本仓库充当自研。 |
| **借鉴** | 仅允许参考公开**协议规范**（Vulkan spec、Language Server Protocol、Debug Adapter Protocol、FLAC/MP3/Vorbis 格式规范、RFC），落地代码自行编写。 |

因此：**允许**用 Silk.NET 做 Vulkan P/Invoke 绑定、用 Roslyn 编译 C#、用 Code - OSS 做编辑器外壳、用 xunit 跑测试；
**不允许**把 Godot 的 `PhysicsServer`、PhysX 的 `PxRigidDynamic`、Unreal 的 Chaos 求解器等实现搬进 `YamaEngine/`。
前者是「用库」，后者是「抄引擎」。

## 2. 现状核对

- `YamaEngine/` 下 100+ 个子系统目录（`Rendering/`、`Physics/`、`Reality/`、`Audio/`、
  `AI/`、`ECS/`、`Scripting/`、`Vulkan/` …）均为本项目自研代码，
  含前十三轮累计 164 项物理断言与音频/FLAC/Vorbis/MP3 自研解码器。
- 协议参考来源已逐项标注：LSP 3.17 / DAP 1.x / Vulkan 1.4 / RFC / 音频格式规范。
- 第三方基础设施逐项清单见 `docs/THIRD_PARTY_SPX.md`，由
  `RepoContractTests.TestSelfResearchBoundarySpdxCoversEveryPackageReference`
  在每次 CI 中强制覆盖 `YamaEngine.csproj` 的**每一个** `PackageReference`。
- **可选第三方物理后端显式披露**：`YamaEngine/Physics/JoltPhysicsBridge.cs` 是
  对原生 `YamaJolt.dll`（封装 Jolt Physics，MIT）的 P/Invoke 适配层，属于
  「用库」而非「抄引擎」；它**不是自研物理内核**，不参与 `YamaEngine/Reality/`
  的自研接触 / CCD / 守恒链，也不得计入自研成果。许可、版本与来源已在
  `docs/THIRD_PARTY_INVENTORY.json` 的 `optional_physics_backends` 逐项登记；
  源码文件内不再保留该引擎的源码仓库 URL，由
  `RepoContractTests.TestNoForeignEngineSourceUrlsInWholeSources` 全文强制。

## 3. 可判定审计

`scripts/audit_no_engine_copy.py`（纯 stdlib）扫描所有 `.cs/.cpp/.h/.rs` 源文件，
匹配以下「其他引擎源码搬运」特征，命中即非零退出：

- 版权头：`Copyright (c) ... Godot Engine contributors` / `Epic Games` /
  `Unity Technologies` / `NVIDIA PhysX` / `Bullet` / `Box2D` / `Jolt Physics` 等
- 许可头：`SPDX-License-Identifier` 指向其他引擎仓库的 LICENSE 路径
- 源码路径/include：`#include "godot/..."`, `#include "PhysX/..."`, ` unrealengine.com` 源码 URL
- 命名空间直搬：`namespace godot`, `namespace physx`, `namespace Urho3D` 等

**不误报**描述性文字（如 README 的「对标 UE5」「对标 Godot 3」）、
文档里的对标表格、以及合法的第三方库 `using`（Silk.NET / Vortice 等属基础设施）。
审计只针对**源码文件头的版权/许可/路径特征**，不是文本关键词黑名单。

对应 C# 门禁：`RepoContractTests.TestNoForeignEngineCopyrightHeadersInSources`。

## 4. Code - OSS 为什么不算「借鉴引擎」

Code - OSS 是**编辑器/IDE 外壳**（MIT，Electron + TypeScript），不是游戏引擎。
本仓库把它作为**可替换的第三方基础设施**内置，锁定 `microsoft/vscode`
tag `1.148.0`；与引擎相连的 LSP/DAP 客户端、扩展清单、命令面板桥接
**全部自研**（`YamaEngine/Editor/CodeOss/`）。
详见 `ide/codeoss/README.md`。

## 5. 不能翻转的边界

本文件**不宣称**「零第三方」，也不宣称「已达 SOTA」。
SOTA 差距仍以 `docs/SOTA_GAP_ANALYSIS.md` 的 G0–G8 为准：
G0–G3 已闭合，G4–G8（真机 GPU / 规模 / 平台 SDK / 生态 / 外部验证）
本环境物理上不具备。持续推进 ≠ 宣称达标。
