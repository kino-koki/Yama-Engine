# benchmark.py — FLOP 受控的 BPE vs byte-patch 受控对比评测 (玩具规模)
#
# 综述 6.2 节 P1 阶段的最小验证链路 (无 GPU 环境):
#   1. 同一语料 (中英代码混合 UTF-8) 切分 train/val
#   2. 三种"切分方式"覆盖同样的字节:
#        a) BPE (基线)         —— bpe_tokenizer.py
#        b) n-gram 熵动态 patch —— entropy_patcher.py
#        c) 可学习熵动态 patch  —— learned_entropy.py (小 Transformer 训练得到的熵)
#   3. 相同主干 (TinyCausalTransformer, dim/seq_len 相同) 分别训练
#   4. 指标: 验证集 bits-per-byte (BPB) —— 跨切分方式可比的标准指标
#      (总 NLL bit 数 / 验证集字节数; 越低越好)
#   5. FLOP 控制: 估计每步 FLOPs, 并给出 BPE 的"FLOP 匹配"版
#      (BPE 序列更长→单步更贵; 按比例放大其步数使总训练 FLOPs 对齐)
#
# 结果写入 results/benchmark_results.json 并打印对比表。
# 注意: 玩具规模仅验证"评测链路正确、方向可测"，不代表论文级 8B/4T 结论。

import json
import math
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from bpe_tokenizer import BPETokenizer  # noqa: E402
from entropy_patcher import ByteEntropyModel, patch_bytes  # noqa: E402
from learned_entropy import LearnedEntropyModel  # noqa: E402
from patch_lm import TinyCausalTransformer  # noqa: E402

DIM = 32
SEQ_LEN = 128
EPOCHS = 40
LEARNED_STEPS = 25
SEED = 0


def make_corpus() -> bytes:
    """多变的训练/验证语料 (避免整段重复, 保证 val 是真正的 held-out 内容)。"""
    lines = [
        "The quick brown fox jumps over the lazy dog.",
        "Tokenizer-free models read raw bytes directly; no vocabulary is required.",
        "分词器不是必需品：模型可以直接理解字节序列，这对中文尤其有利。",
        "研究表明，熵驱动的动态分块能够在算力受控的比较中追平甚至超过子词基线。",
        "def entropy_patch(data):\n    return [data[s:e] for s, e in zip(b, b[1:])]\n",
        "for i in range(10):\n    print(i, i * i, i ** 3)\n",
        "Byte-level modeling removes the arbitrary boundary problem of subword vocabularies.",
        "信息量高的位置应当获得更多算力，而可预测的文本应当被压缩成更长的 patch。",
        "class Transformer:\n    def __init__(self, dim):\n        self.dim = dim\n",
        " unicode handles 中文、日本語、한국어 and emoji 🚀 without special cases.",
        "The entropy model predicts how surprising the next byte will be.",
        "if (surprisal > threshold) { start_new_patch(); }",
        "Frozen embeddings drift when the gradient norm explodes; clip them.",
        "评估指标是 bits-per-byte：跨切分方式可比，越低越好。",
        "A held-out validation set must contain text never seen during training.",
        "print(sum(x ** 2 for x in range(100)))\n",
        "动态 patch 的长度分布本身就是一种信息：可预测区域长，困难区域短。",
        "The hierarchical network learns where words begin and end without supervision.",
        "稳定训练的三个要素：裁剪梯度、 warmup 学习率、以及足够的验证数据。",
        "byte-patch models scale by growing patch size together with model size.",
        "attention over patches costs less than attention over raw bytes.",
        "evaluating with bits-per-byte makes tokenizations comparable.",
    ]
    return "\n".join(lines).encode("utf-8")


def chunks(ids, seq_len):
    out = []
    for i in range(0, max(1, len(ids) - 1), seq_len):
        w = ids[i:i + seq_len + 1]
        if len(w) >= 2:
            out.append(w)
    return out


def est_window_flops(seq: int, dim: int) -> float:
    """单窗口单步 FLOPs 粗估: QKVO+FFN (12 d^2/词元) + 注意力矩阵 (2 L d)。"""
    return seq * (12 * dim * dim) + 2 * seq * seq * dim


def train_on_windows(vocab_size, windows, epochs, seed):
    model = TinyCausalTransformer(vocab_size, dim=DIM, seq_len=SEQ_LEN, seed=seed)
    loss = None
    for _ in range(epochs):
        for w in windows:
            loss = model.train_step(w, lr=0.05)
    return model, loss


def eval_bpb(model, ids, seq_len) -> float:
    """验证集总 NLL (bit): 逐位置 -log2 p(id_j | 前缀), 滑动窗口上下文。"""
    total = 0.0
    for j in range(1, len(ids)):
        ctx = ids[max(0, j - seq_len):j]
        logits, _ = model.forward(ctx)
        row = logits[-1] - logits[-1].max()
        p = np.exp(row)
        p = p / p.sum()
        total += float(-np.log2(p[ids[j]] + 1e-12))
    return total


