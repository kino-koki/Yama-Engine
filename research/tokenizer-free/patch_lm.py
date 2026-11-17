# patch_lm.py — 多尺度 patch 语言模型最小原型 (纯 numpy, 可运行)
#
# 思想来源: MegaByte (arXiv:2305.07185) / BLT (arXiv:2412.09871)
#   bytes --熵patch--> patch id 序列 --因果Transformer--> 下一 patch 预测 --解码--> bytes
#
# 这里实现一个单层因果自注意力 Transformer (含完整手写反向传播),
# 在微型中英代码混合语料上训练, 演示"无分词器的字节级建模"闭环。

import numpy as np

from entropy_patcher import ByteEntropyModel, patch_bytes, unpatch


class PatchVocab:
    """patch bytes -> id 映射 (训练语料内构建; 动态、无常驻词表)。"""

    def __init__(self):
        self.patch_to_id = {}
        self.id_to_patch = []

    def add(self, patch: bytes) -> int:
        idx = self.patch_to_id.get(patch)
        if idx is None:
            idx = len(self.id_to_patch)
            self.patch_to_id[patch] = idx
            self.id_to_patch.append(patch)
        return idx

    def encode(self, patches) -> list:
        return [self.add(p) for p in patches]

    def decode(self, ids) -> bytes:
        return b"".join(self.id_to_patch[i] for i in ids)

    def __len__(self):
        return len(self.id_to_patch)


def build_training_data(corpus: bytes, threshold: float = 3.5):
    """熵模型 + 动态 patch + 词表构建。返回 (vocab, id 序列)。"""
    model = ByteEntropyModel(order=4)
    model.train(corpus)
    patches = patch_bytes(corpus, model, threshold=threshold, min_patch=2, max_patch=24)
    vocab = PatchVocab()
    ids = vocab.encode(patches)
    return vocab, ids


