# 「真正达到 Godot 3 能力」的分阶段路线（诚实版）

> 用户要求「一直补齐，直到真正达到 Godot 3 所有能力，深度」。
> 这份文档把这句话拆成**可判定的 Gate**，并明确每个 Gate 的完成条件与物理边界。

## 结论先说

**不能宣称已完成。** 但也不是「还差 141 项」——远端 `main` 已有 1037 个 `.cs` / 40 篇文档 /
9 个 csproj / 7 个 sln 工程 / 1559 用例 CI。真实差距是三层：

1. **接线未核验**：模块都在，但「新建项目 → 建场景 → 挂脚本 → F5 → 热重载 → 调试 → 导出」没跑通过一遍
2. **端到端未验证**：高级渲染算法没在真实 GPU 跑完整 PBR 帧，运行时还有 `0xC0000005`
3. **少量真实缺失**：GLES2/OpenGL 双渲染器、GDScript 等价物、macOS/iOS/Web 适配、导出模板、社区生态

其中 **第 1、3 层的一部分可以靠文本/数据/规格闭环，第 2 层必须真机**。

## 五个 Gate

| Gate | 名称 | 出口条件 | 本轮进展 |
|---|---|---|---|
| **G0** | 工程基线 | `dotnet build -warnaserror` 0 错 0 警 + 1559/1559 + smoke 60 + 静态审计 | ✅ 补 `capability_audit.py` / `assembly_boundary_check.py` / 81 项矩阵；S0/S1 原有 CI |
| **G1** | 编辑器闭环 | S3–S6 全过（项目/场景往返、热编辑、渲染基线、崩溃排查） | 补 `EDITOR_WIRING.md` + `debug-protocol.schema.json` + `E2E_ACCEPTANCE.md`；**真机未跑** |
| **G2** | 脚本与调试 | S7–S10 全过（脚本、热重载、断点、远程检查器） | 补 `SCRIPTING_ABI_SPEC.md` + `BUILD_DECISIONS.md` D1 双轨决策；**真机未跑** |
| **G3** | 资源与导出 | S11–S12 全过（资产管线、6 平台出包） | 补 4 个 schema + 2 个模板 + 6 份导出契约 + `PLATFORM_MATRIX.md`；**包未产出** |
| **G4** | 生态与宣称 | E0–E5（许可/签名/Release/外部验证/资产库/宣称收敛） | 补 `ECOLOGY_ROADMAP.md`；**无外部验证** |

## 本轮实际交付（分支 `feat/godot3-capability-closure`）

- **81 项原子能力矩阵**（`GODOT3_CAPABILITY_MATRIX.md` + CSV），71 项落到远端真实文件
- **12 条端到端验收**（`E2E_ACCEPTANCE.md`），每条有命令与期望输出
- **6 项 `Compile Remove` 架构决策**（`BUILD_DECISIONS.md`），含热重载命名空间证伪
- **编辑器 9 项接线核验表**（`EDITOR_WIRING.md`）
- **脚本 ABI / 平台矩阵 / 占位分类 / 生态路线 / 分阶段路线**（5 篇）
- **4 个 JSON Schema**（项目 / 场景 / TileSet / 调试协议）
- **2 个新模板**（Basic2D / NpcRealityDemo）+ **6 份导出契约**
- **2 个静态审计脚本** + docfx 规格 + CI 门禁模板

**零新增参与编译的 `.cs`** —— 这是刻意的：远端已有「0 错误 0 警告」基线，
沙盒无 SDK，再堆未编译代码只会把基线重新变成不确定状态。

## 物理边界（文本工作永远达不到的部分）

| 项 | 为什么 | 谁来做 |
|---|---|---|
| 真实 GPU 端到端 PBR 帧 | 需 Vulkan 1.4 显卡与驱动 | 你明天的新版本 / 真机 |
| `0xC0000005` 定位 | 需 WinDbg + minidump + 调用栈 | 真机 |
| GDScript 等价 VM | 需编译验证的解释器实现 | 有 SDK 的环境分阶段落地 |
| macOS / iOS / Web 支持 | 需对应 SDK、GLES2 后端、商店签名 | 多平台真机 |
| 社区 / 发行 / 主机授权 | 需第三方与人 | 时间与外部协作 |

## 明天怎么勾

按 `E2E_ACCEPTANCE.md` 的 **S0 → S12** 顺序跑；哪条红就修哪条。
每红一条，回到 `capability_matrix.csv` 找对应 `G3-xxx` ID，那里有文件、状态与验收口径。

> 这份路线不承诺「一次做完」，但承诺**每一条都能被判定**。
> 判定标准齐全，本身就是从「看起来全」走向「真的全」的必要一步。
