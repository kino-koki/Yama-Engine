# 抛弃分词器：Tokenizer-Free 语言模型研究综述与迁移设计

> 研究时间：2026-11-17 · 依据 arXiv 公开论文（BLT / H-Net / MambaByte / MegaByte / ByT5 / CANINE）
> 目标：**去掉子词分词器（BPE/Unigram），让模型直接理解原始字节**，同时性能不降、语言能力反升。

---

## 1. 分词器为什么是"最丑陋"的设计

子词分词器（BPE/WordPiece/Unigram）是当前 LLM 的标配，但它带来一整类结构性问题：

| 问题 | 说明 |
|---|---|
| **端到端断裂** | 模型被人为切成"分词器 → Transformer → 反分词器"三段，前段不可微、不可学习，是真正的端到端基础模型的最大障碍（H-Net 论文的核心动机） |
| **多语言不公** | BPE 词表按训练语料频率切分，低资源语言被切得更碎（同内容 token 数可达英文的 2-4 倍），推理更贵、效果更差 |
| **组合泛化受损** | 数字、代码、化学式、罕见词被任意切分，模型难以学到字符级规律（如计数、反转、押韵） |
| **鲁棒性脆弱** | 一个字符的噪声（错字/对抗后缀/表情符号）可能导致多个 token 变化，模型对"看不见的切分"脆弱 |
| **工程复杂度** | 词表 20 万+、多语言分词不一致、special token 冲突、词汇表版本管理等维护成本高 |
| **算力分配失当** | token 边界由频率决定而非信息量决定：可预测文本被切得过细，困难文本反而没有更多算力 |

**结论**：分词器是一个"用启发式预处理替代模型能力"的历史包袱。去掉它的关键在于解决"字节序列太长"与"算力如何分配"两个问题。

---

## 2. 技术路线地图

```
字节序列 (UTF-8)
   │
   ├─ 路线 A: 固定多尺度 patch ────── MegaByte (2023)
   ├─ 路线 B: 熵驱动动态 patch ────── Byte Latent Transformer (2024-12) ★当前最优
   ├─ 路线 C: 线性态空间直接吃字节 ── MambaByte (2024-01)
   ├─ 路线 D: 端到端可学习分块 ────── H-Net / Dynamic Chunking (2025-07) ★语言能力反升
   └─ 路线 E: 编码器侧字符化 ──────── CANINE (2021) / ByT5 (2021)（历史奠基）
```

---

## 3. 论文精读

### 3.1 MegaByte（Meta, arXiv:2305.07185, 2023）

- **机制**：把字节序列切成固定大小 patch（如 8 字节）。局部小 Transformer 在 patch 内编码字节，全局大 Transformer 在 patch 序列上做自回归；解码时 patch 内并行预测多个字节。
- **结果**：首次证明百万字节级序列可端到端建模；长上下文语言建模上与子词模型有竞争力；ImageNet 密度估计 SOTA；可直接建模原始音频。
- **局限**：固定 patch 大小与内容无关——不可预测处算力不足、可预测处浪费。

### 3.2 Byte Latent Transformer / BLT（Meta, arXiv:2412.09871, 2024-12）★ 当前工程最强

- **机制**：**熵驱动的动态 patch**。用一个小的熵模型（byte-level LM）预测每个字节的下一字节熵，在熵突增处切分 patch——"信息量大的地方切细，可预测的地方切粗"。主体为 Encoder（局部字节省略模块）+ Latent Global Transformer（在 patch 上）+ Decoder（把 patch 展开回字节），三模块均基于局部/全局 Transformer 块。
- **结果**：
  - 首个 **FLOP 受控** 的字节级 scaling study：直到 **8B 参数 / 4T 训练字节**，与同 FLOP 的分词 LLM（Llama 2/3 风格）**性能持平**；
  - 推理与训练效率同时提升（可预测文本自动使用长 patch）；
  - **鲁棒性与长尾泛化显著更好**（字符级噪声、罕见词、翻译质量）；
  - 固定推理成本下，同时增大 patch 尺寸与模型尺寸的 scaling 优于分词模型。
- **局限**：熵模型引入额外一步（可用小模型/缓存缓解）；工程实现比分词器复杂。

### 3.3 MambaByte（arXiv:2401.13660, 2024-01）

