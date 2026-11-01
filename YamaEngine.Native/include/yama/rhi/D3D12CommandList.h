#pragma once

#include <cstdint>

namespace yama::rhi {

/// <summary>原生命令缓冲区抽象（预留D3D12/Vulkan后端扩展）。</summary>
class ICommandBuffer
{
public:
    virtual ~ICommandBuffer() = default;
    virtual void Begin() = 0;
    virtual void End() = 0;
    virtual void DrawIndexed(uint32_t indexCount, uint32_t instanceCount) = 0;
    virtual void ResourceBarrier(uint32_t type, void* resource) = 0;
    virtual void* GetNativeHandle() = 0;
};

/// <summary>创建平台相关的命令缓冲区（当前为预留桩）。</summary>
ICommandBuffer* CreateCommandBuffer();
void DestroyCommandBuffer(ICommandBuffer* cmd);

} // namespace yama::rhi
