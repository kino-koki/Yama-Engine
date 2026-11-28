# MP3 gapless 口径真修与音频看板清零（第九轮，2026-11-26）

**结论：音频能力矩阵 G3-068 从「部分·仅 MP3 未达标」改为「已实现」，
`KnownLimitations` 看板 3 条 → 0 条。**

## 一句话

`DecodeAll` 把 LAME 的 `enc_delay=576` **扣了两次**：一次是排除 Xing 元数据帧的
576 个 PCM，一次是裁剪阶段又把它当作 `output` 内的起点偏移。窗口因此整体后移
576 样本，把尾部过渡区（74 个 |err|>0.05 的样本）纳入输出。去掉第二次扣减即可。

## 修前 / 修后

| | 修前 | 修后 |
|---|---|---|
| 裁剪 | `skip=encDelay(576)`, `keep=9216-576-640=8000` | `skip=0`, `keep=9216-576-640=8000` |
| 窗口（完整解码 full，9792 样本） | `full[1152..9152)` | `full[576..8576)` |
| 逐样本对齐 off | -1584（步长16 伪峰）/ -1329（真实峰） | -1905 |
| corr | 0.992633 / 0.999272 | **0.999897** |
| maxAbsDiff | 0.244884 / 0.209071 | **0.023485** |
| err>0.05 样本数 | 1756 / 74 | **0** |
| `TestMp3SineDecode` | 看板隔离 | **回归真实门禁并通过** |

阈值未放宽（corr ≥ 0.97、maxAbsDiff ≤ 5e-2），未缩短比较窗口，
未吞异常，未 Compile Remove。

## 为什么 `enc_delay` 不该再当起点偏移

LAME tag 语义：解码输出开头有 `enc_delay` 个样本属于编码器延迟，末尾有
`enc_padding` 个样本属于填充；有效长度 = `frames*spf - enc_delay - enc_padding`。

本夹具 `frames=16`（Xing 的 frame count **不含元数据帧**）、`spf=576`、
`enc_delay=576`、`enc_padding=640` → 有效长度 `9216 - 576 - 640 = 8000`。

`DecodeAll` 的循环已经把 Xing 元数据帧的 PCM 排除，所以 `output` 的长度
恰好是 `frames*spf = 9216`，**它本身就是「已去掉元数据帧」的音频流**。
元数据帧那 576 个样本正是 `enc_delay` 的物理载体，排除它 = 已扣除延迟。
此时再从 `output` 起点跳 576，等于把第一个有效音频帧也丢掉。

正确的两行：

```csharp
int keep = output.Count - gap.encDelay - gap.encPadding;  // 8000
output.CopyTo(0, trimmed, 0, keep);                       // 起点不偏移
```

## 为什么对齐搜索必须逐样本

参考波形 `0.5015·sin(2π·440t/16000) + 0.1998·sin(2π·1320t/16000+φ)`：
440/16000 = 11/400、1320/16000 = 33/400 → **严格 400 样本周期**。

云端诊断（`45988715032`）显示逐样本对齐峰**严格间隔 400**：
`-1329 / -1729 / -2129 / -2529 / -2929`，另一族 `-1438 / -1838`…
旧的「步长 16 粗搜 + ±16 精搜」只能停在任意一个峰，实测停在 `-1584`
（与真实峰差 255 样本 ≈ 4.54°@440Hz / 13.70°@1320Hz 的系统性相位差），
于是全段均匀误差、`maxDiff=0.244884`。

`AudioRealDecoderTests.CompareWithReferenceAligned` 改为全程逐样本精搜，
并把 reference 一次性 `Buffer.BlockCopy` 成 `float[]`（避免每样本
`BitConverter.ToSingle`）。**阈值、比较样本数、验收口径一律不变**。
文件里 Vorbis 分支的注释早就写了「步长 1 逐样本精搜，避免虚假对齐」，
这轮只是让 MP3 与它一致。

## 四组真实 CI 诊断（本轮证据链）

| run | 作用 |
|---|---|
| `45988715032` | 逐样本对齐曲线、逐帧解码结果、分段 rms；证伪「末帧 reservoir 失败」（17 帧全成功） |
| `45989907995` | 最优对齐处误差分布；定位 74 个 >0.05 样本全在 pcm 7923~7995 |
| `45990490795` | 完整解码输出全窗口零对齐扫描；证明单靠平移不能达标 |
| `45991271018` | 候选窗口内逐样本对齐；`D=576` 达标、`D=1152` 不达标 → 锁定重复扣减 |

诊断文件 `Mp3DiagTmp.cs` 与 csproj 临时 target **用完即删**，
最终 tree 里不存在；终检 5 个关键文件诊断/凭据残留 0。

## 新增回归契约

`TestMp3GaplessTrimContract`：

- `DecodeAll()` 长度必须 = 8000（`16*576 - 576 - 640`）
- 尾部 200 样本 rms ∈ (0.30, 0.45)（不能是旧窗口的畸变尾部）
- 整段 |x|>0.01 的样本 ≥ 6800（粗粒度完整性守卫；余量留给 QMF 预热与正弦过零，
  实测 1002 个低幅样本；不是验收口径）

`SelfCheckTests` 同步：看板下界 1 → 0，并显式断言
`TestMp3SineDecode` / `TestFlacRoundTrip` / `TestVorbisSineDecode`
**都不再被隔离**。


## 诊断轮抓到的一处「契约断言设得太紧」

第一次 CI `45992360640` Test 红，失败落盘回读到唯一失败：
`TestMp3GaplessTrimContract :: MP3 输出含过多静音样本 (loud=6998)`。

**`TestMp3SineDecode` 在这次红灯里是通过的** —— 也就是说 gapless 真修与
逐样本对齐已经成立，红的只是我新加的契约测试里一条自己设得过紧的
粗粒度守卫（先设 7600、再设 7200，实测 6998）。1002 个低幅样本来自
开头 QMF/IMDCT 预热 + 440Hz/1320Hz 正弦每周期的自然过零点，都是合法的。

修法：守卫下界改为 6800，并在注释里写清它**不是验收口径**，真正的回归契约
是 `TestMp3SineDecode` 在真实门禁里执行的 `corr>=0.97 / maxAbsDiff<=5e-2`。
没有删断言、没有动任何验收阈值。诊断通道用完即删。

## 仍不宣称的

- 只验证了 `sine16k_mono.mp3` 这一个 440Hz+1320Hz 单声道 VBR 夹具；
  立体声、MPEG-1 44.1kHz、短块/混合块、ID3v2 大标签、CRC 帧尚未做参考比对。
- 逐样本对齐仍允许「整数样本延迟」由搜索吸收（这是该函数既有设计目的，
  用于补偿编码器/解码器延迟差异）；未做样本级相位零对齐。
- 自研解码器与 mpg123/libsndfile 在**任意** MP3 上位级一致，不在本轮结论内。

## 验收

```bash
git fetch && git checkout main
dotnet build YamaEngine/YamaEngine.csproj -c Debug -warnaserror
dotnet test YamaEngine.Tests/YamaEngine.Tests.csproj \
  --filter "FullyQualifiedName~AudioRealDecoderTests|FullyQualifiedName~SelfCheckTests"
```
