# 内置 Code - OSS 1.148.0（最新 · 最轻量 · 完整 Clean-MIT 构建）

## 这是什么

`microsoft/vscode` 仓库即 **Code - OSS**，MIT 许可。最新稳定 tag 为
**1.148.0**（2026-11-25 发布），对应 commit
`661626d0e72f97806550790a98fb87116c9db825`。

本仓库以 **submodule gitlink** 锁定该精确 commit（`.gitmodules` +
`UPSTREAM.lock.json`），并提供：

| 文件 | 作用 |
|---|---|
| `product.json` | 去品牌 / 去遥测 / 无 Marketplace 的干净 MIT 构建覆盖（`nameShort="Yama Code - OSS"`, `licenseName=MIT`, `enableTelemetry=false`, `extensionsGallery={}`） |
| `scripts/fetch_codeoss.py` | stdlib 一键拉取、校验 commit、应用 product.json、输出最小构建步骤 |
| `ci/codeoss-gate.yml` | 可选独立 CI job 模板（Node/Electron 构建链） |
| `YamaEngine/Editor/CodeOss/*.cs` | **自研** LSP/DAP 客户端、扩展清单、命令桥接 |

## 「最轻量完整版」怎么理解

- **完整**：完整 Code - OSS workbench 源码由 submodule 精确锁定，
  `fetch_codeoss.py` 可复现地取回并编译，不是裁剪过的残缺 demo。
- **最轻量**：干净 MIT 构建移除微软品牌、遥测、Marketplace gallery、
  内置扩展下载 —— 这些正是 VSCodium 路径去掉的「非必要重量」；
  本仓库不额外塞主题/标语/装饰扩展。
- **不把 1.8 GB 源码 blob 塞进仓库**：upstream 仓库约 1.8 GB，
  GitHub 单文件 100 MB 上限，且现有 `build.yml` 是 Windows/.NET 门禁、
  无 Node/Electron 链。故用 gitlink 锁定 + 脚本复现，而不是一次性 vendor。
  `actions/checkout@v4` 默认不递归 submodule，**不影响现有 CI**。

## 许可边界（必须遵守）

- GitHub `microsoft/vscode` 仓库 = **MIT**（Code - OSS）。
- 微软官方下载的 Visual Studio Code = 构建时替换 `product.json`
  （品牌 / 遥测 / 扩展市场）+ **微软产品许可**，**不是 MIT**。
- 因此本仓库只做干净 MIT 构建，不得分发官方 VS Code 二进制、图标或商标。
- 逐项 SPDX 署名见 `docs/THIRD_PARTY_SPX.md`。

## 与「全部自研」的关系

Code - OSS 是 **IDE 外壳 / 基础设施**，不是游戏引擎。
按 `docs/SELF_RESEARCH_BOUNDARY.md` 的口径：允许作为可替换的第三方
基础设施内置并署名；**引擎相连的所有桥接代码 100% 自研**
（LSP 帧、DAP 帧、扩展清单、命令路由均为本项目编写，未拷贝 VSCodium 脚本
或任何编辑器插件实现）。引擎功能面不借鉴任何开源游戏引擎。

## 本地构建

```bash
git submodule update --init --depth 1 ide/codeoss/upstream
python scripts/fetch_codeoss.py --check        # 只校验 lock + product.json
python scripts/fetch_codeoss.py                # 拉取/校验/应用 product.json，打印构建步骤
# 之后按脚本输出的 npm ci / npm run compile 步骤在带 Node 20 的机器上构建
```

## CI 说明

`ci/codeoss-gate.yml` 是模板（当前推送 token 无 `workflow` scope，
需在网页端移到 `.github/workflows/` 启用）。它与现有 `build.yml` 完全解耦：
现有 -warnaserror / Test / Smoke 门禁不因 Code - OSS 而变化。
