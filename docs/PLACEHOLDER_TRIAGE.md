# 占位信号分类处置台账

> 由 `scripts/capability_audit.py` 自动生成 `placeholder_inventory.csv`（文件 / 行号 / 信号 / 类别 / 原文）。
> 上一轮基于本地残缺副本统计的「742 处 / 176 文件」口径已废弃；本台账以**当前仓库实扫**为准，
> 并把 `class` 列分成 `production` 与 `test`，避免把测试里正常的 `Mock` 当成技术债。

## 四类处置规则

| 类别 | 判定 | 处置 |
|---|---|---|
| `test` + `Mock`/`Stub` | 测试替身，正常用法 | **保留**，不计入技术债 |
| `production` + `TODO`/`FIXME`/`未实现` | 真实未完成 | 每条建 issue，按模块排期（优先 RHI / Vulkan / Physics / ECS / Assets） |
| `production` + `Stub`/`Placeholder` | 占位实现 | 判断是否可删：有 `[Obsolete]` 迁移说明的保留，否则补实现或条件编译隔离 |
| `production` + `NotSupportedException` | 显式不支持 | 在 `docs/PLATFORM_MATRIX.md` 登记，别静默吞掉 |

## 重点模块（上一轮扫描的分布，供排期参考）

`Testing`(123) / `YamaEngine.Tests`(84) / `ECS`(79) / `Platforms`(74) / `Physics`(44) /
`Vulkan`(44) / `RHI`(41) / `Assets`(35) / `Rendering`(24) / `Build`(22) / `GameFramework`(21) / `Editor`(19)。

## 三条硬规则

1. **先分类再删**：`grep -c` 出来的数字不是待办条数，`NpcBrainTests.cs` 里的 `Mock` 是正确写法。
2. **主机 SDK stub 单列**：`Platforms/ConsoleSDKAbstraction.cs` 的 74 处 TODO 受 NDA 限制，
   处置是「条件编译隔离 + 文档写明」，不是「实现」（见 `BUILD_DECISIONS.md` / `PLATFORM_MATRIX.md`）。
3. **禁止靠 `Compile Remove` 长期压着**：18 条 `*Tests.cs` 的 Remove 会让测试永不运行，按 D6 搬走。

## 验收

```bash
python3 scripts/capability_audit.py
# 检查 placeholder_inventory.csv 里 class=production 的行都有对应 issue 链接或「保留」结论
```
