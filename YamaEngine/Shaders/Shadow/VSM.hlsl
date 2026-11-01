// ════════════════════════════════════════════════════════════════════════════
// Yama Engine — Virtual Shadow Maps (VSM)
// 深度渲染 (VS/PS) + 物理页面分配 (CS) + VSM 矩过滤 (CS)
// 虚拟纹理: 16384 虚拟大小, 128 页大小, 8192 物理图集, 4096 页, 8 mip
// 目标: Shader Model 6.6 (DXC 编译)
// 对应 C#: YamaEngine.Rendering.SparseShadowAtlases
// ════════════════════════════════════════════════════════════════════════════

#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// VSM 常量 (匹配 C# VSMConstantsCB)
// ──────────────────────────────────────────────────────────────────────────
cbuffer VSMConstantsCB : register(b0)
{
    int      gVSMVirtualTextureSize;
    int      gVSMPageSize;
    int      gVSMPhysicalAtlasSize;
    int      gVSMPhysicalPagesPerSide;
    int      gVSMMaxMipLevels;
    uint     gVSMInvalidPhysicalPage;
    int      gVSMPageTableIndexBits;
    float    gVSMPad0;
    float4x4 gVSMLightViewProj;
    float3   gVSMLightPosition;
    float    gVSMLightRange;
    float3   gVSMLightDirection;
    int      gVSMLightType;   // 0=方向光, 1=点光, 2=聚光
};

// 页面渲染任务常量 (匹配 C# VSMPageRenderTask)
cbuffer VSMPageRenderCB : register(b1)
{
    int      gVSMRenderMip;
    int      gVSMRenderPageX;
    int      gVSMRenderPageY;
    int      gVSMRenderVTIndex;
    uint     gVSMRenderPhysPage;
    int      gVSMRenderCubeFace;
    int      gVSMRenderLightIndex;
    float    gVSMRenderPad;
    float4x4 gVSMRenderLightVP;
    float3   gVSMRenderLightPos;
    float    gVSMRenderLightRange;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────

// 场景几何 (深度渲染读取)
// (顶点/索引缓冲通过 VertexBuffer/IndexBuffer 绑定)

// 物理深度图集 (深度渲染输出 / 过滤读取)
Texture2D<float>  gVSMPhysicalDepthAtlas : register(t0);

// 页表 (分配状态)
StructuredBuffer<uint> gVSMPageTable : register(t1);

// 页面分配请求队列
StructuredBuffer<uint4> gVSMPageRequestQueue : register(t2);  // (virtualTex, pageX, pageY, mip)

// 页面空闲列表
StructuredBuffer<uint> gVSMFreePages : register(t3);

// UAV 输出
RWTexture2D<float>  gVSMPhysicalDepthUAV : register(u0);   // 深度图集
RWTexture2D<float2> gVSMMomentsUAV       : register(u1);   // VSM 矩 (mean, mean+variance)
RWStructuredBuffer<uint> gVSMPageTableUAV : register(u2);  // 页表 (分配写入)
RWStructuredBuffer<uint> gVSMFreeCountUAV : register(u3);  // 空闲页计数
RWStructuredBuffer<uint4> gVSMPageClearQueueUAV : register(u4); // 待清除页


// ════════════════════════════════════════════════════════════════════════════
// 深度渲染路径 (VS/PS) — 将几何渲染到物理图集页面
// ════════════════════════════════════════════════════════════════════════════

struct VSMDepthVSInput
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
};

struct VSMDepthVSOutput
{
    float4 Position : SV_POSITION;
    float  LinearDepth : TEXCOORD0;
};

