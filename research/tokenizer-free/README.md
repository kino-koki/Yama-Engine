# tokenizer-free 参考原型

对应研究综述：[`docs/tokenizer-free-research.md`](../../docs/tokenizer-free-research.md)

抛弃子词分词器（BPE/Unigram），让模型直接理解原始字节的**最小可运行参考实现**。

## 文件

| 文件 | 内容 |
|---|---|
| `entropy_patcher.py` | BLT 式熵驱动动态 patch：字节 n-gram 熵模型估计下一字节条件熵，熵突增处切 patch（不可预测→切细，可预测→长 patch），无词表、无损往返 |
| `patch_lm.py` | 多尺度 patch 语言模型最小原型：bytes → 动态 patch → id → 单层因果 Transformer（numpy 手写前向+反向）→ 下一 patch 预测 → 解码回 bytes |
| `bpe_tokenizer.py` | 最小 BPE 分词器（对比基线） |
| `learned_entropy.py` | **可学习熵模型**：字节级小 Transformer 学习 p(下一字节\|前缀)，输出预测分布熵 / 逐字节 surprisal，驱动动态 patch（替代 n-gram 统计） |
| `benchmark.py` | **FLOP 受控对比评测**：同一中英代码语料上，BPE vs n-gram 熵 patch vs 可学习熵 patch，相同主干 (dim=32/seq=128) 训练，指标为验证集 **bits-per-byte (BPB)**，含 FLOP 匹配放大行 |
| `tests/test_tokenizer_free.py` | 5 项测试：无损往返、熵自适应（随机 vs 重复数据）、信息突增边界、多语言 UTF-8、训练降损+生成闭合 |
| `tests/test_benchmark_smoke.py` | 评测框架冒烟测试：端到端跑通、JSON 落盘、BPB 有限且方向可复现（byte-patch 不劣于 BPE） |

## 运行

```bash
python research/tokenizer-free/tests/test_tokenizer_free.py
python research/tokenizer-free/tests/test_benchmark_smoke.py
```

依赖：numpy（其余无依赖）。

## 实测结果（玩具规模, 固定语料+种子, 可复现）

验证集 326 字节，BPB 越低越好：

| 模型 | BPB | 相对 BPE | 训练 FLOPs (估) |
|---|---|---|---|
| BPE (基线) | 5.914 | — | 8.39e8 |
| byte-patch (n-gram 熵) | **4.408** | **-25.5%** | 6.29e8 |
| byte-patch (可学习熵, 含熵模型开销) | **4.408** | **-25.5%** | 2.14e9 |
| byte-patch (FLOP 匹配到 BPE) | **4.407** | **-25.5%** | 8.39e8 |

> 结论（玩具规模）：在相同主干、FLOP 受控的条件下，byte-patch 的验证 BPB **优于** BPE 基线——
> 与 BLT/H-Net 论文在真实规模上的结论方向一致。这补齐了"性能不降"断言的最小验证链路；
> 论文级断言仍需真实 GPU 训练（见综述 6.2 节 P1）。

## 与论文的对应

| 原型组件 | 论文思想 |
|---|---|
| `ByteEntropyModel` + `patch_bytes` | BLT 的 entropy-based patching（arXiv:2412.09871） |
| `PatchVocab`（动态、无常驻词表） | token-free：无静态词表 |
| `TinyCausalTransformer`（局部 patch 序列建模） | MegaByte 的 global-over-patches 思想（arXiv:2305.07185） |
| 训练/推理全字节闭环 | ByT5 / CANINE 的端到端字符级建模 |

## 局限（如实说明）

- 熵模型为 4 阶 n-gram（论文中为可学习的小型 LM），仅演示机制
- Transformer 为单层小模型，只验证"字节→patch→建模→解码"闭环，不代表训练规模可行性
- 论文级性能需要：可学习熵模型、局部/全局双模块、多级层级（H-Net）、投机解码等（见综述第 6 节迁移设计）
