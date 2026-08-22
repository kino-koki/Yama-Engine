// ============================================================================================
//  GPUProfiler.cs — GPU Profiling 封装
//  本文件是 ProfilingSystem.cs 的前端封装。GPU Profiling 使用 RHI Query 实现。
// ============================================================================================

using System.Diagnostics;

namespace ShanyuEngine.Diagnostics
{
    public class GPUProfiler
    {
        /// <summary>开始 GPU 采样（需 RHI TimestampQuery 支持）。</summary>
        public static void BeginSample(string name) { }

        /// <summary>结束 GPU 采样。</summary>
        public static double EndSample(string name) => 0;
    }
}
