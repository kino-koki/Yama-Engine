// ════════════════════════════════════════════════════════════════════════════
// Yama Engine - Water / Ocean FFT Simulation & Interaction
// FFT 波浪模拟: Phillips 频谱生成, 时间演化, Stockham 蝶形 IFFT, 位移/雅可比输出。
// 水面交互: 涟漪脉冲注入, 波动方程传播, 法线计算, 泡沫动态消散, 湿润痕迹。
// Shader Model 6.6 (DXC)
// 入口点: PhillipsCS, UpdateSpectrumCS, ButterflyCS, InverseTransformCS,
//         RippleImpulseCS, RippleUpdateCS, RippleNormalCS, FoamUpdateCS, WetnessUpdateCS
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# FFTSimCB / FFTButterflyCB / RippleCB / FoamSimCB / WetnessCB 对应)
// ──────────────────────────────────────────────────────────────────────────

cbuffer FFTSimCB : register(b0)
{
    int   g_FFTSize;
    float g_PatchSize;
    float g_WindSpeed;
    float2 g_WindDirection;
    float g_FFTTime;
    float g_AmplitudeScale;
    float g_Choppiness;
    float g_Gravity;
    float g_PhillipsDissipation;
    float g_MinWavelength;
    float g_MaxWavelength;
    int   g_UpdateMode;
    float g_FFTPad0;
    float g_FFTPad1;
};

cbuffer FFTButterflyCB : register(b0)
{
    int g_BF_Stage;       // 当前蝶形阶段
    int g_BF_FFTSize;     // FFT 尺寸
    int g_BF_Direction;   // 0=水平, 1=垂直
    float g_BF_Pad0;
};

cbuffer RippleCB : register(b0)
{
    int   g_RP_GridSize;
    float g_RP_CellSize;
    float g_RP_DeltaTime;
    float g_RP_WaveSpeed;
    float g_RP_Damping;
    float2 g_RP_Pad;
};

cbuffer RippleImpulseCB : register(b0)
{
    float2 g_RI_Position;
    float  g_RI_Strength;
    float  g_RI_Radius;
    int    g_RI_GridSize;
    float  g_RI_CellSize;
    float  g_RI_Pad0;
    float  g_RI_Pad1;
};

cbuffer FoamSimCB : register(b0)
{
    int   g_FM_GridSize;
    float g_FM_CellSize;
    float g_FM_DeltaTime;
    float g_FM_Dissipation;
    float g_FM_FoamThreshold;
    float g_FM_FoamIntensity;
    float2 g_FM_Pad;
};

