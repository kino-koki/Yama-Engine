// Sandbox 三角形片段着色器 (HLSL, DXC 编译到 SPIR-V)
// 直接输出顶点透传的颜色

struct PSInput
{
    [[vk::location(0)]] float4 in_col : COLOR0;
};

struct PSOutput
{
    float4 color : SV_Target0;
};

PSOutput PSMain(PSInput input)
{
    PSOutput output;
    output.color = input.in_col;
    return output;
}
