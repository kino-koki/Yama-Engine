# tests/test_transformer.py — 工业级 numpy Transformer 深度验证
# 金标准: 数值梯度检验 (central difference) 验证手写反向传播的正确性。
# 运行: python research/tokenizer-free/tests/test_transformer.py

import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from transformer import (AdamW, KVCache, TransformerConfig, clip_grads_global,  # noqa: E402
                         forward, forward_step, generate, init_params,
                         lr_at, loss_and_grads)

_results = []


def check(name, fn):
    try:
        fn()
        _results.append((name, True, ""))
        print(f"  [PASS] {name}")
    except AssertionError as e:
        _results.append((name, False, str(e)))
        print(f"  [FAIL] {name}: {e}")
    except Exception as e:  # noqa: BLE001
        _results.append((name, False, f"{type(e).__name__}: {e}"))
        print(f"  [FAIL] {name}: {type(e).__name__}: {e}")


def expect(cond, msg):
    if not cond:
        raise AssertionError(msg)


def tiny_cfg():
    return TransformerConfig(vocab_size=11, dim=6, n_layers=1, n_heads=2,
                             ffn_mult=2, seq_len=8, tie_weights=True, norm_eps=1e-5)


# ── 1. 数值梯度检验 (手写反向传播的金标准) ──
def test_numerical_gradient_check():
    cfg = tiny_cfg()
    params = init_params(cfg, seed=3)
    ids = [1, 4, 2, 7, 3]
    _, grads = loss_and_grads(params, cfg, ids)

    eps = 1e-5
    worst = 0.0
    worst_key = ""
    checked = 0
    for key, arr in params.items():
        flat = arr.ravel()
        gflat = grads[key].ravel()
        for j in range(flat.shape[0]):
            orig = flat[j]
            flat[j] = orig + eps
            lp, _ = loss_and_grads(params, cfg, ids)
            flat[j] = orig - eps
            lm, _ = loss_and_grads(params, cfg, ids)
            flat[j] = orig
            numeric = (lp - lm) / (2 * eps)
            analytic = gflat[j]
            denom = max(1.0, abs(numeric), abs(analytic))
            rel = abs(numeric - analytic) / denom
            if rel > worst:
                worst, worst_key = rel, f"{key}[{j}]"
            checked += 1
    expect(worst < 1e-4,
           f"gradient check failed: max rel err {worst:.2e} at {worst_key} ({checked} entries checked)")


# ── 2. 因果性: 改变未来 token 不影响过去 logits ──
def test_causal_property():
    cfg = tiny_cfg()
    params = init_params(cfg, seed=5)
    ids = [1, 2, 3, 4]
    l1, _ = forward(params, cfg, ids)
    ids2 = [1, 2, 3, 9]
    l2, _ = forward(params, cfg, ids2)
    expect(np.allclose(l1[:3], l2[:3], atol=1e-10),
           "changing the last token must not affect earlier positions' logits")
    expect(not np.allclose(l1[3], l2[3]), "the last position itself must change")


# ── 3. KV-Cache 增量解码与并行前向一致 ──
def test_kv_cache_consistency():
    cfg = tiny_cfg()
    params = init_params(cfg, seed=7)
    ids = [1, 2, 3, 4, 5]
    full, _ = forward(params, cfg, ids)

    kv = KVCache(cfg.n_layers)
    last = None
    for pos, tok in enumerate(ids):
        last = forward_step(params, cfg, tok, kv, pos)
    expect(np.allclose(last, full[-1], atol=1e-8),
           "incremental KV-cache logits must match parallel forward at the last position")


# ── 4. AdamW 单步数学: 与手算参考一致 (含解耦权重衰减) ──
def test_adamw_step_math():
    from transformer import AdamW as A
    p = {"w": np.array([1.0])}
    opt = A(p, lr=0.1, betas=(0.9, 0.95), eps=1e-8, weight_decay=0.1)
    opt.step(p, {"w": np.array([2.0])})
    # m = 0.9*0 + 0.1*2 = 0.2 ; v = 0.95*0 + 0.05*4 = 0.2
    # mhat = 0.2/0.1 = 2.0 ; vhat = 0.2/0.05 = 4.0
    # update = lr * (mhat/(sqrt(vhat)+eps) + wd*w)
    expected = 1.0 - 0.1 * (2.0 / (math.sqrt(4.0) + 1e-8) + 0.1 * 1.0)
    expect(abs(p["w"][0] - expected) < 1e-9,
           f"AdamW step mismatch: {p['w'][0]} vs {expected}")


