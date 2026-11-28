# 真实物理内核 · 第八轮：增量动态树与逐点持久 warm start（2026-11-25 继续）

延续口径：**不新增语言、不动渲染管线、不宣称 GTA6 级**。
这轮完成第七轮「下一步」的第 1 条与第 3 条的 warm start 部分。

## 1. 增量动态 AABB 树（替代每帧重建）

第七轮 `YrSweptBroadphase` 每帧从零重建整棵 BVH。新增 `YrDynamicAabbTree`
是 Box2D `b2DynamicTree` 风格的**真增量**结构：

- 每个刚体有稳定 leaf id；`Update(body, box)` 在 fat AABB 仍包住新盒时
  **零操作**，静止/微动物体完全不触动树；只有超出才 Remove+Insert。
- **最佳兄弟插入**：沿「合并面积增量最小」的分支下探，代价比较用严格小于 +
  索引 tie-break，保证逐位确定。
- **AVL 式单/双旋**：插入/删除后 `FixUpwards` 重算 AABB/Height 并 `Balance`，
  200 个顺序插入的盒树高 ≤ 24。
- **自由链表**复用节点，不产生每帧 GC 压力。
- `QueryPairs` 自比较时跳过对称的 `(R,L)` 分支，输出按 `(A,B)` 字典序去重。

**parity 是硬证明**：`TestR8DynamicTreeMatchesStaticBvhParity` 对同一 48 体布局，
断言动态树与静态 BVH 的候选对数组**逐项完全相同**（两者用同一 Fat 余量）。
另有确定性、高度有界、fat 内复用 leaf、删后重插复原、与 SAP 管线逐位一致共 7 项。

> 边界：叶盒由调用方提供（管线里 `SweptBox` 已含 fat 余量），因此候选对是
> **宽松超集**（多报不漏报）；动态树本身不额外膨胀，才能与静态 BVH 做逐项
> parity 校验。这是每帧局部更新的 CPU 结构，接 GPU 帧循环做 1000+ 刚体压测
> 仍留给真机。

## 2. 逐点持久 warm start

第五轮起就挂着的局限：「warm 缓存按 `(A,B)` 对取 `max(λ_n)`，多点流形下只有
首点吃到缓存」。这一轮真正解决：

- `YrFaceClipping.BuildTagged` 给每个接触点返回**稳定 Id**：
  初始为 incident 面顶点序号 `0..3`，Sutherland-Hodgman 交点在 `Lerp` 里
  **继承起点 Id**，因此同一接触特征跨帧保持同一标识。
  原 `Build` 改为兼容包装，第七轮语义逐点不变。
- `YrNarrowPhase.UsePointWarmCache`（默认 false）开启后，按
  `(A,B,pointId)` 存取 `λ_n`，每个接触点都吃到自己的缓存；
  `ExportWarmPoints/ImportWarmPoints` 按 `(A,B,Id)` 字典序序列化。
- `YrPhysicsPipeline.Snapshot` 末尾**追加**逐点段；`Restore` 检测
  `p == state.Length` 时按「长度 0」兼容第七轮及以前的旧快照。
  `TestR8LegacySnapshotStillRestores` 手工截掉新段构造旧格式，断言仍能恢复。

**零回归设计**：`UsePointWarmCache` / `UseDynamicTree` **全部默认 false**。
`TestR8DefaultSwitchesKeepLegacyBehavior` 断言默认路径不写逐点缓存、不建树、
快照逐点段长度为 0；`TestR8DynamicTreePipelineMatchesSap` 断言开启动态树后
与 SAP 路径**逐 tick 位级校验和相同**。

## 3. 快照格式演进（向前兼容）

```
第七轮 : 2 + 14n + 1 + 3m            (warm 按对)
第八轮 : 2 + 14n + 1 + 3m + 1 + 4k  (追加逐点段；缺省视为 k=0)
```

Restore 不要求新段存在，所以老快照、老回放缓冲、老测试全部照常工作。

## 4. 语言与构建边界（继续收窄）

- **不新增语言、不新增 Rust 导出**。动态树与逐点缓存全是控制流密集，
  纯数值内核无收益。`sln` / `CMake` / `NuGet` / `YamaEngine.csproj` 一行未改
  （`Reality\**\*.cs` 通配自动纳入）。
- Rust 内核在 CI **仍不编译**（主工程不依赖、沙盒无 cargo）。
- **音频 MP3 看板仍 1 条**（解码器固有延迟 + 末帧 bit reservoir），本轮不碰。


## 诊断轮抓到的一个「测试断言写错」

CI `45985656625` Test 红，失败落盘回读到唯一失败：
`TestR8DynamicTreeUpdateReusesLeafWhenFatContains :: 超出 fat 盒应触发 Remove+Insert`。

根因不是动态树实现，而是**断言要求了与设计相反的东西**：自由链表会复用刚
释放的节点索引，单节点树 `Remove` 后 `Insert` 拿到**同一个 leaf id** 恰恰是
预期行为（零 GC 压力的来源）。`moved != leaf` 永远不成立。

修法：保留"超出盒应触发重建"的语义，但改验真正该验的三件事——
`LeafOfBody(0) == moved`、`Root == moved`、`Nodes[moved].Box.Min.X == 50`
（叶盒确实换了）、`NodeCount == 1`（无残留）。**没有删断言、没有放宽容差**，
只是把"节点索引变了"这个错误代理换成"状态确实变了"。

## 回归门禁

八轮累计 **107 项**真实物理断言（9 + 14×7），
全部进入现有 `build.yml` 的 Test step 真实执行：无 `[Skip]`、无吞异常、无阈值放宽。

## 不能宣称的（不变且不缩小）

- **不宣称 GTA6 / RAGE 级最真实**：专有实现未公开。
- 动态树叶盒由调用方提供（管线含 fat 余量），是宽松候选超集、非精确窄相；逐点 Id 只在**单对参考/入射面**
  内稳定，多面同时接触时不同面可能复用同一 Id（下一步要扩成 `(面Id,点Id)`）；
  面裁剪仍是单对面近似；扫掠 TOI 是线性匀速外推 + 角速度保守上界，非精确 CA；
  序列冲量非全局 LCP 精确解；PBF 非 N–S 完整解；XPBD 非有限元；
  断裂为工程判据；Tait 为弱可压缩近似；化学反应为集总宏观模型；
  材料参数仍是工程量级近似。
- **GPU 端到端、`0xC0000005`、百万粒子/刚体堆叠真机压测、多平台导出、
  Rust 原生内核编译**都未在沙盒验证。
- **渲染管线一律未动**。

能确认的仍是：**守恒可核算、角动量严格守恒、逐位确定、云端真实编译并执行**。

## 下一步（真机侧）

1. 把逐点 Id 从 `0..3` 扩成 `(参考面Id, incident顶点Id)`，支持多面同时接触。
2. 动态树接 GPU 帧循环，实测 1000+ 刚体下 `Update` 复用率与树高。
3. 真正的 conservative advancement 替换角速度保守上界。
4. 编译并加载 Rust 原生库，跑三个 parity 测试的原生路径。
5. **专项攻 MP3 的 M4/M5**：这是唯一仍挂着的已知差距，下一轮单独收敛。
