namespace ShanyuEngine.Rendering
{
    public static class TerrainShaders
    {
        public const string TerrainVertexShader = @"
cbuffer TerrainCB : register(b0) {
    float4x4 ViewProjection;
    float4 CameraPosition;
    float HeightScale;
    float TerrainSize;
    float TessellationFactor;
    float TessellationDistance;
    uint UseTessellation;
};
struct VSInput {
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD;
};
struct VSOutput {
    float4 Position : SV_POSITION;
    float3 WorldPos : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float2 UV : TEXCOORD2;
    float Height : TEXCOORD3;
};
Texture2D HeightMap : register(t0);
SamplerState LinearSampler : register(s0);
VSOutput main(VSInput input) {
    VSOutput output;
    float height = HeightMap.SampleLevel(LinearSampler, input.UV, 0).r * HeightScale;
    float3 worldPos = float3(input.Position.x, height, input.Position.z);
    output.Position = mul(float4(worldPos, 1.0), ViewProjection);
    output.WorldPos = worldPos;
    // Compute normal from heightmap
    float2 texelSize = 1.0 / float2(1024, 1024);
    float hL = HeightMap.SampleLevel(LinearSampler, input.UV - float2(texelSize.x, 0), 0).r * HeightScale;
    float hR = HeightMap.SampleLevel(LinearSampler, input.UV + float2(texelSize.x, 0), 0).r * HeightScale;
    float hD = HeightMap.SampleLevel(LinearSampler, input.UV - float2(0, texelSize.y), 0).r * HeightScale;
    float hU = HeightMap.SampleLevel(LinearSampler, input.UV + float2(0, texelSize.y), 0).r * HeightScale;
    float3 normal = normalize(float3(hL - hR, 2.0, hD - hU));
    output.Normal = normal;
    output.UV = input.UV * 16.0; // texture tiling
    output.Height = height;
    return output;
}";

        public const string TerrainPixelShader = @"
struct PSInput {
    float4 Position : SV_POSITION;
    float3 WorldPos : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float2 UV : TEXCOORD2;
    float Height : TEXCOORD3;
};
Texture2D SplatMap : register(t1);
Texture2D Albedo0 : register(t2);
Texture2D Albedo1 : register(t3);
Texture2D Albedo2 : register(t4);
Texture2D Albedo3 : register(t5);
Texture2D Normal0 : register(t6);
SamplerState LinearWrap : register(s0);
float4 main(PSInput input) : SV_TARGET {
    float4 splat = SplatMap.Sample(LinearWrap, input.UV / 16.0);
    float4 col0 = Albedo0.Sample(LinearWrap, input.UV);
    float4 col1 = Albedo1.Sample(LinearWrap, input.UV);
    float4 col2 = Albedo2.Sample(LinearWrap, input.UV);
    float4 col3 = Albedo3.Sample(LinearWrap, input.UV);
    float4 albedo = col0 * splat.r + col1 * splat.g + col2 * splat.b + col3 * splat.a;
    float3 normal = normalize(input.Normal);
    float3 lightDir = normalize(float3(0.5, 1.0, 0.3));
    float NdotL = max(dot(normal, lightDir), 0.0);
    float3 ambient = albedo.rgb * 0.1;
    float3 diffuse = albedo.rgb * NdotL * 0.7;
    return float4(ambient + diffuse, albedo.a);
}";

        public const string TerrainHullShader = @"
struct HSInput { float3 Position : POSITION; float3 Normal : NORMAL; float2 UV : TEXCOORD; };
struct HSOutput { float3 Position : POSITION; float3 Normal : NORMAL; float2 UV : TEXCOORD; };
struct HSConstantOutput {
    float EdgeTess[3] : SV_TessFactor;
    float InsideTess : SV_InsideTessFactor;
};
HSConstantOutput PatchConstant(InputPatch<HSInput, 3> patch) {
    HSConstantOutput output;
    output.EdgeTess[0] = output.EdgeTess[1] = output.EdgeTess[2] = 4.0;
    output.InsideTess = 4.0;
    return output;
}
[domain(""tri"")]
[partitioning(""integer"")]
[outputtopology(""triangle_cw"")]
[outputcontrolpoints(3)]
[patchconstantfunc(""PatchConstant"")]
HSOutput main(InputPatch<HSInput, 3> patch, uint id : SV_OutputControlPointID) {
    HSOutput output;
    output.Position = patch[id].Position;
    output.Normal = patch[id].Normal;
    output.UV = patch[id].UV;
    return output;
}";

        public const string TerrainDomainShader = @"
struct DSInput {
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 UV : TEXCOORD;
};
struct DSOutput {
    float4 Position : SV_POSITION;
    float3 WorldPos : TEXCOORD0;
    float3 Normal : TEXCOORD1;
    float2 UV : TEXCOORD2;
    float Height : TEXCOORD3;
};
Texture2D HeightMap : register(t0);
SamplerState LinearSampler : register(s0);
cbuffer TerrainCB : register(b0) {
    float4x4 ViewProjection;
    float4 CameraPosition;
    float HeightScale;
};
[domain(""tri"")]
DSOutput main(HSConstantOutput input, float3 bary : SV_DomainLocation, const OutputPatch<DSInput, 3> patch) {
    DSOutput output;
    float3 pos = patch[0].Position * bary.x + patch[1].Position * bary.y + patch[2].Position * bary.z;
    float2 uv = patch[0].UV * bary.x + patch[1].UV * bary.y + patch[2].UV * bary.z;
    float3 normal = patch[0].Normal * bary.x + patch[1].Normal * bary.y + patch[2].Normal * bary.z;
    float height = HeightMap.SampleLevel(LinearSampler, uv, 0).r * HeightScale;
    pos.y = height;
    output.Position = mul(float4(pos, 1.0), ViewProjection);
    output.WorldPos = pos;
    output.Normal = normalize(normal);
    output.UV = uv * 16.0;
    output.Height = height;
    return output;
}";
    }
}
