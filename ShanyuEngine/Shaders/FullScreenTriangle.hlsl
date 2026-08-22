// =============================================================================
//  Shanyu Engine - FullScreenTriangle.hlsl
//  A post-processing vertex/pixel shader pair that renders a single triangle
//  covering the entire viewport. No vertex or index buffers are required: the
//  geometry is generated procedurally from SV_VertexID.
//
//  This is the standard building block for full-screen passes (tone mapping,
//  blurs, screen-space effects, debug overlays, etc.): bind a source render
//  target as t0 and draw 3 vertices.
//
//  Resources:
//    t0  SourceTexture     - input render target / texture to sample
//    s0  PointClampSampler - point filtering, clamp addressing (no bleeding)
//
//  Shader model: 5.0 (fxc) / 6.0+ (dxc).
//
//  NOTE: This file intentionally does NOT include Common.hlsli so it stays
//  self-contained and free of the PBR constant-buffer / texture bindings.
// =============================================================================

// ---------------------------------------------------------------------------
//  Vertex output: clip-space position + UV.
// ---------------------------------------------------------------------------
struct FullScreenVSOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

// ---------------------------------------------------------------------------
//  Vertex shader
//
//  Generates a full-screen triangle from the vertex id alone. The three
//  vertices (id 0, 1, 2) are placed at:
//
//      id 0 -> (-1, -1)   uv (0, 1)
//      id 1 -> ( 3, -1)   uv (2, 1)
//      id 2 -> (-1,  3)   uv (0,-1)
//
//  The single triangle comfortably covers the [-1,1] x [-1,1] clip region; the
//  parts of the triangle that fall outside the viewport are clipped by the
//  rasterizer, so only the on-screen [0,1] UV range is actually shaded.
// ---------------------------------------------------------------------------
FullScreenVSOutput VSMain(uint vertexId : SV_VertexID)
{
    FullScreenVSOutput output;

    // Bit trick: x = (id << 1) & 2, y = id & 2  ->  (0,0),(2,0),(0,2)
    output.TexCoord = float2((vertexId << 1) & 2, vertexId & 2);

    // Map UV [0,2] -> NDC [-1,3] so the triangle spans and overshoots the screen.
    output.Position = float4(output.TexCoord * 2.0 - 1.0, 0.0, 1.0);

    // Flip V for the common D3D convention where texture V=0 is at the top and
    // render-target Y=0 is at the top as well (so a straight blit looks upright).
    output.TexCoord.y = 1.0 - output.TexCoord.y;

    return output;
}

// ---------------------------------------------------------------------------
//  Resources
// ---------------------------------------------------------------------------
Texture2D    SourceTexture   : register(t0);
SamplerState PointClampSampler : register(s0);

// ---------------------------------------------------------------------------
//  Pixel shader
//
//  Simply samples the source texture at the interpolated UV. Swap in a custom
//  kernel here for any screen-space post effect (blur, tone map, etc.).
// ---------------------------------------------------------------------------
float4 PSMain(FullScreenVSOutput input) : SV_TARGET
{
    return SourceTexture.Sample(PointClampSampler, input.TexCoord);
}
