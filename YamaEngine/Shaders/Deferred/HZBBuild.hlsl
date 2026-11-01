// ════════════════════════════════════════════════════════════════════════════
// HZB (Hierarchical Z-Buffer) 构建 — 层级深度缓冲生成
// 用于: 屏幕空间反射/遮蔽的光线步进加速、MicroCluster 簇剔除、遮挡剔除
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

Texture2D<float> gSrcDepth : register(t0);
RWTexture2D<float> gDstDepth : register(u0);

cbuffer HZBConstants : register(b2)
{
    uint2 gSrcSize;
    uint2 gDstSize;
    uint mMipLevel;
    uint gPad0;
    uint gPad1;
    uint gPad2;
};

[numthreads(8, 8, 1)]
void HZBBuildCS(uint3 dtid : SV_DispatchThreadID)
{
    if (dtid.x >= gDstSize.x || dtid.y >= gDstSize.y) return;

    // 2x2 降采样: 取最远深度 (最大值, 因为深度是 0=near, 1=far)
    uint2 samplePos = dtid.xy * 2;
    float d00 = gSrcDepth.Load(int3(samplePos, 0));
    float d10 = gSrcDepth.Load(int3(samplePos + uint2(1, 0), 0));
    float d01 = gSrcDepth.Load(int3(samplePos + uint2(0, 1), 0));
    float d11 = gSrcDepth.Load(int3(samplePos + uint2(1, 1), 0));

    // 取最大深度 (保守, 确保 HZB 不误剔除)
    float maxDepth = max(max(d00, d10), max(d01, d11));
    gDstDepth[dtid.xy] = maxDepth;
}
