#include "shanyu/rhi/D3D12CommandList.h"

namespace shanyu::rhi
{
    // 命令列表辅助函数（预留扩展）
    // 实际后端实现由C#侧通过Vortice.Direct3D12管理

    ICommandBuffer* CreateCommandBuffer()
    {
        // 桩实现：返回nullptr，实际使用时由平台后端覆盖
        return nullptr;
    }

    void DestroyCommandBuffer(ICommandBuffer* cmd)
    {
        // 桩实现
        (void)cmd;
    }
}
