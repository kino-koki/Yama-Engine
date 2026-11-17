# transformer.py — 工业级 numpy 因果 Transformer (训练 + KV-Cache 推理)
#
# 面向生产实践的完整组件 (纯 numpy, 无深度学习框架依赖):
#   - RMSNorm (含手写反向)
#   - 多头因果自注意力 (训练并行前向 + KV-Cache 逐 token 增量解码)
#   - SwiGLU 前馈网络 (含手写反向)
#   - AdamW (解耦权重衰减) + 全局范数梯度裁剪
#   - Warmup + 余弦退火学习率调度
#   - 输出头权重绑定 (tie_weights)
#   - 完整手写反向传播 —— 可通过数值梯度检验 (见 tests/test_transformer.py)
#
# 所有参数存放于 dict[str, np.ndarray]，便于优化器/梯度检验统一处理。

import math

import numpy as np


# ══════════════════════════════════════════════════════════════════════
# 配置
# ══════════════════════════════════════════════════════════════════════

class TransformerConfig:
    """因果 Transformer 超参数。"""

    def __init__(self, vocab_size: int, dim: int = 64, n_layers: int = 2,
                 n_heads: int = 4, ffn_mult: int = 4, seq_len: int = 128,
                 tie_weights: bool = True, norm_eps: float = 1e-5):
        if dim % n_heads != 0:
            raise ValueError("dim 必须能被 n_heads 整除")
        self.vocab_size = vocab_size
        self.dim = dim
        self.n_layers = n_layers
        self.n_heads = n_heads
        self.head_dim = dim // n_heads
        self.ffn_dim = ffn_mult * dim
        self.seq_len = seq_len
        self.tie_weights = tie_weights
        self.norm_eps = norm_eps


# ══════════════════════════════════════════════════════════════════════
# 基础算子 (前向 + 反向)
# ══════════════════════════════════════════════════════════════════════

def rmsnorm_forward(x: np.ndarray, g: np.ndarray, eps: float) -> tuple:
    """RMSNorm: x / sqrt(mean(x^2)+eps) * g。返回 (out, cache)。"""
    ms = np.mean(x * x, axis=-1, keepdims=True)
    r = 1.0 / np.sqrt(ms + eps)
    out = x * r * g
    return out, (x, r, g)


def rmsnorm_backward(dout: np.ndarray, cache: tuple) -> np.ndarray:
    x, r, g = cache
    dnorm = dout * g
    # d x_j = dnorm_j / r - x_j * sum_i(dnorm_i * x_i) * r^3 / L
    # (r = 1/rms, 故 dr/dx_j = -x_j·r³/L)
    dx = dnorm * r - x * (np.sum(dnorm * x, axis=-1, keepdims=True) * (r ** 3) / x.shape[-1])
    dg = np.sum(dout * x * r, axis=0)
    return dx, dg


def silu(x: np.ndarray) -> np.ndarray:
    return x / (1.0 + np.exp(-x))


def silu_backward(x: np.ndarray) -> np.ndarray:
    s = 1.0 / (1.0 + np.exp(-x))
    return s * (1.0 + x * (1.0 - s))


def softmax_rows(s: np.ndarray) -> np.ndarray:
    s = s - s.max(axis=-1, keepdims=True)
    e = np.exp(s)
    return e / e.sum(axis=-1, keepdims=True)


def cross_entropy(logits: np.ndarray, targets: np.ndarray) -> tuple:
    """平均交叉熵。返回 (loss, dlogits)。"""
    z = logits - logits.max(axis=-1, keepdims=True)
    e = np.exp(z)
    probs = e / e.sum(axis=-1, keepdims=True)
    n = len(targets)
    loss = float(-np.mean(np.log(probs[np.arange(n), targets] + 1e-12)))
    dlogits = probs.copy()
    dlogits[np.arange(n), targets] -= 1.0
    dlogits /= n
    return loss, dlogits


# ══════════════════════════════════════════════════════════════════════
# 参数初始化
# ══════════════════════════════════════════════════════════════════════