def main():
    t0 = time.time()
    corpus = make_corpus()
    split = int(len(corpus) * 0.8)
    train_bytes, val_bytes = corpus[:split], corpus[split:]
    print(f"corpus: train={len(train_bytes)}B val={len(val_bytes)}B")

    rows = []

    # ── 基线: BPE ──
    bpe = BPETokenizer()
    bpe.train(train_bytes, vocab_size=300)
    bpe_train_ids = bpe.encode(train_bytes)
    bpe_val_ids = bpe.encode(val_bytes)
    bpe_windows = chunks(bpe_train_ids, SEQ_LEN)
    model_bpe, loss_bpe = train_on_windows(bpe.vocab_size, bpe_windows, EPOCHS, SEED)
    bits_bpe = eval_bpb(model_bpe, bpe_val_ids, SEQ_LEN)
    flops_bpe = EPOCHS * len(bpe_windows) * est_window_flops(SEQ_LEN, DIM)
    rows.append({
        "model": "BPE (baseline)",
        "train_units": len(bpe_train_ids), "val_units": len(bpe_val_ids),
        "val_bits": bits_bpe,
        "bpb": bits_bpe / len(val_bytes),
        "est_train_flops": flops_bpe,
    })

    # FLOP 匹配版 BPE: 依据与 patch 模型的单位数比例放大步数 (在得到 patch 单位数后补跑)
    # ── n-gram 熵动态 patch ──
    ng = ByteEntropyModel(order=4)
    ng.train(train_bytes)
    train_patches = patch_bytes(train_bytes, ng)
    val_patches = patch_bytes(val_bytes, ng)

    # ── 可学习熵动态 patch ──
    lem = LearnedEntropyModel(dim=DIM, seq_len=SEQ_LEN, steps=LEARNED_STEPS, seed=SEED)
    lem.train(train_bytes)
    train_patches_l = patch_bytes(train_bytes, lem)
    val_patches_l = patch_bytes(val_bytes, lem)

    def patch_rows(tag, tr_patches, va_patches):
        vocab = {}
        for p in tr_patches + va_patches:
            vocab.setdefault(p, len(vocab))
        tr_ids = [vocab[p] for p in tr_patches]
        va_ids = [vocab[p] for p in va_patches]
        windows = chunks(tr_ids, SEQ_LEN)
        model, loss = train_on_windows(len(vocab), windows, EPOCHS, SEED)
        bits = eval_bpb(model, va_ids, SEQ_LEN)
        flops = EPOCHS * len(windows) * est_window_flops(SEQ_LEN, DIM)
        rows.append({
            "model": tag,
            "train_units": len(tr_ids), "val_units": len(va_ids),
            "val_bits": bits,
            "bpb": bits / len(val_bytes),
            "est_train_flops": flops,
        })

    patch_rows("byte-patch (n-gram entropy)", train_patches, val_patches)
    patch_rows("byte-patch (learned entropy)", train_patches_l, val_patches_l)

    # 可学习熵模型自身的训练/推理开销必须计入其 FLOPs (BLT 的诚实核算方式)
    byte_windows = max(1, len(train_bytes) // SEQ_LEN)
    flops_entropy_model = (LEARNED_STEPS * byte_windows * est_window_flops(SEQ_LEN, DIM)
                           + len(val_bytes) * est_window_flops(SEQ_LEN, DIM))
    for r in rows:
        if "learned entropy" in r["model"]:
            r["est_train_flops"] += flops_entropy_model

    # ── FLOP 匹配: 让较便宜的一方按比例增加步数, 使总训练 FLOPs 对齐 ──
    cheapest = min(r["est_train_flops"] for r in rows)
    if flops_bpe > cheapest * 1.05:
        scale = flops_bpe / cheapest
        epochs_matched = max(1, int(round(EPOCHS * scale)))
        # 给两个 patch 模型都补跑到 BPE 的 FLOP 水平
        for tag, tr_p, va_p in (("byte-patch (n-gram entropy, FLOP-matched)", train_patches, val_patches),
                                ("byte-patch (learned entropy, FLOP-matched)", train_patches_l, val_patches_l)):
            vocab = {}
            for p in tr_p + va_p:
                vocab.setdefault(p, len(vocab))
            tr_ids = [vocab[p] for p in tr_p]
            va_ids = [vocab[p] for p in va_p]
            windows = chunks(tr_ids, SEQ_LEN)
            model, _ = train_on_windows(len(vocab), windows, epochs_matched, SEED)
            bits = eval_bpb(model, va_ids, SEQ_LEN)
            rows.append({
                "model": tag,
                "train_units": len(tr_ids), "val_units": len(va_ids),
                "val_bits": bits,
                "bpb": bits / len(val_bytes),
                "est_train_flops": EPOCHS * len(windows) * est_window_flops(SEQ_LEN, DIM) * scale,
            })

    # ── 输出 ──
    print(f"\nvalidation bytes: {len(val_bytes)}  (BPB = bits-per-byte, 越低越好)\n")
    print(f"{'model':34s} {'train_u':>8s} {'val_u':>7s} {'BPB':>8s} {'FLOPs(est)':>14s}")
    for r in rows:
        print(f"{r['model']:34s} {r['train_units']:>8d} {r['val_units']:>7d} "
              f"{r['bpb']:>8.4f} {r['est_train_flops']:>14.3g}")

    base = rows[0]["bpb"]
    print()
    for r in rows[1:]:
        delta = (r["bpb"] - base) / base * 100
        verdict = "更优" if delta < 0 else "更差"
        print(f"  {r['model']:34s} vs BPE: BPB {delta:+.1f}% ({verdict})")

    os.makedirs(os.path.join(os.path.dirname(os.path.abspath(__file__)), "results"), exist_ok=True)
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results", "benchmark_results.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"val_bytes": len(val_bytes), "dim": DIM, "seq_len": SEQ_LEN,
                   "epochs": EPOCHS, "rows": rows, "elapsed_s": time.time() - t0},
                  f, ensure_ascii=False, indent=2)
    print(f"\nresults -> {out}  (elapsed {time.time() - t0:.1f}s)")


if __name__ == "__main__":
    main()
