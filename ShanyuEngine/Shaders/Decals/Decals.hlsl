// ════════════════════════════════════════════════════════════════════════════
// Shanyu Engine - Deferred Decal System
// 延迟贴花: 渲染贴花包围盒 (AABB), 在像素阶段从 GeometryBuffer 深度重建世界坐标,
// 将世界坐标变换到贴花局部空间进行 UV 投影, 采样贴花纹理并混合到 GeometryBuffer。
// 支持法线/粗糙度/自发光覆盖, 角度衰减 (贴花不贴在掠射角表面),
// 双面投影与裁剪。
// 与 C# DecalSystem.cs / HybridLightingRenderer.cs (DecalCS) 对应。
// Shader Model 6.6 (DXC)
// 入口点: DecalVS, DecalPS (图形管线), DecalCS (计算管线)
// ════════════════════════════════════════════════════════════════════════════
#include "../Common.hlsli"

// ──────────────────────────────────────────────────────────────────────────
// 常量缓冲 (与 C# DecalInstanceConstants 对应)
// ──────────────────────────────────────────────────────────────────────────
cbuffer DecalConstantsCB : register(b2)
{
    float4x4 g_Decal_WorldMatrix;       // 贴花世界矩阵 (含缩放=包围盒大小)
    float4x4 g_Decal_InvWorldMatrix;    // 世界->贴花局部 (用于投影)
    float4x4 g_Decal_ViewProjMatrix;    // 相机视图投影
    float4   g_Decal_Color;             // 贴花颜色乘数 (RGBA)
    float    g_Decal_NormalBlend;       // 法线混合强度
    float    g_Decal_RoughnessBlend;    // 粗糙度混合强度
    float    g_Decal_MetallicBlend;     // 金属度混合强度
    float    g_Decal_EmissiveBlend;     // 自发光混合强度
    float    g_Decal_Opacity;           // 整体不透明度
    float    g_Decal_AngleFade;         // 角度衰减阈值 (与表面法线夹角)
    int      g_Decal_BlendMode;         // 混合模式: 0=透明, 1=加法, 2=乘法
    int      g_Decal_TwoSided;          // 双面投影
    float    g_Decal_Pad0;
    float    g_Decal_Pad1;
};

// 贴花数组常量 (用于 DecalCS 批量处理)
cbuffer DecalArrayCB : register(b3)
{
    uint  g_DecalArray_Count;
    float g_DecalArray_Pad0;
    float g_DecalArray_Pad1;
    float g_DecalArray_Pad2;
};

// ──────────────────────────────────────────────────────────────────────────
// 资源绑定
// ──────────────────────────────────────────────────────────────────────────
Texture2D<float4> g_DecalAlbedoSRV    : register(t0); // 贴花反照率 (含 alpha)
Texture2D<float3> g_DecalNormalSRV    : register(t1); // 贴花法线 (切线空间)
Texture2D<float4> g_DecalORMSRV       : register(t2); // 贴花 ORM (AO/Roughness/Metallic)
Texture2D<float3> g_DecalEmissiveSRV  : register(t3); // 贴花自发光

Texture2D<float>  g_GeometryBufferDepthSRV   : register(t4); // GeometryBuffer 深度
Texture2D<float4> g_GeometryBufferAlbedoSRV  : register(t5); // GeometryBuffer 反照率
Texture2D<float4> g_GeometryBufferNormalSRV  : register(t6); // GeometryBuffer 法线 (编码)
Texture2D<float4> g_GeometryBufferORMSRV     : register(t7); // GeometryBuffer ORM
Texture2D<float3> g_GeometryBufferEmissiveSRV: register(t8); // GeometryBuffer 自发光

RWTexture2D<float4> g_DecalAlbedoUAV  : register(u0); // 输出: 修改后的 GeometryBuffer 反照率
RWTexture2D<float4> g_DecalNormalUAV  : register(u1); // 输出: 修改后的 GeometryBuffer 法线
RWTexture2D<float4> g_DecalORMUAV     : register(u2); // 输出: 修改后的 GeometryBuffer ORM
RWTexture2D<float3> g_DecalEmissiveUAV: register(u3); // 输出: 修改后的 GeometryBuffer 自发光

// ──────────────────────────────────────────────────────────────────────────
// 顶点/像素结构
// ──────────────────────────────────────────────────────────────────────────
// 贴花包围盒顶点 (单位立方体 -1..1)
struct DecalVSInput
{
    float3 Position : POSITION;
};