cbuffer WetnessCB : register(b0)
{
    int   g_WT_GridSize;
    float g_WT_CellSize;
    float g_WT_DryRate;
    float g_WT_WetnessStrength;
    float g_WT_DeltaTime;
    float g_WT_Pad0;
    float g_WT_Pad1;
    float g_WT_Pad2;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
// FFT 频谱纹理 (复数: RG = real/imag)
RWTexture2D<float2> g_SpectrumH0UAV   : register(u0); // 初始频谱 h0(k)
RWTexture2D<float2> g_SpectrumH0ConjUAV: register(u1); // h0*(-k)
RWTexture2D<float2> g_SpectrumHTUAV   : register(u0); // 时刻频谱 h(k,t) (UpdateSpectrum 写)
Texture2D<float2>   g_SpectrumH0SRV   : register(t0);
Texture2D<float2>   g_SpectrumH0ConjSRV: register(t1);

// 蝶形运算: 双缓冲
RWTexture2D<float2> g_FFTBufferA : register(u0);
RWTexture2D<float2> g_FFTBufferB : register(u0);

// 位移与法线输出
RWTexture2D<float4> g_DisplacementMap : register(u0); // XYZ 位移, W 雅可比
RWTexture2D<float4> g_NormalJacobMap   : register(u1); // XYZ 法线, W 雅可比
Texture2D<float4>   g_HeightFieldSRV   : register(t0); // IFFT 后的高度场

// 涟漪资源
RWTexture2D<float>  g_RippleHeightUAV  : register(u0);
Texture2D<float>    g_RippleHeightSRV  : register(t0);
Texture2D<float>    g_RipplePrevHeightSRV : register(t1);
RWTexture2D<float4> g_RippleNormalUAV  : register(u0);
StructuredBuffer<float4> g_RippleImpulseBuf : register(t0); // (posXZ, strength, radius)

// 泡沫资源
Texture2D<float>    g_RippleFoamSourceSRV : register(t0); // 涟漪高度作为泡沫源
RWTexture2D<float>  g_FoamMapUAV       : register(u0);
Texture2D<float>    g_FoamMapSRV       : register(t1);

// 湿润资源
RWTexture2D<float>  g_WetnessMapUAV    : register(u0);

// ════════════════════════════════════════════════════════════════════════════
// PhillipsCS - 生成初始 Phillips 频谱 h0(k)
// ════════════════════════════════════════════════════════════════════════════
// Phillips 谱: P(k) = A * exp(-1/(k*L)^2) / k^4 * |k_hat . w_hat|^2
// L = windSpeed^2 / gravity (风浪特征长度)

float GaussianRandom(float2 uv)
{
    // Box-Muller 变换, 用哈希生成高斯随机数
    float2 r = hash2(uv * 1234.5 + 0.1);
    r.x = max(r.x, 1e-4);
    return sqrt(-2.0 * log(r.x)) * cos(SHANYU_TWO_PI * r.y);
}

[numthreads(8, 8, 1)]
void PhillipsCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_FFTSize || y >= (uint)g_FFTSize) return;

    int N = g_FFTSize;
    float2 k;
    // 将像素坐标映射到波数: k = (2*pi*n/L - pi*N/L)
    k.x = SHANYU_TWO_PI * (x - N * 0.5) / g_PatchSize;
    k.y = SHANYU_TWO_PI * (y - N * 0.5) / g_PatchSize;

    float kLen = length(k);
    if (kLen < 1e-6)
    {
        g_SpectrumH0UAV[uint2(x, y)] = float2(0.0, 0.0);
        g_SpectrumH0ConjUAV[uint2(x, y)] = float2(0.0, 0.0);
        return;
    }

    // 风浪特征长度
    float L = g_WindSpeed * g_WindSpeed / max(g_Gravity, 0.01);
    float wHat = dot(normalize(k), normalize(g_WindDirection));

    // Phillips 谱
    float k2 = kLen * kLen;
    float k4 = k2 * k2;
    float phillips = g_AmplitudeScale * exp(-1.0 / (k2 * L * L + 1e-7)) / (k4 + 1e-7);
    phillips *= wHat * wHat;
    phillips *= exp(-k2 * g_MinWavelength * g_MinWavelength); // 消除高频
    // 消散
    phillips *= g_PhillipsDissipation;
    // 波浪方向反向 (背风) 抑制
    phillips *= (wHat < 0.0) ? 0.01 : 1.0;

    // 高斯随机数 (实部/虚部)
    float2 gauss = float2(GaussianRandom(float2(x, y)), GaussianRandom(float2(y + 0.5, x)));

    // h0(k) = sqrt(P(k)/2) * (gaussReal + i*gaussImag)
    float sqrtP = sqrt(phillips * 0.5);
    float2 h0 = gauss * sqrtP;

    g_SpectrumH0UAV[uint2(x, y)] = h0;
    // h0*(-k): 共轭, 注意 -k 对应索引 (N-x, N-y)
    // 由于 IFFT 周期性, 存储共轭以便时间演化
    g_SpectrumH0ConjUAV[uint2(x, y)] = float2(h0.x, -h0.y);
}

