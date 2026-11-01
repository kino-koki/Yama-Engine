// =============================================================================
//  Cel2DSandboxApp.cs — CelShading2D 真实 GPU 验证应用（headless 离屏渲染 + 读回）
//
//  在真实 Vulkan 设备上：
//    1. 创建离屏渲染目标 (R8G8B8A8_UNorm)
//    2. 程序化生成赛璐璐角色精灵纹理并上传 GPU
//    3. 用 CelShading2DSystem 渲染 4 组不同材质参数的精灵（量化/描边/边缘光/网点）
//    4. RHIReadbackManager 读回像素 → 自实现 PNG 编码器 → 保存 .png
//
//  无需窗口，可在无显示环境运行，用于验证赛璐璐效果在 GPU 上真实生效。
// =============================================================================

using System.Numerics;
using YamaEngine.Cel2D;
using YamaEngine.RHI;

namespace YamaCel2DSandbox;

internal static class Cel2DSandboxApp
{
    private const int CharSize = CelSpriteGen.Size;

    /// <summary>headless 离屏渲染单帧并保存 PNG。返回进程退出码。</summary>
    public static int RunHeadless(string outputPath, int width = 800, int height = 600)
    {
        Console.WriteLine($"[Cel2D] === 赛璐璐 2D headless 渲染: {width}x{height} ===");

        // ── 1. 创建设备（Vulkan 后端）──
        using var device = RHIDeviceFactory.Create();
        Console.WriteLine($"[Cel2D] GPU 设备: {device.AdapterName}");

        // ── 2. 屏障跟踪 / 拷贝引擎 / 读回管理器 ──
        using var barrierTracker = new RHIBarrierTracker();
        using var copyEngine = new RHICopyEngine(device, barrierTracker);
        using var readback = new RHIReadbackManager(device, copyEngine, barrierTracker);

        var graphicsQueue = device.GetCommandQueue(RHIQueueType.Graphics);
        var copyQueue = device.GetCommandQueue(RHIQueueType.Copy);
        using var cmdList = device.CreateCommandList(RHIQueueType.Graphics, "Cel2D.MainCmdList");
        using var fence = device.CreateFence(0, "Cel2D.FrameFence");
        ulong fenceValue = 0;

        // ── 3. 离屏渲染目标 ──
        var clear = new Vector4(0.16f, 0.14f, 0.22f, 1f);
        using var rt = device.CreateTexture(new RHITextureDesc
        {
            Dimension = RHIResourceDimension.Texture2D,
            Format = RHIFormat.R8G8B8A8_UNorm,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleCount = 1,
            SampleQuality = 0,
            Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource,
            OptimizedClearValue = RHIClearValueHelper.Color(RHIFormat.R8G8B8A8_UNorm, clear)
        }, "Cel2D.OffscreenRT");
        var rtv = device.CreateRTV(rt);

        // ── 4. 生成角色精灵纹理并上传 ──
        var pixels = CelSpriteGen.GenerateCharacter();
        using var spriteTex = device.CreateTexture(new RHITextureDesc
        {
            Dimension = RHIResourceDimension.Texture2D,
            Format = RHIFormat.R8G8B8A8_UNorm,
            Width = CharSize,
            Height = CharSize,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleCount = 1,
            SampleQuality = 0,
            Flags = RHITextureFlags.ShaderResource
        }, "Cel2D.CharacterTex");
        device.UploadToTexture(spriteTex, pixels.AsSpan(), CharSize, CharSize);
        var spriteSRV = device.CreateSRV(spriteTex);
        var texRef = new CelTextureRef(spriteSRV, new Vector2(CharSize, CharSize));

        // ── 5. Cel2D 系统 ──
        using var cel = new CelShading2DSystem(
            device,
            path => path == "character" ? texRef : null,
            RHIFormat.R8G8B8A8_UNorm);
        cel.Initialize();

        // ── 6. 构建 4 组不同赛璐璐参数的精灵 ──
        var sprites = BuildScene(width, height);

        // ── 7. 渲染一帧 ──
        var camera = new Cel2DCamera(new Vector2(width, height), new Vector2(width / 2f, height / 2f), 1f);
        RenderFrame(device, cmdList, graphicsQueue, fence, ref fenceValue,
            rt, rtv, cel, in camera, sprites, 0f);

        // ── 8. 读回像素 ──
        Console.WriteLine("[Cel2D] 请求读回渲染目标...");
        readback.BeginFrame(0);
        long requestId = readback.RequestTextureReadback(rt, 0, 0, width, height, RHIFormat.R8G8B8A8_UNorm);
        readback.EndFrame();
        copyQueue.Flush();

        var raw = readback.GetResultData(requestId);
        if (raw == null)
        {
            Console.Error.WriteLine("[Cel2D] 读回失败: 结果为空");
            return 1;
        }

        // ── 9. 解交错（rowPitch 256 字节对齐 → 紧凑 RGBA）→ PNG ──
        int rowPitch = raw.Length / height;
        var unpacked = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Array.Copy(raw, y * rowPitch, unpacked, y * width * 4, width * 4);

        var png = PngWriter.Encode(unpacked, width, height);
        File.WriteAllBytes(outputPath, png);

        Console.WriteLine($"[Cel2D] ✓ 已保存: {outputPath} ({width}x{height}, PNG {png.Length} 字节)");
        Console.WriteLine($"[Cel2D] 场景: 量化 | 量化+描边 | 量化+边缘光 | 量化+网点");
        return 0;
    }

