// =============================================================================
//  Yama Engine - BasicPBR.hlsl
//  A simplified physically-based forward shader.
//
//  Pipeline:
//    VSMain  - transforms position/normal/UV to clip & world space
//    PSMain  - evaluates a single Cook-Torrance direct light + ambient term
//
//  Resources (declared here, cbuffer & PBR math come from Common.hlsli):
//    b0  PBRConstants        - per-draw transform + light + material data
//    t0  AlbedoMap           - base color / albedo texture (sRGB source)
//    t1  NormalMap           - tangent-space normal map (neutral = 0.5,0.5,1.0)
//    s0  LinearWrapSampler   - linear filtering, wrap addressing
//
//  The constant buffer `PBRConstants` (register b0) and the PBR helper
//  functions are defined in Common.hlsli, which is included below.
//
//  Shader model: 5.0 (fxc) / 6.0+ (dxc).
// =============================================================================

#include "Common.hlsli"

// ---------------------------------------------------------------------------
//  Textures & samplers
// ---------------------------------------------------------------------------
Texture2D    AlbedoMap        : register(t0);
Texture2D    NormalMap        : register(t1);
SamplerState LinearWrapSampler : register(s0);

// ---------------------------------------------------------------------------
//  Vertex shader
//
//  Receives Position / Normal / UV and outputs SV_POSITION plus the world-space
//  normal, UV and world position required by the pixel shader. Normals are
//  transformed by the upper-left 3x3 of the World matrix, which is correct for
//  uniform scaling (the common case). Non-uniform scaling would require the
//  inverse-transpose, which is not part of this constant buffer.
// ---------------------------------------------------------------------------
PixelInput VSMain(VertexInput input)
{
    PixelInput output;

    // Clip-space position (row-vector convention matches row-major uploads).
    output.Position = mul(float4(input.Position, 1.0), WorldViewProj);

    // World-space position (used for lighting).
    output.WorldPos = mul(float4(input.Position, 1.0), World).xyz;

    // World-space normal (upper-left 3x3; assume uniform scale).
    output.Normal = mul(input.Normal, (float3x3) World);

    // Pass-through UVs.
    output.TexCoord = input.TexCoord;

    return output;
}

// ---------------------------------------------------------------------------
//  Perturb the geometric normal using a tangent-space normal map, without
//  requiring a precomputed tangent vector. The TBN basis is reconstructed from
//  screen-space derivatives of the world position and UV (Schuler's method).
//  A neutral normal map sample (0.5, 0.5, 1.0) leaves the geometric normal
//  unchanged, so binding a flat blue texture effectively disables the effect.
// ---------------------------------------------------------------------------
float3 GetNormalFromMap(float3 geoNormal, float3 worldPos, float2 uv)
{
    // Decode the sampled tangent-space normal from [0,1] to [-1,1].
    float3 tangentNormal = NormalMap.Sample(LinearWrapSampler, uv).xyz * 2.0 - 1.0;

    // Screen-space derivatives of position and UV.
    float3 dp1 = ddx(worldPos);
    float3 dp2 = ddy(worldPos);
    float2 duv1 = ddx(uv);
    float2 duv2 = ddy(uv);

    float3 N = normalize(geoNormal);

    // Reconstruct a scale-invariant tangent/bitangent basis.
    float3 dp2perp = cross(N, dp2);
    float3 dp1perp = cross(dp1, N);
    float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
    float3 B = dp2perp * duv1.y + dp1perp * duv2.y;

    float invmax = rsqrt(max(dot(T, T), dot(B, B)));
    T *= invmax;
    B *= invmax;

    // Transform the tangent-space normal into world space.
    float3x3 TBN = float3x3(T, B, N);
    return normalize(mul(tangentNormal, TBN));
}

// ---------------------------------------------------------------------------
//  Pixel shader
//
//  Implements a simplified physically based shading model with one directional
//  light (direct) plus a constant ambient term:
//
//    Lo = ( kD * albedo / PI  +  DFG / (4 * NdotV * NdotL) ) * radiance * NdotL
//    color = ambient + Lo
//
//  where the Fresnel term drives both the specular and the energy-conserving
//  diffuse weight kD = (1 - F) * (1 - metallic). Output is Reinhard tone-mapped
//  and gamma-corrected to sRGB.
// ---------------------------------------------------------------------------
float4 PSMain(PixelInput input) : SV_TARGET
{
    // --- Texture sampling -------------------------------------------------
    float4 albedoSample = AlbedoMap.Sample(LinearWrapSampler, input.TexCoord);
    float3 albedo       = albedoSample.rgb * BaseColor;

    // --- Surface normal (with normal mapping) -----------------------------
    float3 N = GetNormalFromMap(input.Normal, input.WorldPos, input.TexCoord);

    // --- Direction vectors ------------------------------------------------
    // LightDirection points from the light toward the scene; L points toward
    // the light (from surface to light).
    float3 V = normalize(CameraPosition - input.WorldPos);
    float3 L = normalize(-LightDirection);
    float3 H = normalize(V + L);

    // --- Material parameters ----------------------------------------------
    float  metallic  = saturate(Metallic);
    float  roughness = saturate(Roughness);

    // F0: reflectance at normal incidence. Dielectrics ~0.04, metals = albedo.
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

    // --- Cook-Torrance specular BRDF --------------------------------------
    float  NdotL = max(dot(N, L), 0.0);
    float  NdotV = max(dot(N, V), 0.0);

    float  D = DistributionGGX(N, H, roughness);
    float  G = GeometrySmith(N, V, L, roughness);
    float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    float3 numerator   = D * G * F;
    float  denominator = 4.0 * NdotV * NdotL + EPSILON;
    float3 specular    = numerator / denominator;

    // --- Energy-conserving Lambertian diffuse -----------------------------
    // Diffuse is scaled by (1 - F) and killed for metals.
    float3 kD = (1.0 - F) * (1.0 - metallic);
    float3 diffuse = kD * albedo * INV_PI;

    // --- Incoming radiance from the directional light ---------------------
    float3 radiance = LightColor * NdotL;

    // --- Direct lighting --------------------------------------------------
    float3 Lo = (diffuse + specular) * radiance;

    // --- Ambient (simplified IBL approximation) ---------------------------
    float3 ambient = 0.03 * albedo;

    // --- Final color: tone map + gamma ------------------------------------
    float3 color = ambient + Lo;
    color = color / (color + 1.0);                  // Reinhard tone mapping
    color = pow(max(color, 0.0), 1.0 / 2.2);        // linear -> sRGB gamma (clamp >= 0 for pow)

    return float4(color, albedoSample.a);
}