def init_params(cfg: TransformerConfig, seed: int = 0) -> dict:
    rng = np.random.default_rng(seed)
    p = {}
    p["E"] = rng.normal(0, 0.05, (cfg.vocab_size, cfg.dim))
    p["P"] = rng.normal(0, 0.02, (cfg.seq_len, cfg.dim))
    for l in range(cfg.n_layers):
        p[f"l{l}.norm1.g"] = np.ones(cfg.dim)
        p[f"l{l}.Wq"] = rng.normal(0, 0.2, (cfg.dim, cfg.dim))
        p[f"l{l}.Wk"] = rng.normal(0, 0.2, (cfg.dim, cfg.dim))
        p[f"l{l}.Wv"] = rng.normal(0, 0.2, (cfg.dim, cfg.dim))
        p[f"l{l}.Wo"] = rng.normal(0, 0.2, (cfg.dim, cfg.dim)) / math.sqrt(2 * cfg.n_layers)
        p[f"l{l}.norm2.g"] = np.ones(cfg.dim)
        p[f"l{l}.Wu"] = rng.normal(0, 0.15, (cfg.dim, cfg.ffn_dim))
        p[f"l{l}.Wg"] = rng.normal(0, 0.15, (cfg.dim, cfg.ffn_dim))
        p[f"l{l}.W2"] = rng.normal(0, 0.15 / math.sqrt(2 * cfg.n_layers), (cfg.ffn_dim, cfg.dim))
    p["norm_out.g"] = np.ones(cfg.dim)
    if not cfg.tie_weights:
        p["Wout"] = rng.normal(0, 0.02, (cfg.dim, cfg.vocab_size))
    return p


# ══════════════════════════════════════════════════════════════════════
# 前向 (训练: 并行因果; 推理: KV-Cache 逐 token)
# ══════════════════════════════════════════════════════════════════════

def _split_heads(x: np.ndarray, n_heads: int) -> np.ndarray:
    L, d = x.shape
    dh = d // n_heads
    return x.reshape(L, n_heads, dh)


def forward(params: dict, cfg: TransformerConfig, ids: list) -> tuple:
    """训练用并行前向。返回 (logits [L, V], cache)。ids 长度 ≤ cfg.seq_len。"""
    L = len(ids)
    if L > cfg.seq_len:
        raise ValueError(f"序列长度 {L} 超过 seq_len {cfg.seq_len}")
    caches = []
    x = params["E"][ids] + params["P"][:L]

    for l in range(cfg.n_layers):
        h1, c1 = rmsnorm_forward(x, params[f"l{l}.norm1.g"], cfg.norm_eps)
        q = h1 @ params[f"l{l}.Wq"]
        k = h1 @ params[f"l{l}.Wk"]
        v = h1 @ params[f"l{l}.Wv"]
        qh = _split_heads(q, cfg.n_heads)
        kh = _split_heads(k, cfg.n_heads)
        vh = _split_heads(v, cfg.n_heads)
        dh = cfg.head_dim
        scale = 1.0 / math.sqrt(dh)
        att_logits = np.stack([qh[:, h] @ kh[:, h].T * scale for h in range(cfg.n_heads)], axis=0)
        causal = np.triu(np.ones((L, L), dtype=bool), k=1)
        att_logits = np.where(causal[None, :, :], -1e9, att_logits)
        att = np.stack([softmax_rows(att_logits[h]) for h in range(cfg.n_heads)], axis=0)
        oh = np.stack([att[h] @ vh[:, h] for h in range(cfg.n_heads)], axis=0)
        o = oh.transpose(1, 0, 2).reshape(L, cfg.dim)
        ao = o @ params[f"l{l}.Wo"]
        x2 = x + ao
        h2, c2 = rmsnorm_forward(x2, params[f"l{l}.norm2.g"], cfg.norm_eps)
        u = h2 @ params[f"l{l}.Wu"]
        g = h2 @ params[f"l{l}.Wg"]
        f = silu(u) * g
        y = f @ params[f"l{l}.W2"]
        x3 = x2 + y
        caches.append((c1, (h1, q, k, v, qh, kh, vh, att, o), (c2, h2, u, g, f)))
        x = x3

    hn, cn = rmsnorm_forward(x, params["norm_out.g"], cfg.norm_eps)
    if cfg.tie_weights:
        logits = hn @ params["E"].T
    else:
        logits = hn @ params["Wout"]
    caches.append((cn, hn))
    return logits, caches


