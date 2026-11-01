#pragma once

#ifdef _WIN32
    #ifdef SHANYU_EXPORTS
        #define SHANYU_API __declspec(dllexport)
    #else
        #define SHANYU_API __declspec(dllimport)
    #endif
#else
    #define SHANYU_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// 数学 SIMD
// ============================================================================

/// <summary>批量4x4矩阵乘法（AVX2加速）。</summary>
/// <param name="a">输入矩阵A数组，每个矩阵16个float（行主序）。</param>
/// <param name="b">输入矩阵B数组，每个矩阵16个float。</param>
/// <param name="out">输出矩阵数组，每个矩阵16个float。</param>
/// <param name="count">矩阵数量。</param>
SHANYU_API void yama_simd_matrix_multiply(const float* a, const float* b, float* out, int count);

/// <summary>批量向量-矩阵变换（AVX2加速）。</summary>
/// <param name="vec">输入向量数组 [x,y,z,w] * count。</param>
/// <param name="mat">变换矩阵数组，每个矩阵16个float。</param>
/// <param name="out">输出向量数组。</param>
/// <param name="count">向量数量。</param>
SHANYU_API void yama_simd_vector_transform(const float* vec, const float* mat, float* out, int count);

// ============================================================================
// 物理广相 Broad Phase
// ============================================================================

/// <summary>Sweep-and-Prune 广相碰撞检测（SIMD加速）。</summary>
/// <param name="aabbs">AABB数组 [minX,minY,minZ,maxX,maxY,maxZ] * bodyCount。</param>
/// <param name="bodyCount">刚体数量。</param>
/// <param name="outPairs">输出碰撞对数组 [bodyA, bodyB] * pairCount。</param>
/// <param name="maxPairs">最大输出对数。</param>
/// <returns>实际输出的碰撞对数量。</returns>
SHANYU_API int yama_physics_broadphase_sap(
    const float* aabbs, int bodyCount,
    int* outPairs, int maxPairs);

// ============================================================================
// 物理窄相 Narrow Phase
// ============================================================================

/// <summary>GJK距离算法计算两个凸体间的最短距离。</summary>
/// <param name="verticesA">物体A顶点数组 [x,y,z] * countA。</param>
/// <param name="countA">物体A顶点数。</param>
/// <param name="verticesB">物体B顶点数组。</param>
/// <param name="countB">物体B顶点数。</param>
/// <param name="transformA">物体A变换矩阵（16个float，行主序4x4）。</param>
/// <param name="transformB">物体B变换矩阵。</param>
/// <param name="outNormal">输出最短距离方向（单位向量）。</param>
/// <param name="outPointA">输出物体A上最近点。</param>
/// <param name="outPointB">输出物体B上最近点。</param>
/// <returns>最短距离（负值表示穿透）。</returns>
SHANYU_API float yama_physics_gjk_distance(
    const float* verticesA, int countA,
    const float* verticesB, int countB,
    const float* transformA, const float* transformB,
    float* outNormal, float* outPointA, float* outPointB);

/// <summary>EPA（扩展多面体算法）计算穿透深度和方向。</summary>
SHANYU_API float yama_physics_epa_depth(
    const float* verticesA, int countA,
    const float* verticesB, int countB,
    const float* transformA, const float* transformB,
    float* outNormal, float* outContactPoint);

// ============================================================================
// 物理求解器 Solver
// ============================================================================

/// <summary>并行约束求解（速度约束）。</summary>
/// <param name="invMass">质量倒数数组。</param>
/// <param name="invInertia">转动惯量倒数数组 [ix,iy,iz] * count。</param>
/// <param name="velocities">线速度数组 [vx,vy,vz] * count。</param>
/// <param name="angularVelocities">角速度数组 [wx,wy,wz] * count。</param>
/// <param name="constraints">约束数据（紧凑格式）。</param>
/// <param name="constraintCount">约束数量。</param>
/// <param name="bodyCount">刚体数量。</param>
/// <param name="iterations">迭代次数。</param>
SHANYU_API void yama_physics_solver_velocity(
    const float* invMass,
    const float* invInertia,
    float* velocities,
    float* angularVelocities,
    const float* constraints,
    int constraintCount,
    int bodyCount,
    int iterations);

// ============================================================================
// 音频 FFT
// ============================================================================

/// <summary>正向快速傅里叶变换（AVX2加速Cooley-Tukey）。</summary>
/// <param name="real">实部数组（输入/输出），长度必须为2的幂。</param>
/// <param name="imag">虚部数组（输入/输出），长度必须为2的幂。</param>
/// <param name="n">数组长度（2的幂）。</param>
SHANYU_API void yama_audio_fft_forward(float* real, float* imag, int n);

/// <summary>逆快速傅里叶变换（AVX2加速）。</summary>
SHANYU_API void yama_audio_fft_inverse(float* real, float* imag, int n);

// ============================================================================
// 音频重采样
// ============================================================================

/// <summary>SINC插值高质量重采样。</summary>
/// <param name="input">输入采样数组。</param>
/// <param name="inputCount">输入采样数。</param>
/// <param name="inputRate">输入采样率。</param>
/// <param name="output">输出采样数组（预分配）。</param>
/// <param name="outputCount">输出采样数。</param>
/// <param name="outputRate">输出采样率。</param>
SHANYU_API void yama_audio_resample_sinc(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate);

/// <summary>线性插值快速重采样（SSE加速）。</summary>
SHANYU_API void yama_audio_resample_linear(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate);

// ============================================================================
// RHI 命令缓冲
// ============================================================================

/// <summary>创建原生命令缓冲区。</summary>
/// <returns>不透明句柄。</returns>
SHANYU_API void* yama_rhi_create_command_buffer();

/// <summary>销毁命令缓冲区。</summary>
SHANYU_API void yama_rhi_destroy_command_buffer(void* handle);

/// <summary>记录索引绘制命令。</summary>
SHANYU_API void yama_rhi_cmd_draw_indexed(void* handle, int indexCount, int instanceCount);

/// <summary>记录资源屏障。</summary>
SHANYU_API void yama_rhi_cmd_resource_barrier(void* handle, int type, void* resource);

/// <summary>提交命令缓冲区到队列。</summary>
SHANYU_API void yama_rhi_submit_command_buffer(void* handle);

// ============================================================================
// 版本与初始化
// ============================================================================

/// <summary>获取原生层版本字符串。</summary>
SHANYU_API const char* yama_get_version_string();

/// <summary>检测运行时CPU特性。</summary>
/// <returns>位掩码：1=SSE2, 2=AVX, 4=AVX2, 8=AVX-512。</returns>
SHANYU_API int yama_cpu_feature_detect();

#ifdef __cplusplus
}
#endif
