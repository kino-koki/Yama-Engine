# 第九轮交付：音频看板清零 + gapless 真因

**焦点**：上轮结尾挂着的唯一已知差距 `AUDIO-MP3-001`（矩阵 G3-068
`部分·仅 MP3 未达标`）。本轮不再堆物理，专攻音频并把看板清到 **0 条**。

## 做了什么

1. **真修 `Mp3Decoder.DecodeAll`**：`enc_delay` 不再被重复扣减
   （Xing 元数据帧已排除其 576 个 PCM，裁剪阶段不再把它当起点偏移）。
   窗口从 `full[1152..9152)` 改为 `full[576..8576)`。
2. **真修测试工具**：`CompareWithReferenceAligned` 改全程逐样本精搜。
   参考波形是严格 400 样本周期信号，旧步长16 搜索必然停在伪峰。
3. **看板清零**：`KnownLimitations` 3 条 → 0 条，
   `TestMp3SineDecode` 回归真实门禁；`SelfCheckTests` 下界 1→0 并显式
   禁止三个已修用例再被隔离。
4. **矩阵 G3-068** → `已实现`。
5. **新增** `TestMp3GaplessTrimContract`、`docs/AUDIO_MP3_GAPLESS_FIX.md`，
   重写 `docs/AUDIO_DECODER_GAPS.md`（含明确撤回的旧结论清单）。

## 最终数值

```
bestOff=-1905  corr=0.999897  maxAbsDiff=0.023485  err>0.05 样本数=0
阈值：corr >= 0.97、maxAbsDiff <= 5e-2（一律未放宽）
```

## 明确撤回的四条旧结论

- 「≈0.46 样本亚样本相位」
- 「末帧 bit reservoir 局部畸变」（17 帧全部解码成功）
- 「解码器固有延迟需额外前移约 1329 样本」
- 「固定 6000 窗口 md=0.02348 可作验收」

## 未做 / 不宣称

- 不动渲染管线、不新增语言、不改 sln/CMake/NuGet/csproj。
- 物理侧本轮零改动，前八轮 107 项断言保持。
- 仍不宣称 GTA6/RAGE 级、Godot 3 全能力、任意 MP3 位级一致。
- GPU 帧 / `0xC0000005` / 百万粒子真机压测 / Rust 原生内核编译仍待新版本。

## 下一步（真机侧）

1. 扩 MP3 夹具覆盖：立体声、MPEG-1 44.1kHz、短块/混合块、ID3v2、CRC 帧。
2. 物理侧：逐点 Id 扩成 `(面Id, 点Id)`、动态树接 GPU 帧循环压测、
   真正的 conservative advancement、Rust 原生内核编译加载。