// ════════════════════════════════════════════════════════════════════════════
// UpdateSpectrumCS - 时间演化: h(k,t) = h0(k)*exp(i*w*t) + h0*(-k)*exp(-i*w*t)
// 色散关系 w(k) = sqrt(g * |k|)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void UpdateSpectrumCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_FFTSize || y >= (uint)g_FFTSize) return;

    int N = g_FFTSize;
    float2 k;
    k.x = SHANYU_TWO_PI * (x - N * 0.5) / g_PatchSize;
    k.y = SHANYU_TWO_PI * (y - N * 0.5) / g_PatchSize;

    float kLen = length(k);
    float omega = sqrt(g_Gravity * kLen); // 深水色散关系
    float wt = omega * g_FFTTime;

    // 复数指数 exp(i*wt) = (cos(wt), sin(wt))
    float2 expPos = float2(cos(wt), sin(wt));
    float2 expNeg = float2(cos(-wt), sin(-wt));

    // h0(k) 与共轭频谱 (索引对应 -k)
    // -k 的索引为 (N - x) mod N
    uint conjX = (N - x) % N;
    uint conjY = (N - y) % N;
    float2 h0 = g_SpectrumH0SRV[uint2(x, y)];
    float2 h0Conj = g_SpectrumH0ConjSRV[uint2(conjX, conjY)];

    // 复数乘法: (a+bi)*(c+di) = (ac-bd) + (ad+bc)i
    float2 term1 = float2(h0.x * expPos.x - h0.y * expPos.y,
                           h0.x * expPos.y + h0.y * expPos.x);
    float2 term2 = float2(h0Conj.x * expNeg.x - h0Conj.y * expNeg.y,
                           h0Conj.x * expNeg.y + h0Conj.y * expNeg.x);

    float2 ht = term1 + term2;
    g_SpectrumHTUAV[uint2(x, y)] = ht;
}

// ════════════════════════════════════════════════════════════════════════════
// ButterflyCS - Stockham 蝶形运算 (单阶段)
// 实现 1D FFT 的一个蝶形阶段, 水平/垂直方向分别调用
// ═══─────────────────────────────────────────────────────────────────────────
// 复数乘法辅助
float2 cmul(float2 a, float2 b)
{
    return float2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x);
}

[numthreads(8, 8, 1)]
void ButterflyCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_BF_FFTSize || y >= (uint)g_BF_FFTSize) return;

    int N = g_BF_FFTSize;
    int stage = g_BF_Stage;
    int dir = g_BF_Direction;

    // 当前阶段的蝶形跨度
    int halfSize = 1 << stage;       // 蝶形跨度的一半
    int fullSize = halfSize << 1;     // 完整跨度

    // 确定沿哪个轴进行蝶形运算
    uint idx = (dir == 0) ? x : y;
    uint other = (dir == 0) ? y : x;

    // 计算蝶形配对
    int group = idx / fullSize;
    int posInGroup = idx % fullSize;
    int idxA = group * fullSize + posInGroup;
    int idxB = (posInGroup < halfSize) ? idxA + halfSize : idxA - halfSize;

    // 旋转因子
    int k = (posInGroup < halfSize) ? posInGroup : (posInGroup - halfSize);
    float angle = SHANYU_TWO_PI * k / fullSize;
    // 逆 FFT: 使用正号 (exp(+i*2pi*k/N))
    float2 twiddle = float2(cos(angle), sin(angle));

    // 读取配对 (沿轴索引)
    float2 valA = (dir == 0) ? g_FFTBufferA[uint2(idxA, other)] : g_FFTBufferA[uint2(other, idxA)];
    float2 valB = (dir == 0) ? g_FFTBufferA[uint2(idxB, other)] : g_FFTBufferA[uint2(other, idxB)];

    // 蝶形运算
    float2 t = cmul(twiddle, valB);
    float2 outA = valA + t;
    float2 outB = valA - t;

    // 写回 (乒乓缓冲: 写到 BufferB)
    if (dir == 0)
    {
        g_FFTBufferB[uint2(idxA, other)] = outA;
        g_FFTBufferB[uint2(idxB, other)] = outB;
    }
    else
    {
        g_FFTBufferB[uint2(other, idxA)] = outA;
        g_FFTBufferB[uint2(other, idxB)] = outB;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// InverseTransformCS - 将 IFFT 结果转换为位移场 + 法线 + 雅可比
// 输出: DisplacementMap(XYZ位移, W=雅可比 J), NormalJacobMap(XYZ法线, W=J)
// 雅可比用于检测波浪翻转 (J < 0) 产生泡沫
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void InverseTransformCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_FFTSize || y >= (uint)g_FFTSize) return;

    int N = g_FFTSize;
    // IFFT 缩放因子 1/N^2
    float invN2 = 1.0 / (N * N);

    // 中心采样以消除 IFFT 的循环移位
    // 读取高度 (实部) 与水平位移
    float height = g_HeightFieldSRV[uint2(x, y)].x * invN2;

    // 邻居采样用于计算梯度 (有限差分)
    uint xL = (x - 1 + N) % N;
    uint xR = (x + 1) % N;
    uint yD = (y - 1 + N) % N;
    uint yU = (y + 1) % N;

    float hL = g_HeightFieldSRV[uint2(xL, y)].x * invN2;
    float hR = g_HeightFieldSRV[uint2(xR, y)].x * invN2;
    float hD = g_HeightFieldSRV[uint2(x, yD)].x * invN2;
    float hU = g_HeightFieldSRV[uint2(x, yU)].x * invN2;

    // 高度梯度
    float dhdx = (hR - hL) * 0.5;
    float dhdz = (hU - hD) * 0.5;

    // Choppy 水平位移 (Gerstner 风格): 位移与梯度反向
    float dispX = -dhdx * g_Choppiness;
    float dispZ = -dhdz * g_Choppiness;
    float dispY = height;

    // 法线 (从梯度)
    float3 normal = normalize(float3(-dhdx, 1.0, -dhdz));

    // 雅可比行列式 J = (1 + dDx/dx)(1 + dDz/dz) - (dDx/dz)(dDz/dx)
    // D = (dispX, dispZ), 简化用二阶差分
    float dDxdx = (hR - 2.0 * height + hL);
    float dDzdz = (hU - 2.0 * height + hD);
    float jacobian = (1.0 - g_Choppiness * dDxdx) * (1.0 - g_Choppiness * dDzdz)
                   - g_Choppiness * g_Choppiness * dhdx * dhdz;

    g_DisplacementMap[uint2(x, y)] = float4(dispX, dispY, dispZ, jacobian);
    g_NormalJacobMap[uint2(x, y)] = float4(normal, jacobian);
}

