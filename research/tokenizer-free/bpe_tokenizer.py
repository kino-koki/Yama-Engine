# bpe_tokenizer.py — 最小 BPE 分词器 (对比基线)
#
# 标准字节级 BPE: 从 256 个基础字节开始, 迭代合并语料中最高频相邻对,
# 直到达到目标词表大小。encode 按训练时的合并顺序重放。
# 仅用于与 tokenizer-free 方案做受控对比, 非生产实现。

from collections import defaultdict


class BPETokenizer:
    def __init__(self):
        self.merges = []              # 按顺序的合并对 ((id_a, id_b))
        self.merge_to_id = {}         # (id_a, id_b) -> 合并后 id
        self.tokens = {i: bytes([i]) for i in range(256)}  # id -> bytes
        self.next_id = 256

    def train(self, data: bytes, vocab_size: int = 300) -> None:
        ids = list(data)
        while self.next_id < vocab_size and len(ids) > 1:
            pair_counts = defaultdict(int)
            for a, b in zip(ids, ids[1:]):
                pair_counts[(a, b)] += 1
            best = max(pair_counts, key=pair_counts.get)
            if pair_counts[best] < 2:
                break  # 无重复对, 停止
            merged_id = self.next_id
            self.tokens[merged_id] = self.tokens[best[0]] + self.tokens[best[1]]
            self.merges.append(best)
            self.merge_to_id[best] = merged_id
            new_ids = []
            i = 0
            while i < len(ids):
                if i < len(ids) - 1 and (ids[i], ids[i + 1]) == best:
                    new_ids.append(merged_id)
                    i += 2
                else:
                    new_ids.append(ids[i])
                    i += 1
            ids = new_ids
            self.next_id += 1

    def encode(self, data: bytes) -> list:
        ids = list(data)
        for pair in self.merges:
            merged = self.merge_to_id[pair]
            new_ids = []
            i = 0
            n = len(ids)
            while i < n:
                if i < n - 1 and ids[i] == pair[0] and ids[i + 1] == pair[1]:
                    new_ids.append(merged)
                    i += 2
                else:
                    new_ids.append(ids[i])
                    i += 1
            ids = new_ids
            if len(ids) == 1:
                break
        return ids

    def decode(self, ids) -> bytes:
        return b"".join(self.tokens[i] for i in ids)

    @property
    def vocab_size(self) -> int:
        return self.next_id
