# tests/test_benchmark_smoke.py — 对比评测框架冒烟测试 (确定性语料+固定种子, 结果可复现)
# 运行: python research/tokenizer-free/tests/test_benchmark_smoke.py

import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import benchmark  # noqa: E402

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


def test_benchmark_end_to_end():
    benchmark.main()
    out = os.path.join(os.path.dirname(os.path.abspath(benchmark.__file__)), "results", "benchmark_results.json")
    expect(os.path.exists(out), "results json must be written")
    with open(out, encoding="utf-8") as f:
        data = json.load(f)
    expect(data["val_bytes"] > 0, "val bytes must be positive")
    rows = data["rows"]
    expect(len(rows) >= 3, "must contain BPE + at least two patch variants")
    for r in rows:
        expect(math.isfinite(r["bpb"]) and r["bpb"] > 0, f"BPB must be finite/positive for {r['model']}")
        expect(r["est_train_flops"] > 0, f"FLOPs estimate must be positive for {r['model']}")
    # 固定语料 + 固定种子 → 方向可复现: byte-patch 至少不劣于 BPE
    bpe = next(r for r in rows if r["model"] == "BPE (baseline)")
    patches = [r for r in rows if "byte-patch" in r["model"] and "FLOP-matched" not in r["model"]]
    best = min(r["bpb"] for r in patches)
    expect(best <= bpe["bpb"],
           f"byte-patch BPB ({best:.4f}) should not be worse than BPE ({bpe['bpb']:.4f})")


if __name__ == "__main__":
    check("BenchmarkEndToEnd", test_benchmark_end_to_end)
    failed = [r for r in _results if not r[1]]
    print(f"== benchmark smoke: {len(_results) - len(failed)}/{len(_results)} passed ==")
    sys.exit(1 if failed else 0)