class TinyCausalTransformer:
    """单层因果自注意力 Transformer (含手写反向传播), 预测下一个 patch。"""

    def __init__(self, vocab_size: int, dim: int = 24, seq_len: int = 96, seed: int = 0):
        rng = np.random.default_rng(seed)
        self.vocab_size = vocab_size
        self.dim = dim
        self.seq_len = seq_len
        self.E = rng.normal(0, 0.05, (vocab_size, dim))          # patch embedding
        self.P = rng.normal(0, 0.05, (seq_len, dim))             # 位置
        self.Wq = rng.normal(0, 0.2, (dim, dim))
        self.Wk = rng.normal(0, 0.2, (dim, dim))
        self.Wv = rng.normal(0, 0.2, (dim, dim))
        self.Wo = np.eye(dim) * 0.8
        self.W1 = rng.normal(0, 0.15, (dim, 2 * dim))
        self.b1 = np.zeros(2 * dim)
        self.W2 = rng.normal(0, 0.15, (2 * dim, dim))
        self.b2 = np.zeros(dim)
        self.Wout = rng.normal(0, 0.05, (dim, vocab_size))
        self.mask = np.triu(np.ones((seq_len, seq_len)), k=1).astype(bool)

    def forward(self, ids):
        """前向。返回 (logits, cache) 用于反向。"""
        L = len(ids)
        X = self.E[ids] + self.P[:L]
        Q = X @ self.Wq
        K = X @ self.Wk
        V = X @ self.Wv
        S = Q @ K.T / np.sqrt(self.dim) + np.where(self.mask[:L, :L], -1e9, 0.0)
        S = S - S.max(axis=-1, keepdims=True)
        A = np.exp(S)
        A /= A.sum(axis=-1, keepdims=True)
        H = A @ V
        H2 = X + H @ self.Wo
        F = np.tanh(H2 @ self.W1 + self.b1)
        H3 = H2 + F @ self.W2 + self.b2
        logits = H3 @ self.Wout
        return logits, (ids, X, Q, K, V, A, H, H2, F, H3)

    def loss(self, ids):
        """下一 patch 预测的交叉熵 (自回归)。"""
        if len(ids) < 2:
            return 0.0
        logits, _ = self.forward(ids[:-1])
        targets = ids[1:]
        logits = logits - logits.max(axis=-1, keepdims=True)
        exp = np.exp(logits)
        probs = exp / exp.sum(axis=-1, keepdims=True)
        n = len(targets)
        return float(-np.mean(np.log(probs[np.arange(n), targets] + 1e-9)))

    def train_step(self, ids, lr: float = 0.05) -> float:
        """对整段 id 序列做一步 SGD。返回 loss。"""
        if len(ids) < 2:
            return 0.0
        ctx = ids[:-1]
        targets = ids[1:]
        n = len(ctx)
        logits, cache = self.forward(ctx)
        ids_in, X, Q, K, V, A, H, H2, F, H3 = cache
        logits = logits - logits.max(axis=-1, keepdims=True)
        exp = np.exp(logits)
        probs = exp / exp.sum(axis=-1, keepdims=True)
        loss = float(-np.mean(np.log(probs[np.arange(n), targets] + 1e-9)))

        # ── 反向 ──
        dlogits = probs.copy()
        dlogits[np.arange(n), targets] -= 1.0
        dlogits /= n

        dWout = H3.T @ dlogits
        dH3 = dlogits @ self.Wout.T
        dW2 = F.T @ dH3
        db2 = dH3.sum(axis=0)
        dF = dH3 @ self.W2.T
        dF = dF * (1.0 - F * F)
        dW1 = H2.T @ dF
        db1 = dF.sum(axis=0)
        dH2 = dF @ self.W2 + dH3            # 残差 (W2: 2*dim -> dim)
        dX = dH2.copy()                        # X 的梯度 (残差直连)
        dWo = H.T @ dH2
        dH = dH2 @ self.Wo.T
        dV = A.T @ dH
        dA = dH @ V.T
        dA -= A * (dA * A).sum(axis=-1, keepdims=True)   # softmax 反向
        dS = np.where(self.mask[:n, :n], 0.0, dA)        # 因果 mask: 屏蔽未来位置梯度
        dQ = dS @ K
        dK = dS.T @ Q
        dWq = X.T @ dQ
        dWk = X.T @ dK
        dWv = X.T @ dV
        dX += dQ @ self.Wq.T + dK @ self.Wk.T + dV @ self.Wv.T

        # embedding / 位置梯度 (仅示范, 累加到对应行)
        for i, tid in enumerate(ids_in):
            self.E[tid] -= lr * dX[i]
        self.P[:n] -= lr * dX

        for name, grad in (("Wq", dWq), ("Wk", dWk), ("Wv", dWv), ("Wo", dWo),
                           ("W1", dW1), ("b1", db1), ("W2", dW2), ("b2", db2),
                           ("Wout", dWout)):
            setattr(self, name, getattr(self, name) - lr * grad)
        return loss

    def generate(self, seed_ids, max_new: int, temperature: float = 0.8) -> list:
        """自回归生成 patch id 序列。"""
        ids = list(seed_ids)
        for _ in range(max_new):
            ctx = ids[-self.seq_len:]
            logits, _ = self.forward(ctx)
            logits = logits[-1] / max(temperature, 1e-6)
            logits = logits - logits.max()
            probs = np.exp(logits)
            probs /= probs.sum()
            ids.append(int(np.random.default_rng().choice(len(probs), p=probs)))
        return ids


def train_demo(steps: int = 250, lr: float = 0.05, seed: int = 0):
    """在微型中英代码混合语料上训练, 返回 (模型, 词表, 初始loss, 最终loss)。"""
    corpus = (
        "The quick brown fox jumps over the lazy dog. "
        "Tokenizer-free models read raw bytes directly; no vocabulary is needed. "
        "分词器不是必需品：模型可以直接理解字节序列。"
        "def entropy_patch(data): return patches  # code works too\n"
    ).encode("utf-8") * 6

    vocab, ids = build_training_data(corpus, threshold=3.5)
    model = TinyCausalTransformer(len(vocab), dim=24, seq_len=min(96, len(ids) - 1), seed=seed)
    seq = ids[: model.seq_len + 1]
    first = model.loss(seq)
    for _ in range(steps):
        model.train_step(seq, lr=lr)
    return model, vocab, first, model.loss(seq), seq
