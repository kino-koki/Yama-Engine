# Changelog

本文件遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 与语义化版本。
原型阶段版本号以 `0.x` 表示，**未发布正式版本**；条目以 `main` 分支真实提交为准。

## [Unreleased]

### 修复

- 移除 `YamaEngine/Audio/VorbisDecoder.cs` 中写死到开发者本机用户目录的调试输出，改为系统临时目录。
- `YamaEngine/DCC/DCCBridge.cs` 的 DCC 自动探测支持环境变量覆盖，不再只能依赖特定安装路径；修正 Assimp 安装提示。
- 修正 README 中 Tests 徽章复用 Build 徽章的问题。
- 修正 `docs/FOUNDATION_STATUS.md` 中 `.NET 8.0.422` 的过时口径，统一为 `global.json` 锁定的 `10.0.203`。

### 新增

- `SECURITY.md`（漏洞上报、原生崩溃 dump、凭据处理规范）。
- `CODE_OF_CONDUCT.md`、`NOTICE`（第三方依赖与许可证）。
- `docs/GAP_ANALYSIS_CORRECTED.md`：以远端 `main` 为基准的差距分析，撤回基于本地残缺副本的误判。
- `.github/workflows/repo-guards.yml` + `scripts/path_guard.py`：无需 SDK 的静态仓库守卫（硬编码路径 / 孤儿源码 / 治理文件 / 占位台账）。

## [0.0.1] — 2026-11-18

### 修复

- Vorbis 解码器：修复 MDCT 逆变换就地（in-place）别名覆盖缺陷，参考信号相关系数 0.075 → 0.966（`3411fcf`）。
- 同批次修复 floor1 高邻居未更新、补齐 FLAC / MP3 / Vorbis 解码器实现。

## [0.0.1-pre] — 2026-11-01

### 修复

- 全部托管 C# 项目编译错误清零（`YamaEngine.Tests` 115→0，`2f4f8b8`；`YamaEditor` 56→0，`b900c87`）。
- 历史峰值 1414 个编译错误（2026-08-07）→ 374（2026-08-22）→ 0（2026-11-01）。
- WinUI 3 XAML 框架适配、CS0246 / CS0104 / CS0115 / CS9244 等根因分类修复。

### 新增

- `.github/workflows/build.yml`：`-warnaserror` 构建门禁 + xUnit 1559 用例 + `YamaCLI smoke 60` 无头回归。
- `scripts/build_verify.ps1`：本地可复现构建与真实报告。
- `docs/` 下多份基于真实 `dotnet build` 输出的状态报告。

## [基线] — 2026-08-22

- 首个提交 `fa66f5f`「基线提交：抢救前快照」，仓库纳入版本管理。
- 此前开发以文件时间戳为可考证据，见 `README.md` 的「项目起点」说明。

---

> 说明：本文件由仓库真实提交历史整理。2026-11-15 的构建状态（0 错误 / 0 警告）以 `README.md` 与 CI 输出为准。
