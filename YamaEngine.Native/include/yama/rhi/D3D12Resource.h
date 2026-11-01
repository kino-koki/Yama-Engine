#pragma once

#include <cstdint>

namespace yama::rhi {

enum class ResourceState : uint32_t
{
    Common,
    VertexBuffer,
    IndexBuffer,
    ConstantBuffer,
    ShaderResource,
    UnorderedAccess,
    RenderTarget,
    DepthWrite,
    DepthRead,
    CopySource,
    CopyDest,
    Present
};

/// <summary>GPU资源描述（预留扩展）。</summary>
struct ResourceDesc
{
    uint32_t Width;
    uint32_t Height;
    uint32_t Depth;
    uint32_t MipLevels;
    uint32_t Format;
    uint32_t Flags;
};

} // namespace yama::rhi
