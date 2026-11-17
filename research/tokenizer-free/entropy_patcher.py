# entropy_patcher.py — BLT 式熵驱动动态 patch (参考实现)
#
# 思想来源: Byte Latent Transformer (arXiv:2412.09871)
#   - 用一个小的字节级熵模型估计"下一字节的条件熵"
#   - 熵突增 (不可预测) 处开新 patch: 信息量大的地方切细, 可预测的地方切粗
#   - 无词表、无固定切分: patch 完全由内容与上下文决定
#
# 本实现为纯 Python + numpy 的教学级参考: 展示机制而非追求性能。

import math


class ByteEntropyModel:
    """阶数 k 的字节 n-gram 熵模型 (add-k 平滑), 估计下一字节条件熵 (bit)。"""

    def __init__(self, order: int = 4, alpha: float = 0.1):
        self.order = max(1, int(order))
        self.alpha = float(alpha)
        self.counts = {}   # context bytes -> [256] 计数
        self.totals = {}   # context bytes -> 总数

    def train(self, data: bytes) -> None:
        k = self.order
        counts, totals = self.counts, self.totals
        for i in range(len(data)):
            ctx = bytes(data[max(0, i - k):i])
            b = data[i]
            table = counts.get(ctx)
            if table is None:
                table = [0] * 256
                counts[ctx] = table
            table[b] += 1
            totals[ctx] = totals.get(ctx, 0) + 1

    def next_byte_entropy(self, context: bytes) -> float:
        """给定上下文, 返回下一字节的条件熵 (bit, 0~8)。未见过的上下文 → 8 (最大不确定)。"""
        ctx = bytes(context[-self.order:])
        total = self.totals.get(ctx, 0)
        if total == 0:
            return 8.0
        table = self.counts[ctx]
        denom = total + self.alpha * 256
        h = 0.0
        seen = 0
        for c in table:
            if c:
                p = (c + self.alpha) / denom
                h -= p * math.log2(p)
                seen += 1
        # 未见过字节的剩余概率质量
        p_unseen = (256 - seen) * self.alpha / denom
        if p_unseen > 0:
            # 均匀摊到未见面上的熵贡献
            h -= p_unseen * math.log2(self.alpha / denom)
        return h


def patch_bytes(data: bytes, entropy_model=None, threshold: float = 4.0,
                min_patch: int = 2, max_patch: int = 32):
    """BLT 式动态分 patch。

    - 熵 > threshold (下一字节不可预测) → 在该字节处开新 patch
    - 可预测的连续区域自动形成长 patch (省算力)
    - min_patch / max_patch 约束 patch 尺寸
    返回 list[bytes] (无缝、有序、可无损还原)。
    """
    if not data:
        return []
    if entropy_model is None:
        entropy_model = ByteEntropyModel()
        entropy_model.train(data)

    patches = []
    start = 0
    for i in range(1, len(data)):
        ctx = data[max(0, i - 8):i]
        ent = entropy_model.next_byte_entropy(ctx)
        too_long = i - start >= max_patch
        can_split = i - start >= min_patch
        if can_split and (ent > threshold or too_long):
            patches.append(bytes(data[start:i]))
            start = i
    if start < len(data):
        patches.append(bytes(data[start:]))
    return patches


def unpatch(patches) -> bytes:
    """把 patch 列表无损拼回原始字节。"""
    return b"".join(patches)