struct DecalVSOutput
{
    float4 Position : SV_POSITION;
    float4 ScreenPos: TEXCOORD0; // 屏幕空间位置 (用于深度重建)
    float3 LocalPos : TEXCOORD1; // 贴花局部空间位置 (用于投影 UV)
};

// ──────────────────────────────────────────────────────────────────────────
// 工具函数
// ──────────────────────────────────────────────────────────────────────────

// 从贴花局部位置计算投影 UV
// 局部位置范围 -0.5..0.5, 映射到 UV 0..1
float2 DecalLocalToUV(float3 localPos)
{
    return localPos.xy + 0.5;
}

// 检查局部位置是否在贴花范围内
bool IsInsideDecal(float3 localPos)
{
    return all(abs(localPos) <= 0.5);
}

// 角度衰减: 贴花法线 (局部 Z) 与表面法线夹角过大时淡出
float AngleFade(float3 surfaceNormal, float3 decalForward, float threshold)
{
    float cosAngle = dot(surfaceNormal, -decalForward);
    return smoothstep(threshold, threshold + 0.2, cosAngle);
}

// 切线空间法线转换到世界空间
float3 TangentToWorld(float3 tangentNormal, float3 worldNormal)
{
    float3 up = abs(worldNormal.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 tangent = normalize(cross(up, worldNormal));
    float3 bitangent = cross(worldNormal, tangent);
    return normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + worldNormal * tangentNormal.z);
}

// 混合模式应用
float3 ApplyBlendMode(float3 dst, float3 src, float alpha, int mode)
{
    if (mode == 0) return lerp(dst, src, alpha);       // 透明
    if (mode == 1) return dst + src * alpha;            // 加法
    if (mode == 2) return dst * lerp(1.0, src, alpha);  // 乘法
    return lerp(dst, src, alpha);
}