// ════════════════════════════════════════════════════════════════════════════
// RippleImpulseCS - 注入涟漪脉冲 (物体入水/移动)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void RippleImpulseCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_RI_GridSize || y >= (uint)g_RI_GridSize) return;

    // 当前单元格世界位置
    float2 worldPos = float2(x, y) * g_RI_CellSize;

    float impulse = 0.0;
    // 遍历所有脉冲 (假设最多 16 个, 缓冲区以 (0,0,0,0) 结尾)
    [unroll]
    for (int i = 0; i < 16; i++)
    {
        float4 pulse = g_RippleImpulseBuf[i];
        if (pulse.w <= 0.0 && pulse.z <= 0.0) break; // 空脉冲
        float2 pulsePos = pulse.xy;
        float strength = pulse.z;
        float radius = pulse.w;

        float dist = length(worldPos - pulsePos);
        // 高斯衰减
        float falloff = exp(-dist * dist / (radius * radius + 1e-6));
        impulse += strength * falloff;
    }

    // 累加到当前高度场
    float current = g_RippleHeightUAV[uint2(x, y)];
    g_RippleHeightUAV[uint2(x, y)] = current + impulse;
}

// ════════════════════════════════════════════════════════════════════════════
// RippleUpdateCS - 2D 波动方程传播
// u_tt = c^2 * (u_xx + u_zz), 离散: u_new = 2*u - u_prev + c^2*dt^2*laplacian
// ═════════════════════════════════════════════════─────────────────────────────
[numthreads(8, 8, 1)]
void RippleUpdateCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_RP_GridSize || y >= (uint)g_RP_GridSize) return;

    int N = g_RP_GridSize;
    float current = g_RippleHeightSRV[uint2(x, y)];
    float prev = g_RipplePrevHeightSRV[uint2(x, y)];

    // 邻居 (环绕)
    float nL = g_RippleHeightSRV[uint2((x - 1 + N) % N, y)];
    float nR = g_RippleHeightSRV[uint2((x + 1) % N, y)];
    float nD = g_RippleHeightSRV[uint2(x, (y - 1 + N) % N)];
    float nU = g_RippleHeightSRV[uint2(x, (y + 1) % N)];

    // 拉普拉斯算子
    float laplacian = (nL + nR + nD + nU) * 0.25 - current;

    // 波速
    float c = g_RP_WaveSpeed * g_RP_DeltaTime / g_RP_CellSize;
    float c2 = c * c;

    // 波动方程更新
    float newHeight = (2.0 * current - prev + c2 * laplacian) * g_RP_Damping;

    g_RippleHeightUAV[uint2(x, y)] = newHeight;
}