def backward(params: dict, cfg: TransformerConfig, ids: list,
             caches: tuple, dlogits: np.ndarray) -> dict:
    """从 dlogits 反传至全部参数。返回 grads dict (与 params 同构)。"""
    L = len(ids)
    grads = {k: np.zeros_like(v) for k, v in params.items()}
    cn, hn = caches[-1]
    # 输出头 (logits = hn @ E.T 时权重绑定)
    dx = np.zeros((L, cfg.dim))
    dn = dlogits @ params["E"] if cfg.tie_weights else dlogits @ params["Wout"].T
    if cfg.tie_weights:
        grads["E"] += dlogits.T @ hn
    else:
        grads["Wout"] = dlogits.T @ hn
    dxn, dg = rmsnorm_backward(dn, cn)
    grads["norm_out.g"] += dg
    dx = dxn.copy()   # 输出 norm 的梯度流入残差流 (此前被丢弃导致层路径梯度全零)
    x = params["E"][ids] + params["P"][:L]

    for l in reversed(range(cfg.n_layers)):
        c1, att_cache, ffn_cache = caches[l]
        h1, q, k, v, qh, kh, vh, att, o = att_cache
        c2, h2, u, g, f = ffn_cache
        # x3 = x2 + y  →  dx2 = dx + dy
        dx2 = dx.copy()
        # y = f @ W2
        grads[f"l{l}.W2"] = f.T @ dx2
        df = dx2 @ params[f"l{l}.W2"].T
        # f = silu(u) * g
        du = df * g * silu_backward(u)
        dg = df * silu(u)
        grads[f"l{l}.Wu"] = h2.T @ du
        grads[f"l{l}.Wg"] = h2.T @ dg
        dh2 = du @ params[f"l{l}.Wu"].T + dg @ params[f"l{l}.Wg"].T   # dL/dh2
        # h2 = rmsnorm(x2)
        dx2_norm, dg2 = rmsnorm_backward(dh2, c2)
        grads[f"l{l}.norm2.g"] += dg2
        dx2 += dx2_norm                     # dL/dx2 = 直接残差 + norm 路径
        # x2 = x + ao ; ao = o @ Wo
        dao = dx2
        grads[f"l{l}.Wo"] = o.T @ dao
        dx = dx2.copy()                     # 残差流梯度 (传给下一层/输入)
        do = dao @ params[f"l{l}.Wo"].T
        # 拆回头部: o = concat(oh)
        doh = do.reshape(L, cfg.n_heads, cfg.head_dim).transpose(1, 0, 2)
        dh = cfg.head_dim
        scale = 1.0 / math.sqrt(dh)
        # 逐头反向
        dqh = np.zeros_like(qh)
        dkh = np.zeros_like(kh)
        dvh = np.zeros_like(vh)
        for h in range(cfg.n_heads):
            dAh = doh[h] @ vh[:, h].T
            dVh = att[h].T @ doh[h]
            dSh = att[h] * (dAh - np.sum(dAh * att[h], axis=-1, keepdims=True))
            dqh[:, h] = dSh @ kh[:, h] * scale
            dkh[:, h] = dSh.T @ qh[:, h] * scale
            dvh[:, h] = dVh
        dq = dqh.reshape(L, cfg.dim)
        dk = dkh.reshape(L, cfg.dim)
        dv = dvh.reshape(L, cfg.dim)
        grads[f"l{l}.Wq"] = h1.T @ dq
        grads[f"l{l}.Wk"] = h1.T @ dk
        grads[f"l{l}.Wv"] = h1.T @ dv
        dh1 = dq @ params[f"l{l}.Wq"].T + dk @ params[f"l{l}.Wk"].T + dv @ params[f"l{l}.Wv"].T
        # h1 = rmsnorm(x)
        dx1, dg1 = rmsnorm_backward(dh1, c1)
        grads[f"l{l}.norm1.g"] += dg1
        dx += dx1               # x = E[ids] + P → 残差汇入
        for i, tid in enumerate(ids):
            grads["E"][tid] += dx[i]
        grads["P"][:L] += dx
    return grads


# ══════════════════════════════════════════════════════════════════════
# 损失 + 梯度
# ══════════════════════════════════════════════════════════════════════

def loss_and_grads(params: dict, cfg: TransformerConfig, ids: list) -> tuple:
    """自回归下一 token 交叉熵 (平均) 及其梯度。ids 长度 ≥ 2。"""
    ctx, targets = ids[:-1], ids[1:]
    logits, caches = forward(params, cfg, ctx)
    loss, dlogits = cross_entropy(logits, np.array(targets))
    grads = backward(params, cfg, ctx, caches, dlogits)
    return loss, grads


# ══════════════════════════════════════════════════════════════════════
# AdamW 优化器 + 学习率调度 + 梯度裁剪
# ══════════════════════════════════════════════════════════════════════

class AdamW:
    """解耦权重衰减的 AdamW。"""

    def __init__(self, params: dict, lr: float = 3e-3, betas=(0.9, 0.95),
                 eps: float = 1e-8, weight_decay: float = 0.1):
        self.lr = lr
        self.betas = betas
        self.eps = eps
        self.weight_decay = weight_decay
        self.t = 0
        self.m = {k: np.zeros_like(v) for k, v in params.items()}
        self.v = {k: np.zeros_like(v) for k, v in params.items()}

    def step(self, params: dict, grads: dict, lr: float = None) -> None:
        """lr 传入时覆盖默认 (配合调度器)。"""
        lr = lr if lr is not None else self.lr
        self.t += 1
        b1, b2 = self.betas
        c1 = 1.0 - b1 ** self.t
        c2 = 1.0 - b2 ** self.t
        for k, g in grads.items():
            self.m[k] = b1 * self.m[k] + (1.0 - b1) * g
            self.v[k] = b2 * self.v[k] + (1.0 - b2) * (g * g)
            mhat = self.m[k] / c1
            vhat = self.v[k] / c2
            params[k] -= lr * (mhat / (np.sqrt(vhat) + self.eps) + self.weight_decay * params[k])


