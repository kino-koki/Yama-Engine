using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 确定性快照/回滚缓冲：把「逐位确定」从断言升级成工程能力。
    /// 每帧存 state 副本 + FNV-1a 位级校验和；Rewind 丢弃未来帧，
    /// 配合固定步时钟即可做客户端预测 / 状态回滚重模拟的物理基础。
    /// 校验和对 float 取 IEEE 位模式，不用相等比较，能捕捉 -0 / NaN 差异。
    /// </summary>
    public sealed class YrRollbackBuffer
    {
        public readonly struct Frame
        {
            public readonly long Tick;
            public readonly float[] State;
            public readonly ulong Hash;
            public Frame(long tick, float[] state, ulong hash)
            { Tick = tick; State = state; Hash = hash; }
        }

        private readonly System.Collections.Generic.List<Frame> _frames
            = new System.Collections.Generic.List<Frame>();

        public int Count => _frames.Count;
        public System.Collections.Generic.IReadOnlyList<Frame> Frames => _frames;

        public static ulong Checksum(float[] state, long tick)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            ulong h = 14695981039346656037UL;
            ulong t = unchecked((ulong)tick);
            for (int b = 0; b < 8; b++)
            {
                h ^= (t >> (b * 8)) & 0xFFUL;
                h *= 1099511628211UL;
            }
            for (int i = 0; i < state.Length; i++)
            {
                uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(state[i]));
                for (int b = 0; b < 4; b++)
                {
                    h ^= (ulong)((bits >> (b * 8)) & 0xFFU);
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        public void Push(long tick, float[] state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var copy = (float[])state.Clone();
            _frames.Add(new Frame(tick, copy, Checksum(copy, tick)));
        }

        public bool TryGet(long tick, out float[] state)
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                if (_frames[i].Tick == tick)
                {
                    state = (float[])_frames[i].State.Clone();
                    return true;
                }
            }
            state = Array.Empty<float>();
            return false;
        }

        /// <summary>丢弃 tick 之后的所有帧，返回被丢弃的帧数。</summary>
        public int Rewind(long tick)
        {
            int keep = 0;
            while (keep < _frames.Count && _frames[keep].Tick <= tick) keep++;
            int removed = _frames.Count - keep;
            if (removed > 0) _frames.RemoveRange(keep, removed);
            return removed;
        }

        /// <summary>
        /// 验证 step 函数在同一初态下逐 tick 逐位确定。
        /// 若任一 tick 的位级校验和分叉，立即返回 false。
        /// </summary>
        public static bool VerifyDeterministic(
            Func<float[], int, float[]> step, float[] initial, int steps)
        {
            if (step == null || initial == null) throw new ArgumentNullException(nameof(step));
            float[] a = (float[])initial.Clone();
            float[] b = (float[])initial.Clone();
            for (int s = 0; s < steps; s++)
            {
                a = step(a, s);
                b = step(b, s);
                if (Checksum(a, s) != Checksum(b, s)) return false;
            }
            return true;
        }
    }
}