    /// <summary>构建 4 个并排角色，各自使用一种赛璐璐效果组合。</summary>
    private static List<CelSpriteData> BuildScene(int width, int height)
    {
        float y = height / 2f;
        float size = 170f;
        var list = new List<CelSpriteData>();

        // A: 纯赛璐璐量化（4 阶）
        list.Add(Make("character", 120, y, size, m =>
        {
            m.BandCount = 4;
            m.EnableOutline = false;
            m.RimIntensity = 0;
            m.HalftoneDensity = 0;
        }));

        // B: 量化 + 描边（线稿）
        list.Add(Make("character", 320, y, size, m =>
        {
            m.BandCount = 3;
            m.OutlineWidth = 2.6f;
            m.EnableOutline = true;
            m.RimIntensity = 0;
            m.HalftoneDensity = 0;
        }));

        // C: 量化 + 边缘光
        list.Add(Make("character", 520, y, size, m =>
        {
            m.BandCount = 4;
            m.EnableOutline = false;
            m.RimIntensity = 1.4f;
            m.HalftoneDensity = 0;
        }));

        // D: 量化 + 半调网点（阴影区）
        list.Add(Make("character", 720, y, size, m =>
        {
            m.BandCount = 3;
            m.EnableOutline = false;
            m.RimIntensity = 0;
            m.HalftoneDensity = 22f;
        }));

        return list;
    }

    private static CelSpriteData Make(string path, float x, float y, float size, Action<CelToonMaterial> configure)
    {
        var s = new CelSpriteData
        {
            TexturePath = path,
            Position = new Vector2(x, y),
            Size = new Vector2(size, size),
            SourceRect = new Vector4(0, 0, 1, 1),
            Tint = Vector4.One,
            Material = new CelToonMaterial()
        };
        configure(s.Material);
        return s;
    }

    /// <summary>录制并执行一帧 Cel2D 渲染。</summary>
    private static void RenderFrame(RHIDevice device, RHICommandList cmdList,
        RHICommandQueue graphicsQueue, RHIFence fence, ref ulong fenceValue,
        RHITexture rt, RHIDescriptorHandle rtv, CelShading2DSystem cel,
        in Cel2DCamera camera, List<CelSpriteData> sprites, float time)
    {
        int w = rt.Width;
        int h = rt.Height;

        cmdList.Open();
        cmdList.Reset(null);

        cmdList.TransitionTo(rt, RHIResourceState.RenderTarget);
        cmdList.ClearRenderTargetView(rtv, camera.ClearColor);
        cmdList.OMSetRenderTargets(new[] { rtv }, null);
        cmdList.RSSetViewport(0, 0, w, h, 0f, 1f);
        cmdList.RSSetScissorRect(0, 0, w, h);

        cel.Draw(cmdList, rtv, rt, in camera, sprites, time);

        // 为 readback 准备: 转换到 CopySource
        cmdList.TransitionTo(rt, RHIResourceState.CopySource);
        cmdList.Close();

        graphicsQueue.ExecuteCommandLists(cmdList);
        fenceValue++;
        graphicsQueue.Signal(fence, fenceValue);
        fence.Wait(fenceValue);
    }
}