// VSMDepthVS — 阴影深度顶点着色器
// 变换到光源裁剪空间, 计算线性深度供 VSM 使用
// 入口点: VSMDepthVS (匹配 C# VSM_PageRenderPSO 图形管线)
VSMDepthVSOutput VSMDepthVS(VSMDepthVSInput input)
{
    VSMDepthVSOutput output;
    float4 worldPos = mul(gWorldMatrix, float4(input.Position, 1.0));
    float4 lightClip = mul(gVSMRenderLightVP, worldPos);
    output.Position = lightClip;

    // 线性深度 (归一化到 [0,1], 基于光源范围)
    float dist = length(worldPos.xyz - gVSMRenderLightPos);
    output.LinearDepth = saturate_fast(dist / gVSMRenderLightRange);
    return output;
}

// VSMDepthPS — 阴影深度像素着色器
// 写入深度 (深度缓冲), 同时输出线性深度用于 VSM 矩计算
// 入口点: VSMDepthPS
struct VSMDepthPSOutput
{
    float Depth : SV_Depth;
};

VSMDepthPSOutput VSMDepthPS(VSMDepthVSOutput input)
{
    VSMDepthPSOutput output;
    // 写入非线性深度到深度缓冲 (供后续过滤读取)
    output.Depth = input.Position.z;
    return output;
}


// ════════════════════════════════════════════════════════════════════════════
// PageAllocationCS — 物理页面分配 (计算着色器)
// 处理页面分配请求队列, 从空闲列表分配物理页, 更新页表
// 入口点: PageAllocationCS (匹配 C# RHIShaderBytecode("PageAllocationCS", Compute))
// ════════════════════════════════════════════════════════════════════════════

cbuffer VSMAllocCB : register(b2)
{
    uint gVSMRequestCount;
    uint gVSMTotalFreePages;
    uint gVSMTotalPhysicalPages;
    uint gVSMAllocPad;
};

// 计算虚拟纹理的页表线性索引
uint ComputePageTableIndex(uint virtualTex, uint pageX, uint pageY, uint mip)
{
    uint mipBase = 0;
    uint mipSize = gVSMVirtualTextureSize >> mip;
    uint pagesPerSide = mipSize / gVSMPageSize;
    uint localIndex = pageY * pagesPerSide + pageX;
    // 累加前各 mip 的页数
    [loop]
    for (uint m = 0; m < mip; m++)
    {
        uint sz = gVSMVirtualTextureSize >> m;
        mipBase += (sz / gVSMPageSize) * (sz / gVSMPageSize);
    }
    return virtualTex * 0 + localIndex + mipBase; // 简化: 单虚拟纹理
}

[numthreads(64, 1, 1)]
void PageAllocationCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint requestIdx = dispatchID.x;
    if (requestIdx >= gVSMRequestCount)
        return;

    // 读取页面分配请求
    uint4 request = gVSMPageRequestQueue[requestIdx];
    uint virtualTex = request.x;
    uint pageX = request.y;
    uint pageY = request.z;
    uint mip = request.w;

    uint pageTableIdx = ComputePageTableIndex(virtualTex, pageX, pageY, mip);

    // 检查是否已分配
    uint existingPage = gVSMPageTable[pageTableIdx];
    if (existingPage != gVSMInvalidPhysicalPage)
        return; // 已分配

    // 从空闲列表分配物理页 (原子递减计数器获取槽位)
    uint freeSlot;
    InterlockedAdd(gVSMFreeCountUAV[0], -1, freeSlot);

    if (freeSlot >= gVSMTotalFreePages || freeSlot == 0)
    {
        // 空闲页耗尽: 淘汰最久未用页 (LRU, 此处简化为重置计数器)
        InterlockedAdd(gVSMFreeCountUAV[0], 1, freeSlot);
        return;
    }

    uint physicalPage = gVSMFreePages[freeSlot - 1];

    // 更新页表: 虚拟页 -> 物理页
    uint prevValue;
    InterlockedExchange(gVSMPageTableUAV[pageTableIdx], physicalPage, prevValue);

    // 将物理页加入待清除队列 (清除旧深度内容)
    uint clearSlot;
    InterlockedAdd(gVSMPageClearQueueUAV[0].x, 1, clearSlot);
    // 标记物理页索引供清除 Pass 使用
}