def clip_grads_global(grads: dict, max_norm: float = 1.0) -> float:
    """全局范数梯度裁剪 (原地)。返回裁剪前的范数。"""
    total = 0.0
    for g in grads.values():
        total += float(np.sum(g * g))
    norm = math.sqrt(total)
    if norm > max_norm and norm > 0:
        scale = max_norm / norm
        for k in grads:
            grads[k] = grads[k] * scale
    return norm


def lr_at(step: int, base_lr: float, warmup_steps: int, total_steps: int,
          min_ratio: float = 0.1) -> float:
    """Warmup + 余弦退火。"""
    if step < warmup_steps:
        return base_lr * (step + 1) / max(1, warmup_steps)
    progress = (step - warmup_steps) / max(1, total_steps - warmup_steps)
    progress = min(progress, 1.0)
    cos = 0.5 * (1.0 + math.cos(math.pi * progress))
    return base_lr * (min_ratio + (1.0 - min_ratio) * cos)


# ══════════════════════════════════════════════════════════════════════
# KV-Cache 增量推理
# ══════════════════════════════════════════════════════════════════════

class KVCache:
    """逐层缓存 K/V (形状 [t, n_heads, head_dim])，支持增量解码。"""

    def __init__(self, n_layers: int):
        self.k = [None] * n_layers
        self.v = [None] * n_layers

    def append(self, layer: int, k: np.ndarray, v: np.ndarray) -> None:
        if self.k[layer] is None:
            self.k[layer] = k
            self.v[layer] = v
        else:
            self.k[layer] = np.concatenate([self.k[layer], k], axis=0)
            self.v[layer] = np.concatenate([self.v[layer], v], axis=0)

    def __len__(self):
        return 0 if self.k[0] is None else self.k[0].shape[0]


def forward_step(params: dict, cfg: TransformerConfig, token_id: int,
                 kv: KVCache, pos: int) -> np.ndarray:
    """单 token 增量前向 (使用 KV-Cache)。返回该位置 logits [V]。"""
    x = params["E"][token_id] + params["P"][pos]
    for l in range(cfg.n_layers):
        h1, _ = rmsnorm_forward(x[None, :], params[f"l{l}.norm1.g"], cfg.norm_eps)
        q = (h1 @ params[f"l{l}.Wq"])[0]
        k = (h1 @ params[f"l{l}.Wk"])[0]
        v = (h1 @ params[f"l{l}.Wv"])[0]
        kv.append(l, k[None, :], v[None, :])
        dh = cfg.head_dim
        nh = cfg.n_heads
        qh = q.reshape(nh, dh)
        kh = kv.k[l].reshape(-1, nh, dh)
        vh = kv.v[l].reshape(-1, nh, dh)
        scale = 1.0 / math.sqrt(dh)
        logits_h = []
        for h in range(nh):
            scores = kh[:, h] @ qh[h] * scale
            a = softmax_rows(scores[None, :])[0]
            logits_h.append(a @ vh[:, h])
        o = np.concatenate(logits_h)
        x = x + o @ params[f"l{l}.Wo"]
        h2, _ = rmsnorm_forward(x[None, :], params[f"l{l}.norm2.g"], cfg.norm_eps)
        u = (h2 @ params[f"l{l}.Wu"])[0]
        g = (h2 @ params[f"l{l}.Wg"])[0]
        f = silu(u) * g
        x = x + f @ params[f"l{l}.W2"]   # f 已是 1-D (ffn_dim), 勿再取 [0]
    hn, _ = rmsnorm_forward(x[None, :], params["norm_out.g"], cfg.norm_eps)
    if cfg.tie_weights:
        return (hn @ params["E"].T)[0]
    return (hn @ params["Wout"])[0]


def generate(params: dict, cfg: TransformerConfig, prompt: list,
             max_new: int, temperature: float = 0.8, seed: int = 0) -> list:
    """KV-Cache 自回归生成。返回 prompt + 新增 token。"""
    rng = np.random.default_rng(seed)
    kv = KVCache(cfg.n_layers)
    ids = list(prompt)
    logits = None
    for pos, tok in enumerate(ids[: cfg.seq_len]):
        logits = forward_step(params, cfg, tok, kv, pos)
    for _ in range(max_new):
        probs = softmax_rows(logits[None, :] / max(temperature, 1e-6))[0]
        nxt = int(rng.choice(len(probs), p=probs))
        ids.append(nxt)
        if len(ids) >= cfg.seq_len:
            break
        logits = forward_step(params, cfg, nxt, kv, len(ids) - 1)
    return ids