// ════════════════════════════════════════════════════════════════════════════
// DecalVS - 贴花包围盒顶点着色器
// 变换单位立方体到世界/裁剪空间, 传递局部位置用于投影
// ════════════════════════════════════════════════════════════════════════════
DecalVSOutput DecalVS(DecalVSInput input)
{
    DecalVSOutput output;

    // 世界位置
    float3 worldPos = mul(g_Decal_WorldMatrix, float4(input.Position, 1.0)).xyz;

    // 裁剪空间
    output.Position = mul(g_Decal_ViewProjMatrix, float4(worldPos, 1.0));
    output.ScreenPos = output.Position;

    // 贴花局部位置 (用于投影 UV, 范围 -0.5..0.5)
    output.LocalPos = input.Position * 0.5;

    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// DecalPS - 贴花像素着色器 (延迟路径)
// 从 GeometryBuffer 深度重建世界坐标, 投影到贴花局部空间采样, 混合回 GeometryBuffer
// 输出: 修改后的 GeometryBuffer 属性 (通过 MRT)
// ════════════════════════════════════════════════════════════════════════════
struct DecalPSOutput
{
    float4 Albedo   : SV_TARGET0;
    float4 Normal   : SV_TARGET1;
    float4 ORM      : SV_TARGET2;
    float3 Emissive : SV_TARGET3;
};

DecalPSOutput DecalPS(DecalVSOutput input)
{
    DecalPSOutput output;
    // 初始化为当前 GeometryBuffer 值 (不修改的像素保持原样)
    int2 pixel = input.Position.xy;
    float2 uv = (pixel + 0.5) / gScreenParams.xy;

    output.Albedo = g_GeometryBufferAlbedoSRV.Load(int3(pixel, 0));
    output.Normal = g_GeometryBufferNormalSRV.Load(int3(pixel, 0));
    output.ORM = g_GeometryBufferORMSRV.Load(int3(pixel, 0));
    output.Emissive = g_GeometryBufferEmissiveSRV.Load(int3(pixel, 0));

    // ── 从 GeometryBuffer 深度重建世界坐标 ──
    float depth = g_GeometryBufferDepthSRV.Load(int3(pixel, 0));
    if (depth >= 1.0) discard; // 天空像素, 不贴花

    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;
    float4 clipPos = float4(ndc, depth, 1.0);
    float4 viewPos = mul(gInvProjMatrix, clipPos);
    viewPos /= viewPos.w;
    float4 worldPos = mul(gInvViewMatrix, viewPos);

    // ── 变换到贴花局部空间 ──
    float4 localPos = mul(g_Decal_InvWorldMatrix, worldPos);

    // 检查是否在贴花包围盒内
    if (!IsInsideDecal(localPos.xyz * 2.0)) // *2 因为局部范围是 -0.5..0.5
        discard;

    float3 decalLocal = localPos.xyz; // -0.5..0.5

    // ── 计算 UV ──
    float2 decalUV = DecalLocalToUV(decalLocal);

    // 翻转 Y (纹理坐标习惯)
    decalUV.y = 1.0 - decalUV.y;

    // ── 采样贴花纹理 ──
    float4 decalAlbedo = g_DecalAlbedoSRV.Sample(gLinearWrapSampler, decalUV);
    float3 decalNormalTS = g_DecalNormalSRV.Sample(gLinearWrapSampler, decalUV) * 2.0 - 1.0;
    float4 decalORM = g_DecalORMSRV.Sample(gLinearWrapSampler, decalUV);
    float3 decalEmissive = g_DecalEmissiveSRV.Sample(gLinearWrapSampler, decalUV);

    // 贴花 alpha (整体不透明度)
    float decalAlpha = decalAlbedo.a * g_Decal_Opacity * g_Decal_Color.a;
    if (decalAlpha < 0.01) discard;

    // ── 角度衰减 ──
    // 读取 GeometryBuffer 表面法线
    float3 surfaceNormal = DecodeNormalOctahedral(g_GeometryBufferNormalSRV.Load(int3(pixel, 0)).xy);
    // 贴花朝向 (世界矩阵的 Z 轴)
    float3 decalForward = normalize(mul((float3x3)g_Decal_WorldMatrix, float3(0, 0, 1)));
    float angleFade = AngleFade(surfaceNormal, decalForward, g_Decal_AngleFade);

    // 双面投影
    if (g_Decal_TwoSided != 0)
    {
        // 若表面背向贴花, 翻转贴花法线
        if (dot(surfaceNormal, decalForward) > 0.0)
        {
            decalNormalTS.z = -decalNormalTS.z;
        }
    }

    decalAlpha *= angleFade;
    if (decalAlpha < 0.01) discard;

    // ── 将贴花法线转换到世界空间 ──
    float3 decalNormalWS = TangentToWorld(decalNormalTS, surfaceNormal);

    // ── 混合到 GeometryBuffer ──
    // 反照率
    float3 dstAlbedo = output.Albedo.rgb;
    float3 srcAlbedo = decalAlbedo.rgb * g_Decal_Color.rgb;
    output.Albedo.rgb = ApplyBlendMode(dstAlbedo, srcAlbedo, decalAlpha, g_Decal_BlendMode);
    output.Albedo.a = 1.0;

    // 法线 (混合)
    float3 blendedNormal = normalize(lerp(surfaceNormal, decalNormalWS, g_Decal_NormalBlend * decalAlpha));
    output.Normal = float4(EncodeNormalOctahedral(blendedNormal), output.Normal.zw);

    // ORM (粗糙度/金属度混合)
    float3 dstORM = output.ORM.rgb;
    float3 srcORM = decalORM.gba; // R=AO, G=Roughness, B=Metallic
    output.ORM.r = lerp(dstORM.r, srcORM.r, decalAlpha); // AO
    output.ORM.g = lerp(dstORM.g, srcORM.g, g_Decal_RoughnessBlend * decalAlpha); // Roughness
    output.ORM.b = lerp(dstORM.b, srcORM.b, g_Decal_MetallicBlend * decalAlpha); // Metallic
    output.ORM.a = 1.0;

    // 自发光
    float3 dstEmissive = output.Emissive;
    float3 srcEmissive = decalEmissive * g_Decal_Color.rgb;
    output.Emissive = lerp(dstEmissive, srcEmissive, g_Decal_EmissiveBlend * decalAlpha);

    return output;
}

// ════════════════════════════════════════════════════════════════════════════
// DecalCS - 计算着色器路径 (延迟贴花批量应用)
// 每像素遍历影响该像素的贴花列表, 累积混合到 GeometryBuffer UAV
// (HybridLightingRenderer.DecalCS 入口)
// ════════════════════════════════════════════════════════════════════════════
[numthreads(8, 8, 1)]
void DecalCS(uint3 dispatchID : SV_DispatchThreadID)
{
    uint w, h;
    g_DecalAlbedoUAV.GetDimensions(w, h);
    if (dispatchID.x >= w || dispatchID.y >= h) return;

    int2 pixel = dispatchID.xy;

    // 读取当前 GeometryBuffer (作为混合起点)
    float4 albedo = g_GeometryBufferAlbedoSRV.Load(int3(pixel, 0));
    float4 normalEncoded = g_GeometryBufferNormalSRV.Load(int3(pixel, 0));
    float4 orm = g_GeometryBufferORMSRV.Load(int3(pixel, 0));
    float3 emissive = g_GeometryBufferEmissiveSRV.Load(int3(pixel, 0));

    // 从深度重建世界坐标
    float depth = g_GeometryBufferDepthSRV.Load(int3(pixel, 0));
    if (depth >= 1.0)
    {
        // 天空: 直接写回原值
        g_DecalAlbedoUAV[pixel] = albedo;
        g_DecalNormalUAV[pixel] = normalEncoded;
        g_DecalORMUAV[pixel] = orm;
        g_DecalEmissiveUAV[pixel] = emissive;
        return;
    }

    float2 uv = (pixel + 0.5) / float2(w, h);
    float2 ndc = uv * 2.0 - 1.0;
    ndc.y = -ndc.y;
    float4 clipPos = float4(ndc, depth, 1.0);
    float4 viewPos = mul(gInvProjMatrix, clipPos);
    viewPos /= viewPos.w;
    float3 worldPos = mul(gInvViewMatrix, viewPos).xyz;

    float3 surfaceNormal = DecodeNormalOctahedral(normalEncoded.xy);

    // 遍历贴花数组 (简化: 单贴花, 实际从贴花列表缓冲读取影响当前像素的贴花)
    // 此处对当前绑定贴花进行投影混合
    float4 localPos = mul(g_Decal_InvWorldMatrix, float4(worldPos, 1.0));

    if (IsInsideDecal(localPos.xyz * 2.0))
    {
        float3 decalLocal = localPos.xyz;
        float2 decalUV = DecalLocalToUV(decalLocal);
        decalUV.y = 1.0 - decalUV.y;

        // 采样贴花
        float4 decalAlbedo = g_DecalAlbedoSRV.SampleLevel(gLinearWrapSampler, decalUV, 0);
        float3 decalNormalTS = g_DecalNormalSRV.SampleLevel(gLinearWrapSampler, decalUV, 0) * 2.0 - 1.0;
        float4 decalORM = g_DecalORMSRV.SampleLevel(gLinearWrapSampler, decalUV, 0);

        float decalAlpha = decalAlbedo.a * g_Decal_Opacity * g_Decal_Color.a;

        if (decalAlpha > 0.01)
        {
            // 角度衰减
            float3 decalForward = normalize(mul((float3x3)g_Decal_WorldMatrix, float3(0, 0, 1)));
            float angleFade = AngleFade(surfaceNormal, decalForward, g_Decal_AngleFade);
            decalAlpha *= angleFade;

            if (decalAlpha > 0.01)
            {
                // 法线转换
                if (g_Decal_TwoSided != 0 && dot(surfaceNormal, decalForward) > 0.0)
                    decalNormalTS.z = -decalNormalTS.z;

                float3 decalNormalWS = TangentToWorld(decalNormalTS, surfaceNormal);

                // 混合
                float3 srcAlbedo = decalAlbedo.rgb * g_Decal_Color.rgb;
                albedo.rgb = ApplyBlendMode(albedo.rgb, srcAlbedo, decalAlpha, g_Decal_BlendMode);

                float3 blendedNormal = normalize(lerp(surfaceNormal, decalNormalWS, g_Decal_NormalBlend * decalAlpha));
                normalEncoded = float4(EncodeNormalOctahedral(blendedNormal), normalEncoded.zw);

                orm.g = lerp(orm.g, decalORM.g, g_Decal_RoughnessBlend * decalAlpha);
                orm.b = lerp(orm.b, decalORM.b, g_Decal_MetallicBlend * decalAlpha);
            }
        }
    }

    // 写回修改后的 GeometryBuffer
    g_DecalAlbedoUAV[pixel] = albedo;
    g_DecalNormalUAV[pixel] = normalEncoded;
    g_DecalORMUAV[pixel] = orm;
    g_DecalEmissiveUAV[pixel] = emissive;
}
