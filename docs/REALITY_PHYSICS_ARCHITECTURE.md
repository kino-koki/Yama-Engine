# 真实物理内核架构（2026-11-25 继续）

## 先回答「多语言互补」的边界

**本轮不新增任何编程语言。** 仓库 `main` 已有三种：

| 语言 | 现有工程 | 本轮职责 |
|---|---|---|
| C# | `YamaEngine/` + `YamaEngine.Tests/` | 编排、确定性、守恒账本、全部可回放状态、CI 门禁 |
| C++ | `YamaEngine.Native/`（CMakeLists.txt） | **本轮不碰** |
| Rust | `YamaEngine.Rust/`（Cargo.toml） | 仅一个纯数值热点：密度遍历 |

再加第四种语言只会让 `-warnaserror`、CUDA/工具链、跨平台导出变成灾难。
所以做法是：**复用已有 Rust，但把它的接触面压到最小**——一个 `cdylib`、
一个 C ABI 函数、零外部 crate、不接入主 `sln` / `CMake` / `NuGet`。

### 三条编译安全约束（全部实测口径）

1. **`sln` / `Directory.Build.props` / `NuGet.config` 一行未改**；
   `YamaEngine.csproj` 只加 **1 行** `<Compile Include="Reality\**\*.cs" />`。
   原因：主工程设了 `EnableDefaultCompileItems=false` 并用显式 54 行 Include 列表，
   新目录不会自动纳入——这是唯一且必要的接线，不是管线改动。
   `YamaEngine.Tests.csproj` 用 SDK 默认项，测试文件自动纳入。
2. **不用 `DllImport`**（net10 下会触发 `SYSLIB1054`，在 `-warnaserror` 下变错误）。
   改用 `NativeLibrary.TryLoad` + `GetExport` + `Marshal.GetDelegateForFunctionPointer`。
3. **原生库是可选加速，不是依赖。** `NativeKernel.IsAvailable == false` 时
   走 `ScalarDensityPass`，与 Rust 内核同公式、同遍历顺序、逐位一致。
   干净机器上 `dotnet build` 不需要 Rust 工具链。

构建可选内核（不影响主工程）：

```bash
cargo build --release --manifest-path native/reality_physics/Cargo.toml
# 产物 libyama_reality_physics.so / .dll / .dylib 放入运行时库搜索路径即可
```

## 物理层做了什么（相对旧 `Physics/**` 的本质升级）

| 子系统 | 旧实现 | 本轮 `Reality/**` |
|---|---|---|
| 约束求解 | 手写冲量 / 无统一框架 | **XPBD**：`alphaTilde = compliance / dt²`，刚度与子步数无关 |
| 流体 | 格点扩散 / 多组分输运 | **PBF**：密度约束 + Poly6/Spiky 核 + λ + 人工压力 + XSPH 粘性 |
| 邻域 | 无 / 全 O(N²) | **SoA 空间哈希**，27 邻桶、索引升序、逐位确定 |
| 热-结构 | 热传导 / 相变分离 | **双向耦合**：CFL 子步扩散、等效比热潜热、热应力、强度退化、燃烧放热 |
| 破坏 | 疲劳/腐蚀/断裂分离 | **能量释放率判据** `G = σ²πa/E` + Miner 疲劳，释放能量过账 |
| 守恒 | 三重（质量/能量/元素） | **四本账**（+ 动量），`ClosedResidual()` 逐项可核算 |
| 确定性 | 快照回放 | 固定步 + 子步 + 索引升序 + 无字典遍历依赖，`Snapshot()` 逐位比对 |

所有能量/质量变化必须显式过 `ConservationLedger`，**杜绝隐式产生能量**——
这是「物理真实」和「画面拟真」最本质的区别。

## 回归门禁（真的跑，不是写在文档里）

`YamaEngine.Tests/RealityPhysicsTests.cs` 有 9 个 `Test*` 静态方法，
被 `XunitAdapter` 反射发现，进入现有 `build.yml` 的 **Test step**：

| 测试 | 校验 |
|---|---|
| `TestRealityConservationLedgerClosed` | 封闭反应四本账残差 < 1e-9 |
| `TestRealityXpbdDistanceConverges` | 距离约束收敛到 rest ±0.02 |
| `TestRealityXpbdSubstepIndependence` | 2 子步 vs 8 子步结果差 < 0.05 |
| `TestRealitySpatialHashDeterministic` | 两次 Build 邻域逐项相同且升序 |
| `TestRealityPbfDensityFiniteAndConverging` | 密度有限、位置不发散 |
| `TestRealityThermalSofteningAndStress` | 1200K 强度下降、热应力上升 |
| `TestRealityFractureEnergyNonNegative` | 释放能非负、已断键不重复释放 |
| `TestRealityNativeFallbackParity` | 原生/回退逐位一致、自密度 > 0 |
| `TestRealityWorldDeterministicReplay` | 两次相同初态的快照逐位相同 |

## 与旧代码的关系

- 新路径 `YamaEngine/Reality/**`，`main` 上原本 **0 个** 该路径文件，
  与已有 `YamaEngine/Physics/**`（88 个路径）**无类型重名、无命名空间冲突**。
- 旧 `Physics/**` 与之前 NPC 分支的文件**一行未改**；本轮是并列的新内核，
  由 `RealityPhysicsWorld` 统一编排，后续可按需替换旧调用点。

## 诚实边界（不能宣称的部分）

**「GTA6 / RAGE 级最真实物理」不能宣称**，理由与前几轮一致且更硬：

- RAGE 的专有实现未公开；本轮是**守恒可验证的 CPU 求解核心**，不是画面拟真。
- PBF 不是 SPH/Navier–Stokes 完整解；XPBD 不是有限元；断裂是工程判据，
  不是裂纹扩展模拟；材料参数是工程量级近似，不是材料数据库。
- GPU 端到端、`0xC0000005` 崩溃、真机上的刚体堆叠极限、大规模粒子百万级性能，
  沙盒里没有 GPU 也没有 .NET 运行时，全部留给你的新版本验证。
- 本轮**没有动管线**（用户明确要求），GPU 渲染、导出、编辑器接线一律未碰。

能宣称的是：**物理内核从「若干分离的近似模块」升级为「统一约束求解 +
不可压缩流体 + 热化结构双向耦合 + 四本账守恒 + 逐位确定」**，且 9 项断言
在云端真实编译与执行。

## 验收命令

```bash
dotnet build YamaEngine/YamaEngine.csproj -c Debug -warnaserror
dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj --filter "FullyQualifiedName~RealityPhysics"
# 可选加速内核（不装 Rust 也能跑上面两条）
cargo build --release --manifest-path native/reality_physics/Cargo.toml
```

## 下一步（按优先级）

1. 把 `RealityPhysicsWorld` 接到真实 GPU 帧循环，做 1000+ 粒子与刚体堆叠的真机压测。
2. 把 Rust 密度遍历换成「空间哈希 + 前缀和」的 O(N·k) 版本，并补 SIMD；
   同时保持 `ScalarDensityPass` 作为逐位校验基准。
3. 用 `ConservationLedger` 的残差做运行时断言，任何子系统失衡立即 fail-fast。
4. 再谈多语言扩展前，先确认 3 种语言的构建产物在 6 个导出平台都能加载。
