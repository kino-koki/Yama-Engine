# learned_entropy.py — 可学习熵模型 (BLT 式熵模型的玩具级实现)
#
# 与 entropy_patcher.ByteEntropyModel (n-gram 统计) 不同:
#   这里用一个小型字节级因果 Transformer 学习 p(下一字节 | 前缀)，
#   熵 = 预测分布的香农熵，surprisal = -log2 p(真实下一字节)。
# 训练完成后即可对任意字节流给出逐字节熵/意外度，驱动动态 patch。

import numpy as np

from patch_lm import TinyCausalTransformer


class LearnedEntropyModel:
    """可学习熵模型: 字节级小 Transformer (vocab=256)。"""

    def __init__(self, dim: int = 32, seq_len: int = 128, steps: int = 60, lr: float = 0.05, seed: int = 0):
        self.dim = dim
        self.seq_len = seq_len
        self.steps = steps
        self.lr = lr
        self.seed = seed
        self.model = None
        self.train_loss = None

    def train(self, data: bytes) -> None:
        ids = list(data)
        if len(ids) < 3:
            raise ValueError("training data too short")
        self.model = TinyCausalTransformer(256, dim=self.dim, seq_len=self.seq_len, seed=self.seed)
        windows = []
        for i in range(0, len(ids) - 1, self.seq_len):
            w = ids[i:i + self.seq_len + 1]
            if len(w) >= 2:
                windows.append(w)
        losses = []
        for _ in range(self.steps):
            for w in windows:
                losses.append(self.model.train_step(w, lr=self.lr))
        self.train_loss = losses[-1] if losses else None

    def _probs(self, context: bytes) -> np.ndarray:
        logits, _ = self.model.forward(list(context[-self.seq_len:]))
        logits = logits[-1] - logits[-1].max()
        e = np.exp(logits)
        return e / e.sum()

    def next_byte_entropy(self, context: bytes) -> float:
        """预测分布的香农熵 (bit)。上下文不足/模型未训练时返回 8 (最大不确定)。"""
        if self.model is None or len(context) == 0:
            return 8.0
        p = self._probs(context)
        return float(-np.sum(p * np.log2(p + 1e-12)))

    def surprisal(self, data: bytes) -> list:
        """逐字节 surprisal: -log2 p(data[i] | data[:i])。首字节记 0。"""
        out = [0.0]
        for i in range(1, len(data)):
            p = self._probs(data[max(0, i - self.seq_len):i])
            out.append(float(-np.log2(p[data[i]] + 1e-12)))
        return out