- **机制**：用 Mamba 状态空间模型（SSM）直接自回归建模字节序列——固定大小记忆状态、线性复杂度，绕开"字节序列太长导致注意力显存爆炸"的问题。
- **结果**：在语言建模任务上与最强子词 Transformer **持平甚至更优**；对噪声鲁棒；配合"子词草稿 + 字节级验证"的投机解码获得 **2.6× 推理加速**，解码效率接近子词 Mamba。
- **局限**：SSM 生态与工业链路不如 Transformer 成熟。

### 3.4 H-Net / Dynamic Chunking（arXiv:2507.07955, 2025-07）★ 语言能力反升的最强证据

- **机制**：把"分词器 → LM → 反分词器"整体替换为**单一端到端层级网络**。核心是 **Dynamic Chunking**：
  - **Routing 模块**（余弦相似度 soft routing）为每个字节产生指向相邻字节的路由权重，把相似字节聚合成 chunk 表示；
  - **Dechunking 模块** 把 chunk 表示按路由权重"摊回"每个字节；
  - 用 **EMA（指数滑动平均）参数**替代可学习投影以稳定早期训练，配合 **STE（直通估计器）** 处理离散边界，解决端到端分块的训练不稳定；
  - 多级堆叠（bytes → chunks → super-chunks）形成显式层级，替代"分词-LM-反分词"的隐式层级。
- **结果**：
  - **算力与数据对齐**下，单级 H-Net（字节级）**超过强 BPE Transformer LM**；
  - 多级 H-Net 进一步提升，**以 2 倍小模型匹敌 BPE 模型**，数据 scaling 显著更陡；
  - 英文预训练的 H-Net **字符级鲁棒性大幅提升**；无任何启发式/监督即学到有意义的分块（词/子词边界）；
  - **中文与代码等"分词启发式更弱"的域上优势进一步扩大**——这正是"语言能力反升"的直接证据。
- **局限**：训练工程要求高（EMA/STE/多阶段 warmup）；层级数增加时显存与调度更复杂。

### 3.5 ByT5（arXiv:2105.13626, 2021）

- **机制**：T5 的字节级版本——直接在 UTF-8 字节上预训练 encoder-decoder，无词表。
- **结果**：在对抗噪声、拼写纠错、文字游戏（如反转、谐音）等"字符敏感"任务上**显著优于 SentencePiece 版本**；多语言公平性更好。
- **局限**：预训练/推理成本高于子词版本（当时没有 patch 机制），需更大批量与更长训练。

### 3.6 CANINE（arXiv:2103.06874, 2021）

- **机制**：encoder 侧直接吃 Unicode 字符，用**深堆叠窄Transformer + 局部注意力 + 下采样（字符池化）**控制序列长度；提出 **encoder 内子词 softmax 损失**（在不引入推理分词器的前提下用子词信号辅助预训练）。
- **结果**：多语言 NER/分类上与 mBERT 相当或更优，且对噪声更稳。
- **局限**：仅 encoder 侧；生成场景未覆盖。

---

## 4. 横向对比

| 维度 | BPE LLM | MegaByte | BLT | MambaByte | H-Net |
|---|---|---|---|---|---|
| patch 方式 | 词表切分 | 固定大小 | **熵驱动动态** | 无 patch（逐字节） | **可学习动态分块** |
| 序列建模主干 | Transformer | 局部+全局 Transformer | 局部+全局 Transformer | Mamba SSM | 层级 Transformer |
| 与分词模型性能 | 基线 | 长上下文持平 | **8B/4T FLOP 持平** | 持平或更优 | **数据匹配即超越；2×小模型匹敌** |
| 语言能力/鲁棒性 | 基线 | — | 长尾/噪声/翻译更好 | 噪声鲁棒 | **字符鲁棒性大增，中文/代码优势扩大** |
| 推理效率 | 基线 | patch 内并行 | 动态长 patch 省算力 | 投机解码 2.6× | 取决于分块率 |
| 工程复杂度 | 低 | 中 | 中高 | 中（SSM 生态） | 高（EMA/STE） |

**结论**：技术拐点已到——BLT 证明了"规模上不输"，H-Net 证明了"可学习分块能反超"，MambaByte 证明了"线性模型路线可行"。

---

## 5. "性能不降 + 语言能力反升"的机理