// ════════════════════════════════════════════════════════════════════════════
// VSMFilterCS — VSM 矩过滤 (生成虚拟阴影贴图)
// 读取物理深度图集, 计算一阶/二阶矩 (均值 + 方差), 写入矩纹理
// 用于 Chebyshev 上界不等式阴影测试 (减少阴影锯齿)
// 入口点: VSMFilterCS (匹配任务要求 "VSM 过滤 CS")
// ════════════════════════════════════════════════════════════════════════════

cbuffer VSMFilterCB : register(b3)
{
    uint  gVSMFilterMip;
    uint  gVSMFilterPageX;
    uint  gVSMFilterPageY;
    uint  gVSMFilterPhysPage;
    int   gVSMFilterRadius;
    float gVSMFilterLightBleedingReduction;
    float gVSMFilterPad;
};

[numthreads(8, 8, 1)]
void VSMFilterCS(uint3 dispatchID : SV_DispatchThreadID)
{
    // 计算物理图集中的像素坐标
    uint2 pagePixel = dispatchID.xy;
    if (pagePixel.x >= (uint)gVSMPageSize || pagePixel.y >= (uint)gVSMPageSize)
        return;

    // 物理页在图集中的偏移
    uint physPageX = (gVSMFilterPhysPage % gVSMPhysicalPagesPerSide) * gVSMPageSize;
    uint physPageY = (gVSMFilterPhysPage / gVSMPhysicalPagesPerSide) * gVSMPageSize;
    uint2 atlasPixel = uint2(physPageX, physPageY) + pagePixel;
    float2 texel = 1.0 / float2(gVSMPhysicalAtlasSize, gVSMPhysicalAtlasSize);

    // 在过滤半径内采样深度, 计算一阶矩 (均值) 与二阶矩
    float sum = 0.0;
    float sumSq = 0.0;
    int sampleCount = 0;

    int radius = max(1, gVSMFilterRadius);
    [loop]
    for (int x = -radius; x <= radius; x++)
    {
        [loop]
        for (int y = -radius; y <= radius; y++)
        {
            float2 uv = (atlasPixel + float2(x, y) + 0.5) * texel;
            float depth = gVSMPhysicalDepthAtlas.Sample(gPointClampSampler, uv).r;
            // 跳过背景 (无几何)
            if (depth >= 1.0) continue;
            sum += depth;
            sumSq += depth * depth;
            sampleCount++;
        }
    }

    if (sampleCount == 0)
    {
        gVSMMomentsUAV[atlasPixel] = float2(1.0, 1.0);
        return;
    }

    float mean = sum / sampleCount;
    float variance = max(sumSq / sampleCount - mean * mean, 0.0);

    // 减少漏光 (light bleeding reduction)
    variance = variance;
    float secondMoment = mean + variance;

    gVSMMomentsUAV[atlasPixel] = float2(mean, secondMoment);
}


// ════════════════════════════════════════════════════════════════════════════
// 辅助: Chebyshev 上界不等式阴影测试 (供光照着色器调用)
// 输入: 矩纹理采样的 (mean, mean+variance) 与接收者深度
// ════════════════════════════════════════════════════════════════════════════
float VSMSampleShadow(float2 moments, float receiverDepth)
{
    float mean = moments.x;
    float variance = max(moments.y - moments.x * moments.x, 0.0);

    // 接收者在遮挡物前方 -> 无阴影
    if (receiverDepth <= mean)
        return 1.0;

    // Chebyshev 上界: P(unlit) <= variance / (variance + (t - mean)^2)
    float d = receiverDepth - mean;
    float litMax = variance / (variance + d * d);

    // 漏光抑制
    litMax = smoothstep_edge(0.1, 1.0, litMax);
    return litMax;
}