// ════════════════════════════════════════════════════════════════════════════
// RippleNormalCS - 从涟漪高度场计算法线
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void RippleNormalCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_RP_GridSize || y >= (uint)g_RP_GridSize) return;

    int N = g_RP_GridSize;
    float h = g_RippleHeightSRV[uint2(x, y)];
    float hL = g_RippleHeightSRV[uint2((x - 1 + N) % N, y)];
    float hR = g_RippleHeightSRV[uint2((x + 1) % N, y)];
    float hD = g_RippleHeightSRV[uint2(x, (y - 1 + N) % N)];
    float hU = g_RippleHeightSRV[uint2(x, (y + 1) % N)];

    float dhdx = (hR - hL) * 0.5 / g_RP_CellSize;
    float dhdz = (hU - hD) * 0.5 / g_RP_CellSize;

    float3 normal = normalize(float3(-dhdx, 1.0, -dhdz));
    g_RippleNormalUAV[uint2(x, y)] = float4(normal, h);
}

// ════════════════════════════════════════════════════════════════════════════
// FoamUpdateCS - 泡沫动态生成与消散
// 基于涟漪高度变化率与阈值产生泡沫, 随时间衰减
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void FoamUpdateCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_FM_GridSize || y >= (uint)g_FM_GridSize) return;

    // 涟漪高度 (作为泡沫源)
    float rippleHeight = abs(g_RippleFoamSourceSRV[uint2(x, y)]);

    // 当前泡沫量
    float currentFoam = g_FoamMapSRV[uint2(x, y)];

    // 当波高超过阈值时生成泡沫
    float newFoam = smoothstep(g_FM_FoamThreshold, g_FM_FoamThreshold * 2.0, rippleHeight);
    newFoam *= g_FM_FoamIntensity;

    // 累加并衰减
    float foam = max(currentFoam, newFoam);
    foam *= exp(-g_FM_Dissipation * g_FM_DeltaTime);
    foam = saturate(foam);

    g_FoamMapUAV[uint2(x, y)] = foam;
}

// ════════════════════════════════════════════════════════════════════════════
// WetnessUpdateCS - 湿润痕迹更新 (物体经过留下的湿润区域, 随时间干燥)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void WetnessUpdateCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint x = dispatchID.x;
    uint y = dispatchID.y;
    if (x >= (uint)g_WT_GridSize || y >= (uint)g_WT_GridSize) return;

    // 当前湿润度
    float currentWetness = g_WetnessMapUAV[uint2(x, y)];

    // 遍历脉冲位置 (湿润痕迹来源)
    float addedWetness = 0.0;
    float2 worldPos = float2(x, y) * g_WT_CellSize;
    [unroll]
    for (int i = 0; i < 16; i++)
    {
        float4 pulse = g_RippleImpulseBuf[i];
        if (pulse.w <= 0.0 && pulse.z <= 0.0) break;
        float2 pulsePos = pulse.xy;
        float radius = max(pulse.w, 1.0);
        float dist = length(worldPos - pulsePos);
        float falloff = exp(-dist * dist / (radius * radius));
        addedWetness += falloff * g_WT_WetnessStrength;
    }

    // 累加湿润度并干燥
    float wetness = max(currentWetness, addedWetness);
    wetness *= exp(-g_WT_DryRate * g_WT_DeltaTime);
    wetness = saturate(wetness);

    g_WetnessMapUAV[uint2(x, y)] = wetness;
}