1. **算力跟着信息量走**：熵 patch 让 90% 的可预测文本消耗极少算力，把预算集中到实体名/数字/代码/罕见词上——同等 FLOP 下有效学习信号更多（BLT）。
2. **消除切分任意性**：字节级建模使"数字/代码/拼写变体"不再被随机切断，字符级规律可被模型直接学习（ByT5 的字符敏感任务优势、H-Net 的鲁棒性）。
3. **多语言公平**：所有语言共享同一字节空间，中文/泰文/代码不再被"切得更碎更贵"——H-Net 明确报告中文与代码上优势扩大。
4. **层级抽象内生化**：词/子词边界由模型自己发现（H-Net 可视化了学到的分块），而不是被词表硬编码——这是"语言能力反升"的根本来源。
5. **长尾泛化**：无 OOV；任何新词都是字节序列，天然可组合（BLT 长尾基准更好）。

---

## 6. 迁移设计（如何落地"抛弃分词器"）

### 6.1 目标架构（推荐：BLT 骨架 + H-Net 思想）

```
UTF-8 bytes
   │
   ▼
[熵模型 small-LM] ──每字节下一字节熵──► 边界决策 (熵峰值 / 最大 patch 长度)
   │
   ▼
动态 patch (平均 6~10 字节/patch, 难点自动切细)
   │
   ▼
[Local Encoder]  字节→patch 表示 (局部注意力, 省算力)
   │
   ▼
[Global Latent Transformer]  patch 序列自回归 (主干算力)
   │
   ▼
[Local Decoder]  patch→字节 (逐 patch 并行解码, 可与投机解码结合)
   │
   ▼
bytes out
```

### 6.2 分阶段迁移路线（性能不降的保障）

| 阶段 | 动作 | 验收标准 |
|---|---|---|
| P0 影子运行 | 熵 patcher 与现有 BPE 并行跑，只记录不生效 | patch 长度分布、边界与词/子词边界重合率报告 |
| P1 混合预训练 | 以 BPE 模型为初始化，将 embedding 层替换为 byte-patch encoder，继续预训练 | 同 FLOP 下 validation loss 不劣于 BPE 基线 |
| P2 字节级对齐 | 在代码/中文/低资源语料上加大配比，熵 patch 动态生效 | 多语言 token 效率提升 ≥30%，质量不降 |
| P3 全面切换 | 推理路径切换为 byte-patch，配合投机解码（子词草稿+字节验证） | 吞吐 ≥ BPE 基线；鲁棒性/长尾基准反超 |
| P4 端到端分块 | 引入可学习 dynamic chunking（H-Net 式 routing+EMA+STE） | 字符鲁棒性与中文/代码基准反超 |

### 6.3 评估矩阵

- **性能不降**：FLOP-controlled scaling（同训练算力对比 val loss）；下游 MMLU/GSM8K/HumanEval 对齐比较。
- **语言能力上升**：多语言 token 效率（每语言每概念字节数/成本）、字符级抗噪基准、长尾词频率基准、中文/代码专项。
- **工程指标**：吞吐（tokens/s→bytes/s）、显存、patch 长度分布、熵模型开销占比（应 <5%）。

### 6.4 风险与开放问题

- 熵模型本身需要训练与推理开销（BLT 用小模型+缓存，开销可控但非零）。
- patch 边界不可微：H-Net 的 EMA+STE 是目前最稳的端到端方案，但多级堆叠的训练稳定性仍需调参。
- 生态适配：现有 tokenizer 驱动的工具链（采样控制、logit 处理、评测脚本）需要按"字节/patch"重写。
- 超长上下文下全局注意力仍是瓶颈，可结合 MambaByte 思路做混合主干。

---

## 7. 参考

1. Pagnoni et al. **Byte Latent Transformer: Patches Scale Better Than Tokens**. arXiv:2412.09871, 2024.
2. Huang et al. **Dynamic Chunking for End-to-End Hierarchical Sequence Modeling (H-Net)**. arXiv:2507.07955, 2025.
3. Wang & Komatsuzaki. **MambaByte: Token-free Selective State Space Model**. arXiv:2401.13660, 2024.
4. Yu et al. **MEGABYTE: Predicting Million-byte Sequences with Multiscale Transformers**. arXiv:2305.07185, 2023.
5. Xue et al. **ByT5: Towards a token-free future with pre-trained byte-to-byte models**. arXiv:2105.13626, 2021.
6. Clark et al. **CANINE: Pre-training an Efficient Tokenization-Free Encoder for Language Representation**. arXiv:2103.06874, 2021.

> 可运行参考实现见 [`research/tokenizer-free/`](../research/tokenizer-free/README.md)（熵驱动动态 patcher + 多尺度 patch LM 最小原型 + 测试）。
