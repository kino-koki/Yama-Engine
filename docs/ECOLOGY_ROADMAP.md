# 从原型到「可分发引擎」的生态路线

> 对照 Godot 3.5：MIT 许可、官方二进制分发、资产库插件、社区贡献的多语言与 addon 生态、
> 主机经第三方发行商。Yama 远端实测：0 star / 0 fork / 0 follower / 单人提交 / 0 tag / 0 release。

| 阶段 | 目标 | 可判定出口 |
|---|---|---|
| E0 法律与治理 | LICENSE + NOTICE + SECURITY + CHANGELOG + 行为公约 | `dotnet list package --include-transitive` 无许可证冲突（分支 A 已补 4 个文件） |
| E1 版本与签名 | 单一版本真源、签名提交、签名 tag | `git verify-tag v0.1.0-alpha` 通过 |
| E2 第一个 Release | Windows 编辑器二进制 + 示例工程 + 已知限制清单 | 干净机器下载解压能跑 S3 |
| E3 外部验证 | 至少 1 个外部贡献者 / 1 次公开 review / Issue+PR 模板生效 | PR 来自第二个账号且 CI 全绿 |
| E4 资产库 / 插件生态 | `PluginMarketSystem` 接线，第三方 addon 可安装 | 安装一个非自带的插件并生效（E9） |
| E5 宣称收敛 | 删除「超越 UE5 / SOTA / 全平台」等未做基准的表述 | README 无任何未验证对比宣称 |

**不能靠文本完成的部分**：真实用户、外部 review、商店发行、主机授权。
本轮只交付 E0 的文档与 E1–E5 的判定标准，不伪造任何「社区」数据。
