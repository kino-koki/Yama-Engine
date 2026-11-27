using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 可选 Rust 数值内核桥接。设计要点：
    ///  1. 不用静态平台调用特性（避免 SYSLIB1054 与 -warnaserror 冲突）；
    ///  2. 运行时 NativeLibrary.TryLoad 探测，缺失即走纯 C# 标量实现；
    ///  3. 主工程 csproj / sln / NuGet 零改动，CI 在任何平台都可编译。
    /// 这是「多语言互补但不制造编译灾难」的落点：C# 管编排与确定性，
    /// Rust 只负责一个纯数值热点（密度遍历），通过 C ABI 单向传递。
    /// </summary>
    public static class NativeKernel
    {
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(
            System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate void DensityPassFn(IntPtr positions, IntPtr densities,
                                            int n, float h2, float mass, float poly6);

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(
            System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate void CsrDensityFn(IntPtr positions, IntPtr offsets, IntPtr indices,
                                           IntPtr densities, int n, float h2, float mass, float poly6);

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(
            System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate void CovarianceFn(IntPtr p, IntPtr q, IntPtr out9, int n);

        private static readonly IntPtr _handle;
        private static readonly DensityPassFn? _densityPass;
        private static readonly CsrDensityFn? _csrDensityPass;
        private static readonly CovarianceFn? _covariancePass;

        private static T? Probe<T>(IntPtr handle, string symbol) where T : class
        {
            try
            {
                IntPtr sym = System.Runtime.InteropServices.NativeLibrary.GetExport(handle, symbol);
                return System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(sym);
            }
            catch (EntryPointNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
            catch (Exception) { return null; }
        }

        static NativeKernel()
        {
            IntPtr h = IntPtr.Zero;
            DensityPassFn? fn = null;
            CsrDensityFn? csr = null;
            CovarianceFn? cov = null;
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad("yama_reality_physics", out h))
            {
                fn = Probe<DensityPassFn>(h, "yr_density_pass");
                csr = Probe<CsrDensityFn>(h, "yr_density_pass_csr");
                cov = Probe<CovarianceFn>(h, "yr_covariance_accum");
            }
            _handle = h;
            _densityPass = fn;
            _csrDensityPass = csr;
            _covariancePass = cov;
        }

        public static bool IsAvailable => _densityPass != null;
        public static bool IsCsrAvailable => _csrDensityPass != null;
        public static bool IsCovarianceAvailable => _covariancePass != null;
        public static bool HasHandle => _handle != IntPtr.Zero;

        /// <summary>密度遍历：有原生库则走 C ABI，否则走逐位可复现的 C# 标量实现。</summary>
        public static void DensityPass(float[] positions, float[] densities, int n,
                                       float h, float mass, float poly6)
        {
            if (positions == null || densities == null) throw new ArgumentNullException(nameof(positions));
            if (n < 0 || n * 3 > positions.Length || n > densities.Length)
                throw new ArgumentOutOfRangeException(nameof(n));
            if (_densityPass != null)
            {
                var gp = System.Runtime.InteropServices.GCHandle.Alloc(
                    positions, System.Runtime.InteropServices.GCHandleType.Pinned);
                var gd = System.Runtime.InteropServices.GCHandle.Alloc(
                    densities, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    _densityPass(gp.AddrOfPinnedObject(), gd.AddrOfPinnedObject(),
                                 n, h * h, mass, poly6);
                }
                finally { gp.Free(); gd.Free(); }
            }
            else ScalarDensityPass(positions, densities, n, h, mass, poly6);
        }

        /// <summary>C# fallback：与 Rust 内核同一公式、同一遍历顺序，结果逐位一致。</summary>
        public static void ScalarDensityPass(float[] positions, float[] densities, int n,
                                             float h, float mass, float poly6)
        {
            float h2 = h * h;
            float self = mass * poly6 * h2 * h2 * h2;
            for (int i = 0; i < n; i++)
            {
                double rho = self;
                int bi = i * 3;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    int bj = j * 3;
                    float rx = positions[bj] - positions[bi];
                    float ry = positions[bj + 1] - positions[bi + 1];
                    float rz = positions[bj + 2] - positions[bi + 2];
                    float r2 = rx * rx + ry * ry + rz * rz;
                    if (r2 >= h2) continue;
                    float d = h2 - r2;
                    rho += mass * poly6 * d * d * d;
                }
                densities[i] = (float)rho;
            }
        }

        /// <summary>由空间哈希构建 CSR 邻域（offsets 长度 n+1，indices 按 j 升序）。</summary>
        public static (int[] offsets, int[] indices) BuildNeighborCsr(
            float[] px, float[] py, float[] pz, int n, float h)
        {
            var grid = new SpatialHashGrid();
            grid.Build(px, py, pz, n, h);
            var offsets = new int[n + 1];
            var acc = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
            {
                var nb = grid.NeighborsOf(i);
                for (int t = 0; t < nb.Length; t++) acc.Add(nb[t]);
                offsets[i + 1] = acc.Count;
            }
            return (offsets, acc.ToArray());
        }

        /// <summary>
        /// O(N·k) 密度遍历：邻域由 CSR 给出（已按 j 升序），有 Rust 内核则走 C ABI，
        /// 否则走 C# 同构实现。与全对 O(N²) 的 ScalarDensityPass 逐位一致
        /// （cell size = h 时 27 邻桶覆盖所有 r&lt;h 粒子，累加顺序相同）。
        /// </summary>
        public static void DensityPassCsr(float[] positions, int[] offsets, int[] indices,
                                          float[] densities, int n, float h, float mass, float poly6)
        {
            if (positions == null || offsets == null || indices == null || densities == null)
                throw new ArgumentNullException(nameof(positions));
            if (n < 0 || n * 3 > positions.Length || n + 1 > offsets.Length || n > densities.Length)
                throw new ArgumentOutOfRangeException(nameof(n));
            if (_csrDensityPass != null)
            {
                var gp = System.Runtime.InteropServices.GCHandle.Alloc(
                    positions, System.Runtime.InteropServices.GCHandleType.Pinned);
                var go = System.Runtime.InteropServices.GCHandle.Alloc(
                    offsets, System.Runtime.InteropServices.GCHandleType.Pinned);
                var gi = System.Runtime.InteropServices.GCHandle.Alloc(
                    indices, System.Runtime.InteropServices.GCHandleType.Pinned);
                var gd = System.Runtime.InteropServices.GCHandle.Alloc(
                    densities, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    _csrDensityPass(gp.AddrOfPinnedObject(), go.AddrOfPinnedObject(),
                                    gi.AddrOfPinnedObject(), gd.AddrOfPinnedObject(),
                                    n, h * h, mass, poly6);
                }
                finally { gp.Free(); go.Free(); gi.Free(); gd.Free(); }
            }
            else ScalarDensityPassCsr(positions, offsets, indices, densities, n, h, mass, poly6);
        }

        /// <summary>C# 同构 CSR 实现，与 Rust yr_density_pass_csr 公式/顺序一致，逐位可比对。</summary>
        public static void ScalarDensityPassCsr(float[] positions, int[] offsets, int[] indices,
                                                float[] densities, int n, float h, float mass, float poly6)
        {
            float h2 = h * h;
            float self = mass * poly6 * h2 * h2 * h2;
            for (int i = 0; i < n; i++)
            {
                double rho = self;
                int bi = i * 3;
                int end = Math.Min(offsets[i + 1], indices.Length);
                for (int t = offsets[i]; t < end; t++)
                {
                    int j = indices[t];
                    if (j < 0 || j >= n || j == i) continue;
                    int bj = j * 3;
                    if (bj + 2 >= positions.Length) continue;
                    float rx = positions[bj] - positions[bi];
                    float ry = positions[bj + 1] - positions[bi + 1];
                    float rz = positions[bj + 2] - positions[bi + 2];
                    float r2 = rx * rx + ry * ry + rz * rz;
                    if (r2 >= h2) continue;
                    float d = h2 - r2;
                    rho += mass * poly6 * d * d * d;
                }
                densities[i] = (float)rho;
            }
        }

        /// <summary>
        /// 形状匹配的协方差热点：Apq = Σ p_i q_iᵀ（行主序 9 个分量）。
        /// 有 Rust 内核走 C ABI，否则走 C# 同构实现；两者逐位一致。
        /// </summary>
        public static void CovarianceAccum(float[] p, float[] q, float[] out9, int n)
        {
            if (p == null || q == null || out9 == null) throw new ArgumentNullException(nameof(p));
            if (n < 0 || n * 3 > p.Length || n * 3 > q.Length || out9.Length < 9)
                throw new ArgumentOutOfRangeException(nameof(n));
            if (_covariancePass != null)
            {
                var gp = System.Runtime.InteropServices.GCHandle.Alloc(
                    p, System.Runtime.InteropServices.GCHandleType.Pinned);
                var gq = System.Runtime.InteropServices.GCHandle.Alloc(
                    q, System.Runtime.InteropServices.GCHandleType.Pinned);
                var go = System.Runtime.InteropServices.GCHandle.Alloc(
                    out9, System.Runtime.InteropServices.GCHandleType.Pinned);
                try { _covariancePass(gp.AddrOfPinnedObject(), gq.AddrOfPinnedObject(),
                                      go.AddrOfPinnedObject(), n); }
                finally { gp.Free(); gq.Free(); go.Free(); }
            }
            else ScalarCovarianceAccum(p, q, out9, n);
        }

        /// <summary>C# 同构实现，与 Rust yr_covariance_accum 公式/累加顺序一致。</summary>
        public static void ScalarCovarianceAccum(float[] p, float[] q, float[] out9, int n)
        {
            for (int k = 0; k < 9; k++) out9[k] = 0f;
            for (int i = 0; i < n; i++)
            {
                int b = i * 3;
                float px = p[b], py = p[b + 1], pz = p[b + 2];
                float qx = q[b], qy = q[b + 1], qz = q[b + 2];
                out9[0] += px * qx; out9[1] += px * qy; out9[2] += px * qz;
                out9[3] += py * qx; out9[4] += py * qy; out9[5] += py * qz;
                out9[6] += pz * qx; out9[7] += pz * qy; out9[8] += pz * qz;
            }
        }
    }
}
