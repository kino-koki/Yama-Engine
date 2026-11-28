// Sandbox 三角形顶点着色器 (HLSL, DXC 编译到 SPIR-V)
// 输入：2D 位置 (loc 0) + RGBA 颜色 (loc 1)
// 输出：clip-space 位置 + 透传颜色

struct VSInput
{
    [[vk::location(0)]] float2 in_pos : POSITION;
    [[vk::location(1)]] float4 in_col : COLOR0;
};

struct VSOutput
{
    float4 pos : SV_Position;
    [[vk::location(0)]] float4 col : COLOR0;
};

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.pos = float4(input.in_pos, 0.0, 1.0);
    output.col = input.in_col;
    return output;
}