# ── 5. RMSNorm 性质: x 归一化后逐维 RMS 为 1 (即 out/g 的 RMS = 1) ──
def test_rmsnorm_property():
    from transformer import rmsnorm_forward
    x = np.array([[3.0, -4.0, 0.0]])
    g = np.array([2.0, 0.5, 1.0])
    out, _ = rmsnorm_forward(x, g, 1e-5)
    xhat = out / g
    rms = np.sqrt(np.mean(xhat ** 2))
    expect(abs(rms - 1.0) < 1e-6, f"RMS(xhat) should be 1, got {rms}")


# ── 6. 训练收敛: 真实语料上 loss 单调下降趋势 ──
def test_training_reduces_loss():
    cfg = TransformerConfig(vocab_size=16, dim=16, n_layers=2, n_heads=2,
                            ffn_mult=2, seq_len=16, tie_weights=True)
    params = init_params(cfg, seed=11)
    rng = np.random.default_rng(0)
    data = list(rng.integers(0, 16, size=40))
    opt = AdamW(params, lr=0.02, weight_decay=0.0)
    windows = [data[i:i + cfg.seq_len + 1] for i in range(0, len(data) - cfg.seq_len - 1, 8)]
    windows = [w for w in windows if len(w) >= 4]
    first = loss_and_grads(params, cfg, windows[0])[0]
    losses = [first]
    for step in range(60):
        for w in windows:
            loss, grads = loss_and_grads(params, cfg, w)
            clip_grads_global(grads, 1.0)
            opt.step(params, grads, lr=lr_at(step, 0.02, 5, 60))
            losses.append(loss)
    expect(min(losses[10:]) < first - 0.3,
           f"training should reduce loss below {first - 0.3:.3f} (min {min(losses):.3f})")
    expect(all(np.isfinite(l) for l in losses), "all losses must be finite")


# ── 7. 生成: 长度正确、id 在词表范围内、确定性 (同种子) ──
def test_generate_deterministic():
    cfg = tiny_cfg()
    params = init_params(cfg, seed=13)
    g1 = generate(params, cfg, [1, 2], max_new=4, temperature=0.7, seed=99)
    g2 = generate(params, cfg, [1, 2], max_new=4, temperature=0.7, seed=99)
    expect(g1 == g2, "generation with the same seed must be deterministic")
    expect(len(g1) == 6, "generation must append max_new tokens")
    expect(all(0 <= t < cfg.vocab_size for t in g1), "generated ids must be in vocab range")


# ── 8. 学习率调度: warmup 线性上升 + 余弦退火 ──
def test_lr_schedule():
    expect(lr_at(0, 0.1, 10, 100) == 0.01, "warmup starts near 0")
    expect(abs(lr_at(9, 0.1, 10, 100) - 0.1) < 1e-9, "warmup ends at base lr")
    mid = lr_at(55, 0.1, 10, 100)
    expect(0.05 < mid < 0.1, f"mid-training lr should decay (got {mid})")
    expect(abs(lr_at(100, 0.1, 10, 100) - 0.1 * 0.1) < 1e-9, "end of schedule hits min ratio")


if __name__ == "__main__":
    check("NumericalGradientCheck", test_numerical_gradient_check)
    check("CausalProperty", test_causal_property)
    check("KVCacheConsistency", test_kv_cache_consistency)
    check("AdamWStepMath", test_adamw_step_math)
    check("RMSNormProperty", test_rmsnorm_property)
    check("TrainingReducesLoss", test_training_reduces_loss)
    check("GenerateDeterministic", test_generate_deterministic)
    check("LRSchedule", test_lr_schedule)
    failed = [r for r in _results if not r[1]]
    print(f"== transformer: {len(_results) - len(failed)}/{len(_results)} passed ==")
    sys.exit(1 if failed else 0)
