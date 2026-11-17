# tokenizer-free 参考原型

对应研究综述：[`docs/tokenizer-free-research.md`](../../docs/tokenizer-free-research.md)

抛弃子词分词器（BPE/Unigram），让模型直接理解原始字节的**最小可运行参考实现**。

## 文件

| 文件 | 内容 |
|---|---|
| `entropy_patcher.py` | BLT 式熵驱动动态 patch：字节 n-gram 熵模型估计下一字节条件熵，熵突增处切 patch（不可预测→切细，可预测→长 patch），无词表、无损往返 |
| `patch_lm.py` | 多尺度 patch 语言模型最小原型：bytes → 动态 patch → id → 单层因果 Transformer（numpy 手写前向+反向）→ 下一 patch 预测 → 解码回 bytes |
| `tests/test_tokenizer_free.py` | 5 项测试：无损往返、熵自适应（随机 vs 重复数据）、信息突增边界、多语言 UTF-8、训练降损+生成闭合 |

## 运行

```bash
python research/tokenizer-free/tests/test_tokenizer_free.py
```

依赖：numpy（其余无依赖）。

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
