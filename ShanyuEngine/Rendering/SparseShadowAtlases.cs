using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using static ShanyuEngine.Rendering.MatrixMath;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════════
    // UE5 风格虚拟阴影图 (Virtual Shadow Maps, VSM) 系统
    // ════════════════════════════════════════════════════════════════════════════
    //
    // 核心思想：
    //   传统阴影图分辨率固定，大场景下近处精度不足或远处浪费显存。虚拟阴影图采用
    //   "页面化"策略——定义一张巨大的虚拟纹理（16K×16K），按 128×128 的页面切分，
    //   只为屏幕上实际可见的页面分配物理显存并渲染深度。通过 Mip 流式，远处使用低
    //   Mip（小页面集合），近处使用高 Mip（高精度），实现恒定且高质量的阴影密度。
    //
    // 与操作系统的虚拟内存类比：
    //   虚拟纹理(16K)  ≈ 虚拟地址空间
    //   物理图集(8K)   ≈ 物理内存
    //   页表(PageTable)≈ 页表，负责 虚拟页面 → 物理页面 映射
    //   页面分配器      ≈ 内存分配器，含 LRU 淘汰
    //
    // 渲染流水线（每帧 4 个 Pass）：
    //   Pass 1 深度预计算 —— 计算光源视锥体内的深度范围，确定哪些虚拟页面被需要
    //   Pass 2 页面分配   —— 为需要的可见页面分配物理页面，淘汰 LRU 页面
    //   Pass 3 页面渲染   —— 仅渲染脏页面到物理阴影图（通过页表寻址）
    //   Pass 4 采样       —— 着色阶段通过页表查找物理页面进行阴影采样
    // ════════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 全局常量：虚拟阴影图尺寸约定
    // ──────────────────────────────────────────────────────────────

    /// <summary>虚拟阴影图全局常量。所有虚拟纹理共享同一套页面尺寸约定。</summary>
    public static class VSMConstants
    {
        /// <summary>虚拟纹理边长（像素）。16K，与 UE5 默认一致。</summary>
        public const int VirtualTextureSize = 16384;

        /// <summary>单个页面边长（像素）。128×128 是 UE5 的默认页面大小，
        /// 兼顾采样局部性与 GPU 线程组调度效率。</summary>
        public const int PageSize = 128;

        /// <summary>Mip 0 时每边的页面数 = 16384 / 128 = 128。</summary>
        public const int PagesPerSideAtMip0 = VirtualTextureSize / PageSize; // 128

        /// <summary>物理阴影图集边长（像素）。8K 图集可容纳 (8192/128)^2 = 4096 个物理页面。</summary>
        public const int PhysicalAtlasSize = 8192;

        /// <summary>物理图集每边的页面数 = 8192 / 128 = 64。</summary>
        public const int PhysicalPagesPerSide = PhysicalAtlasSize / PageSize; // 64

        /// <summary>物理页面总数 = 64 * 64 = 4096。</summary>
        public const int TotalPhysicalPages = PhysicalPagesPerSide * PhysicalPagesPerSide; // 4096

        /// <summary>页表索引位宽。12 位可寻址 4096 个物理页面，恰好覆盖整个物理图集。
        /// 0xFFF(4095) 被保留为"未分配"哨兵值，因此有效物理页面索引为 0..4094。</summary>
        public const int PageTableIndexBits = 12;

        /// <summary>页表无效哨兵：表示该虚拟页面尚未映射到任何物理页面。</summary>
        public const uint InvalidPhysicalPage = 0xFFFu; // 4095

        /// <summary>有效物理页面索引上界（不含哨兵）。</summary>
        public const int MaxValidPhysicalPageIndex = (int)InvalidPhysicalPage - 1; // 4094

        /// <summary>最大 Mip 级数。16384/128=128=2^7，因此 Mip 0..7 共 8 级。
        /// Mip 7 时虚拟纹理缩到 128 像素 = 1 个页面。</summary>
        public const int MaxMipLevels = 8;

        /// <summary>计算给定 Mip 级下虚拟纹理每边的页面数。</summary>
        public static int PagesPerSide(int mip) => PagesPerSideAtMip0 >> Math.Clamp(mip, 0, MaxMipLevels - 1);

        /// <summary>计算给定 Mip 级下虚拟纹理的有效像素边长。</summary>
        public static int VirtualSizeAtMip(int mip) => PageSize * PagesPerSide(mip);

        /// <summary>计算 (mip, pageX, pageY) 的线性虚拟页面索引。用于在页表中寻址。</summary>
        public static int LinearVirtualPageIndex(int mip, int pageX, int pageY)
        {
            // 各 Mip 层级页面数呈平方递减，按 Mip 顺序线性排列。
            // Mip 0: 128*128, Mip 1: 64*64, ... 累计偏移。
            int baseIndex = 0;
            for (int m = 0; m < mip; m++)
                baseIndex += PagesPerSide(m) * PagesPerSide(m);
            return baseIndex + pageY * PagesPerSide(mip) + pageX;
        }

        /// <summary>所有 Mip 层级的虚拟页面总数（单个虚拟纹理）。</summary>
        public static int TotalVirtualPages
        {
            get
            {
                int total = 0;
                for (int m = 0; m < MaxMipLevels; m++)
                    total += PagesPerSide(m) * PagesPerSide(m);
                return total; // = 21845
            }
        }
    }

    /// <summary>虚拟阴影图支持的光源类型。</summary>
    public enum VSMLightType
    {
        /// <summary>方向光：使用 Clipmap 级联分层，每层一个虚拟纹理。</summary>
        Directional,
        /// <summary>点光源：立方体贴图虚拟化，6 个面各一个虚拟纹理。</summary>
        Point,
        /// <summary>聚光灯：单张虚拟纹理。</summary>
        Spot
    }

    /// <summary>物理页面的分配状态。</summary>
    public enum PhysicalPageState
    {
        /// <summary>空闲，可被分配。</summary>
        Free,
        /// <summary>已分配且正在使用。</summary>
        Allocated,
        /// <summary>已分配但可被淘汰（候选）。</summary>
        Evictable
    }

    // ──────────────────────────────────────────────────────────────
    // 页面数据结构
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 虚拟阴影图页面描述。表示虚拟纹理中一个 128×128 的页面。
    /// 这是页面分配、脏页跟踪、LRU 缓存的基本单元。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SparseShadowAtlasPage : IEquatable<SparseShadowAtlasPage>
    {
        /// <summary>Mip 级别（0 = 最高精度，7 = 最低精度）。</summary>
        public int Mip;
        /// <summary>该 Mip 下的页面 X 坐标（页面单位，非像素）。</summary>
        public int PageX;
        /// <summary>该 Mip 下的页面 Y 坐标。</summary>
        public int PageY;
        /// <summary>所属光源索引（用于多光源共享物理图集时区分）。</summary>
        public int LightIndex;
        /// <summary>该页面所属的虚拟纹理实例索引（如方向光的多级 Clipmap）。</summary>
        public int VirtualTextureIndex;
        /// <summary>立方体面索引（仅点光源有效，0..5；其它光源为 0）。</summary>
        public int CubeFace;
        /// <summary>是否需要更新（脏页标记）。光源或物体移动时置位。</summary>
        public bool Dirty;

        /// <summary>页面对应的虚拟纹理像素区域起点 X。</summary>
        public readonly int PixelX => PageX * VSMConstants.PageSize;
        /// <summary>页面对应的虚拟纹理像素区域起点 Y。</summary>
        public readonly int PixelY => PageY * VSMConstants.PageSize;

        /// <summary>该 Mip 下每边的页面数。</summary>
        public readonly int PagesPerSide => VSMConstants.PagesPerSide(Mip);

        /// <summary>线性虚拟页面索引（用于页表寻址）。</summary>
        public readonly int LinearIndex => VSMConstants.LinearVirtualPageIndex(Mip, PageX, PageY);

        public SparseShadowAtlasPage(int mip, int pageX, int pageY, int lightIndex, int vtIndex, int cubeFace = 0)
        {
            Mip = mip; PageX = pageX; PageY = pageY;
            LightIndex = lightIndex; VirtualTextureIndex = vtIndex; CubeFace = cubeFace;
            Dirty = true;
        }

        public readonly bool Equals(SparseShadowAtlasPage other) =>
            Mip == other.Mip && PageX == other.PageX && PageY == other.PageY &&
            LightIndex == other.LightIndex && VirtualTextureIndex == other.VirtualTextureIndex &&
            CubeFace == other.CubeFace;

        public override readonly bool Equals(object? obj) => obj is SparseShadowAtlasPage p && Equals(p);
        public override readonly int GetHashCode() => HashCode.Combine(Mip, PageX, PageY, LightIndex, VirtualTextureIndex, CubeFace);
        public static bool operator ==(SparseShadowAtlasPage a, SparseShadowAtlasPage b) => a.Equals(b);
        public static bool operator !=(SparseShadowAtlasPage a, SparseShadowAtlasPage b) => !a.Equals(b);

        public override readonly string ToString() =>
            $"VT{VirtualTextureIndex}/L{LightIndex}/F{CubeFace}/M{Mip}/P({PageX},{PageY}){(Dirty ? "*" : "")}";
    }

    /// <summary>
    /// 物理页面地址。表示物理图集中一个 128×128 槽位的坐标。
    /// 采样时由页表返回此地址，用于在物理纹理上定位像素。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct PhysicalPageAddress : IEquatable<PhysicalPageAddress>
    {
        /// <summary>物理图集中的页面 X 坐标（页面单位，0..63）。</summary>
        public readonly int PageX;
        /// <summary>物理图集中的页面 Y 坐标（0..63）。</summary>
        public readonly int PageY;

        public PhysicalPageAddress(int pageX, int pageY) { PageX = pageX; PageY = pageY; }

        /// <summary>物理像素起点 X。</summary>
        public int PixelX => PageX * VSMConstants.PageSize;
        /// <summary>物理像素起点 Y。</summary>
        public int PixelY => PageY * VSMConstants.PageSize;

        /// <summary>线性物理页面索引（0..4095），即 12 位页表索引。</summary>
        public uint LinearIndex => (uint)(PageY * VSMConstants.PhysicalPagesPerSide + PageX);

        /// <summary>由 12 位线性索引构造物理页面地址。</summary>
        public static PhysicalPageAddress FromLinear(uint linear)
        {
            linear &= (1u << VSMConstants.PageTableIndexBits) - 1u;
            int px = (int)(linear % VSMConstants.PhysicalPagesPerSide);
            int py = (int)(linear / VSMConstants.PhysicalPagesPerSide);
            return new PhysicalPageAddress(px, py);
        }

        public bool Equals(PhysicalPageAddress other) => PageX == other.PageX && PageY == other.PageY;
        public override bool Equals(object? obj) => obj is PhysicalPageAddress a && Equals(a);
        public override int GetHashCode() => HashCode.Combine(PageX, PageY);
        public static bool operator ==(PhysicalPageAddress a, PhysicalPageAddress b) => a.Equals(b);
        public static bool operator !=(PhysicalPageAddress a, PhysicalPageAddress b) => !a.Equals(b);

        public override string ToString() => $"Phys({PageX},{PageY})=#{LinearIndex}";
    }

    /// <summary>
    /// 页表条目：虚拟页面 → 物理页面映射。打包为 32 位以匹配 HLSL 结构化缓冲布局。
    /// 位布局：[11:0] 物理页面线性索引(12位) | [12] 有效位 | [15:13] Mip(3位) | [31:16] 帧龄(LRU)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PageTableEntry
    {
        public uint Packed;

        public PageTableEntry(uint packed) { Packed = packed; }

        /// <summary>12 位物理页面索引。0xFFF 表示未映射。</summary>
        public readonly uint PhysicalIndex => Packed & 0xFFFu;
        /// <summary>是否已映射到物理页面。</summary>
        public readonly bool IsValid => (Packed & 0x1000u) != 0u && PhysicalIndex != VSMConstants.InvalidPhysicalPage;
        /// <summary>Mip 级别（3 位）。</summary>
        public readonly int Mip => (int)((Packed >> 13) & 0x7u);
        /// <summary>帧龄：自上次使用以来的帧数，用于 LRU 淘汰。</summary>
        public readonly uint FrameAge => (Packed >> 16) & 0xFFFFu;

        /// <summary>构造一个有效的页表条目。</summary>
        public static PageTableEntry Create(uint physicalIndex, int mip)
        {
            uint packed = (physicalIndex & 0xFFFu) | 0x1000u;
            packed |= ((uint)(mip & 0x7u) << 13);
            return new PageTableEntry(packed);
        }

        /// <summary>构造一个无效（未映射）的页表条目。</summary>
        public static PageTableEntry Invalid => new(VSMConstants.InvalidPhysicalPage);

        /// <summary>递增帧龄（用于 LRU），保留映射信息。</summary>
        public PageTableEntry IncrementAge()
        {
            uint age = FrameAge;
            if (age < 0xFFFFu) age++;
            Packed = (Packed & 0x0000FFFFu) | (age << 16);
            return this;
        }

        /// <summary>重置帧龄为 0（页面被访问时调用）。</summary>
        public PageTableEntry ResetAge()
        {
            Packed &= 0x0000FFFFu;
            return this;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 页表 (PageTable)：虚拟页面 → 物理页面映射
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 页表：维护单个虚拟纹理的 虚拟页面 → 物理页面 映射。
    /// 类似操作系统的页表结构：给定 (mip, pageX, pageY) 查找物理页面地址。
    /// CPU 端维护一份镜像，GPU 端通过结构化缓冲(UAV)访问。
    /// </summary>
    public sealed class PageTable
    {
        /// <summary>页表条目数组，按线性虚拟页面索引排列。</summary>
        private readonly PageTableEntry[] _entries;
        /// <summary>该页表所属的虚拟纹理索引。</summary>
        public int VirtualTextureIndex { get; }
        /// <summary>页表条目总数（所有 Mip 层级的虚拟页面之和）。</summary>
        public int EntryCount => _entries.Length;

        public PageTable(int virtualTextureIndex)
        {
            VirtualTextureIndex = virtualTextureIndex;
            _entries = new PageTableEntry[VSMConstants.TotalVirtualPages];
            // 初始化全部为无效映射。
            for (int i = 0; i < _entries.Length; i++)
                _entries[i] = PageTableEntry.Invalid;
        }

        /// <summary>查询虚拟页面是否已映射到物理页面。</summary>
        public bool IsMapped(in SparseShadowAtlasPage page) =>
            this[page.LinearIndex].IsValid;

        /// <summary>通过线性索引读写页表条目。</summary>
        public PageTableEntry this[int linearIndex]
        {
            get => _entries[linearIndex];
            set => _entries[linearIndex] = value;
        }

        /// <summary>通过虚拟页面读写页表条目。</summary>
        public PageTableEntry this[in SparseShadowAtlasPage page]
        {
            get => _entries[page.LinearIndex];
            set => _entries[page.LinearIndex] = value;
        }

        /// <summary>建立映射：将虚拟页面绑定到物理页面地址。</summary>
        public void Map(in SparseShadowAtlasPage page, PhysicalPageAddress physical)
        {
            this[page] = PageTableEntry.Create(physical.LinearIndex, page.Mip);
        }

        /// <summary>解除映射：将虚拟页面标记为未映射。</summary>
        public void Unmap(in SparseShadowAtlasPage page)
        {
            this[page] = PageTableEntry.Invalid;
        }

        /// <summary>查找虚拟页面对应的物理页面地址。未映射返回 null。</summary>
        public PhysicalPageAddress? Resolve(in SparseShadowAtlasPage page)
        {
            var entry = this[page];
            if (!entry.IsValid) return null;
            return PhysicalPageAddress.FromLinear(entry.PhysicalIndex);
        }

        /// <summary>对该页表所有条目递增帧龄（每帧调用一次，用于 LRU）。</summary>
        public void IncrementAllAges()
        {
            for (int i = 0; i < _entries.Length; i++)
                _entries[i] = _entries[i].IncrementAge();
        }

        /// <summary>重置指定虚拟页面的帧龄（页面被访问时调用）。</summary>
        public void Touch(in SparseShadowAtlasPage page)
        {
            var idx = page.LinearIndex;
            _entries[idx] = _entries[idx].ResetAge();
        }

        /// <summary>清空所有映射。</summary>
        public void Clear()
        {
            for (int i = 0; i < _entries.Length; i++)
                _entries[i] = PageTableEntry.Invalid;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 页面分配器 (PageAllocator)：物理页面分配与 LRU 淘汰
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 物理页面分配器。管理 8K×8K 物理图集中 4096 个物理页面的分配/回收。
    /// 采用空闲列表 + LRU（最近最少使用）淘汰策略：
    /// 当物理页面耗尽时，淘汰帧龄最大的已分配页面。
    /// </summary>
    public sealed class PageAllocator
    {
        /// <summary>每个物理页面的状态。</summary>
        private readonly PhysicalPageState[] _states;
        /// <summary>每个物理页面当前映射到的虚拟页面（用于反向查找与淘汰）。</summary>
        private readonly SparseShadowAtlasPage?[] _owners;
        /// <summary>空闲物理页面线性索引队列。</summary>
        private readonly Queue<int> _freeList;
        /// <summary>已分配物理页面线性索引集合（用于 LRU 遍历）。</summary>
        private readonly HashSet<int> _usedSet;
        /// <summary>本帧已分配的页面数（统计用）。</summary>
        public int AllocatedCount => _usedSet.Count;
        /// <summary>本帧淘汰的页面数（统计用）。</summary>
        public int EvictedCount { get; private set; }

        public PageAllocator()
        {
            int total = VSMConstants.TotalPhysicalPages;
            _states = new PhysicalPageState[total];
            _owners = new SparseShadowAtlasPage?[total];
            _freeList = new Queue<int>(total);
            _usedSet = new HashSet<int>();
            for (int i = 0; i < total; i++)
            {
                _states[i] = PhysicalPageState.Free;
                _freeList.Enqueue(i);
            }
        }

        /// <summary>
        /// 尝试分配一个物理页面。优先使用空闲页面；若耗尽则按 LRU 淘汰。
        /// </summary>
        /// <param name="pageTables">所有虚拟纹理的页表集合（用于淘汰时解除映射）。</param>
        /// <param name="requestedPage">请求分配的虚拟页面（用于记录归属）。</param>
        /// <returns>物理页面地址；分配失败返回 null。</returns>
        public PhysicalPageAddress? Allocate(IReadOnlyList<PageTable> pageTables, in SparseShadowAtlasPage requestedPage)
        {
            int linear;
            if (_freeList.Count > 0)
            {
                linear = _freeList.Dequeue();
            }
            else
            {
                // 物理页面耗尽：执行 LRU 淘汰。
                linear = EvictLRU(pageTables);
                if (linear < 0) return null; // 无可淘汰页面
                EvictedCount++;
            }

            _states[linear] = PhysicalPageState.Allocated;
            _owners[linear] = requestedPage;
            _usedSet.Add(linear);
            return PhysicalPageAddress.FromLinear((uint)linear);
        }

        /// <summary>
        /// LRU 淘汰：在已分配页面中寻找帧龄最大的页面回收。
        /// 淘汰时会同步解除该页面在所属页表中的映射，并标记原虚拟页面为脏（下次需重渲染）。
        /// </summary>
        private int EvictLRU(IReadOnlyList<PageTable> pageTables)
        {
            int bestLinear = -1;
            uint maxAge = 0;
            foreach (var linear in _usedSet)
            {
                var owner = _owners[linear];
                if (!owner.HasValue) { bestLinear = linear; break; }

                // 通过所属页表查询该物理页面的帧龄。
                var pt = pageTables.Count > owner.Value.VirtualTextureIndex
                    ? pageTables[owner.Value.VirtualTextureIndex] : null;
                uint age = 1;
                if (pt != null)
                {
                    var entry = pt[owner.Value.LinearIndex];
                    if (entry.IsValid) age = entry.FrameAge + 1;
                }
                if (age > maxAge) { maxAge = age; bestLinear = linear; }
            }

            if (bestLinear < 0) return -1;

            // 解除原虚拟页面的映射，并标记为脏以便重新分配时重渲染。
            var oldOwner = _owners[bestLinear];
            if (oldOwner.HasValue)
            {
                var pt = pageTables.Count > oldOwner.Value.VirtualTextureIndex
                    ? pageTables[oldOwner.Value.VirtualTextureIndex] : null;
                pt?.Unmap(oldOwner.Value);
            }
            _owners[bestLinear] = null;
            _usedSet.Remove(bestLinear);
            return bestLinear;
        }

        /// <summary>释放指定物理页面，归还空闲列表。</summary>
        public void Free(PhysicalPageAddress addr)
        {
            int linear = (int)addr.LinearIndex;
            if (linear < 0 || linear >= _states.Length) return;
            if (_states[linear] == PhysicalPageState.Free) return;
            _states[linear] = PhysicalPageState.Free;
            _owners[linear] = null;
            _usedSet.Remove(linear);
            _freeList.Enqueue(linear);
        }

        /// <summary>查询物理页面是否已被分配。</summary>
        public bool IsAllocated(PhysicalPageAddress addr)
        {
            int linear = (int)addr.LinearIndex;
            return linear >= 0 && linear < _states.Length && _states[linear] != PhysicalPageState.Free;
        }

        /// <summary>查询物理页面归属的虚拟页面（反向查找）。</summary>
        public SparseShadowAtlasPage? GetOwner(PhysicalPageAddress addr)
        {
            int linear = (int)addr.LinearIndex;
            return linear >= 0 && linear < _owners.Length ? _owners[linear] : null;
        }

        /// <summary>重置统计计数器（每帧开始调用）。</summary>
        public void ResetStats() => EvictedCount = 0;

        /// <summary>释放所有物理页面（场景切换时调用）。</summary>
        public void Clear()
        {
            _freeList.Clear();
            _usedSet.Clear();
            for (int i = 0; i < _states.Length; i++)
            {
                _states[i] = PhysicalPageState.Free;
                _owners[i] = null;
                _freeList.Enqueue(i);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 脏页跟踪器 (DirtyPageTracker)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 脏页跟踪器。当光源移动或场景物体移动时，受影响的虚拟页面被标记为脏，
    /// 在 Pass 3 中重新渲染。避免每帧全量渲染阴影，大幅降低开销。
    /// </summary>
    public sealed class DirtyPageTracker
    {
        /// <summary>脏页集合：键为虚拟页面的线性索引 + 虚拟纹理索引组合键。</summary>
        private readonly HashSet<long> _dirtyPages = new();
        /// <summary>脏页列表（按加入顺序，供渲染遍历）。</summary>
        private readonly List<SparseShadowAtlasPage> _dirtyList = new();

        public int DirtyCount => _dirtyList.Count;

        /// <summary>组合键：(虚拟纹理索引, 虚拟页面线性索引)。</summary>
        private static long MakeKey(in SparseShadowAtlasPage p) =>
            ((long)p.VirtualTextureIndex << 40) | (uint)p.LinearIndex;

        /// <summary>标记单个页面为脏。</summary>
        public void MarkDirty(in SparseShadowAtlasPage page)
        {
            long key = MakeKey(in page);
            if (_dirtyPages.Add(key))
            {
                var dirty = page;
                dirty.Dirty = true;
                _dirtyList.Add(dirty);
            }
        }

        /// <summary>标记整个虚拟纹理的所有页面为脏（光源移动/首次分配时）。</summary>
        public void MarkAllDirty(int virtualTextureIndex, int lightIndex, int cubeFace = 0)
        {
            for (int mip = 0; mip < VSMConstants.MaxMipLevels; mip++)
            {
                int side = VSMConstants.PagesPerSide(mip);
                for (int py = 0; py < side; py++)
                    for (int px = 0; px < side; px++)
                        MarkDirty(new SparseShadowAtlasPage(mip, px, py, lightIndex, virtualTextureIndex, cubeFace));
            }
        }

        /// <summary>标记光源视锥体覆盖范围内的页面为脏（光源小幅移动时）。</summary>
        public void MarkRangeDirty(int virtualTextureIndex, int lightIndex, int mip,
            int pageX0, int pageY0, int pageX1, int pageY1, int cubeFace = 0)
        {
            int side = VSMConstants.PagesPerSide(mip);
            int x0 = Math.Clamp(pageX0, 0, side - 1);
            int x1 = Math.Clamp(pageX1, 0, side - 1);
            int y0 = Math.Clamp(pageY0, 0, side - 1);
            int y1 = Math.Clamp(pageY1, 0, side - 1);
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                    MarkDirty(new SparseShadowAtlasPage(mip, px, py, lightIndex, virtualTextureIndex, cubeFace));
        }

        /// <summary>获取当前所有脏页（只读快照）。</summary>
        public IReadOnlyList<SparseShadowAtlasPage> GetDirtyPages() => _dirtyList;

        /// <summary>清空脏页集合（Pass 3 渲染完成后调用）。</summary>
        public void Clear()
        {
            _dirtyPages.Clear();
            _dirtyList.Clear();
        }

        /// <summary>页面是否为脏。</summary>
        public bool IsDirty(in SparseShadowAtlasPage page) => _dirtyPages.Contains(MakeKey(in page));
    }

    // ──────────────────────────────────────────────────────────────
    // 物理阴影图集 (PhysicalShadowMap)：8K×8K 物理纹理
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 物理阴影图集。一张 8K×8K 的深度纹理，按 128×128 页面切分为 64×64=4096 个槽位。
    /// 所有虚拟纹理的可见页面最终都渲染到此图集的某个槽位。着色时通过页表查找槽位采样。
    /// </summary>
    public sealed class PhysicalShadowMap : IDisposable
    {
        /// <summary>RHI 设备引用。</summary>
        private readonly RHIDevice _device;
        /// <summary>物理深度图集纹理（8K×8K，深度格式）。</summary>
        public RHITexture? DepthAtlas { get; private set; }
        /// <summary>深度 SRV（着色阶段采样用）。</summary>
        public RHIDescriptorHandle DepthSrv { get; private set; }
        /// <summary>深度 DSV（页面渲染写入用，每个页面单独视口）。</summary>
        public RHIDescriptorHandle DepthDsv { get; private set; }
        /// <summary>阴影采样器（比较采样）。</summary>
        public RHIDescriptorHandle ShadowSampler { get; private set; }
        /// <summary>图集边长。</summary>
        public int AtlasSize => VSMConstants.PhysicalAtlasSize;

        public PhysicalShadowMap(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            CreateResources();
        }

        private void CreateResources()
        {
            // 创建 8K×8K 深度图集，启用深度模板 + 着色器资源访问。
            var depthDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = VSMConstants.PhysicalAtlasSize,
                Height = VSMConstants.PhysicalAtlasSize,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };
            DepthAtlas = _device.CreateTexture(depthDesc, "VSM_PhysicalDepthAtlas");
            DepthSrv = _device.CreateSRV(DepthAtlas);
            DepthDsv = _device.CreateDSV(DepthAtlas);
            ShadowSampler = _device.GetOrCreateSampler(RHISamplerDesc.ShadowSampler);
        }

        /// <summary>计算物理页面对应的物理图集视口（用于 Pass 3 渲染）。</summary>
        public RHIViewport GetPageViewport(PhysicalPageAddress addr) =>
            new(addr.PixelX, addr.PixelY, VSMConstants.PageSize, VSMConstants.PageSize, 0f, 1f);

        /// <summary>计算物理页面对应的物理图集裁剪矩形。</summary>
        public RHIScissorRect GetPageScissor(PhysicalPageAddress addr) =>
            new(addr.PixelX, addr.PixelY, addr.PixelX + VSMConstants.PageSize, addr.PixelY + VSMConstants.PageSize);

        /// <summary>清空整个物理图集深度（每帧或按需调用）。</summary>
        public void ClearDepth(RHICommandList cmd)
        {
            if (DepthAtlas == null) return;
            cmd.TransitionTo(DepthAtlas, RHIResourceState.DepthWrite);
            cmd.ClearDepthStencilView(DepthDsv, 1.0f, 0, 3);
        }

        public void Dispose()
        {
            DepthAtlas?.Dispose();
            DepthAtlas = null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 虚拟阴影图缓存 (SparseShadowAtlasCache)：LRU 页面缓存
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 虚拟阴影图缓存。封装单个虚拟纹理的页表 + 其页面的 LRU 缓存状态。
    /// 负责记录哪些页面在本帧被请求（可见）、维护帧龄、与全局页面分配器协作。
    /// </summary>
    public sealed class SparseShadowAtlasCache
    {
        /// <summary>该虚拟纹理的页表。</summary>
        public PageTable PageTable { get; }
        /// <summary>本帧请求的可见页面集合（Pass 1/2 填充）。</summary>
        private readonly HashSet<SparseShadowAtlasPage> _requestedPages = new();
        /// <summary>当前已缓存（已分配物理页面）的页面集合。</summary>
        private readonly HashSet<SparseShadowAtlasPage> _cachedPages = new();
        /// <summary>光源索引。</summary>
        public int LightIndex { get; }
        /// <summary>虚拟纹理索引（在光源内的编号，如 Clipmap 层级或立方体面）。</summary>
        public int VirtualTextureIndex { get; }
        /// <summary>立方体面（点光源 0..5，其它为 0）。</summary>
        public int CubeFace { get; }

        public SparseShadowAtlasCache(int lightIndex, int vtIndex, int cubeFace = 0)
        {
            LightIndex = lightIndex;
            VirtualTextureIndex = vtIndex;
            CubeFace = cubeFace;
            PageTable = new PageTable(vtIndex);
        }

        /// <summary>请求一个页面（Pass 2 中根据可见性调用）。</summary>
        public void RequestPage(in SparseShadowAtlasPage page) => _requestedPages.Add(page);

        /// <summary>本帧请求的可见页面（只读）。</summary>
        public IReadOnlyCollection<SparseShadowAtlasPage> RequestedPages => _requestedPages;

        /// <summary>已缓存页面集合。</summary>
        public IReadOnlyCollection<SparseShadowAtlasPage> CachedPages => _cachedPages;

        /// <summary>页面是否已缓存（已映射到物理页面）。</summary>
        public bool IsCached(in SparseShadowAtlasPage page) => _cachedPages.Contains(page);

        /// <summary>注册一个新分配的缓存页面。</summary>
        public void RegisterCached(in SparseShadowAtlasPage page)
        {
            _cachedPages.Add(page);
            PageTable.Touch(page);
        }

        /// <summary>注销一个被淘汰的缓存页面。</summary>
        public void UnregisterCached(in SparseShadowAtlasPage page) => _cachedPages.Remove(page);

        /// <summary>帧结束清理：清空本帧请求集合，递增页表帧龄。</summary>
        public void EndFrame()
        {
            _requestedPages.Clear();
            PageTable.IncrementAllAges();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 光源虚拟阴影图容器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 方向光 VSM：使用 Clipmap 级联分层。每层一个虚拟纹理，越靠近相机的层覆盖范围越小
    /// （精度越高），类似传统 CSM 但每层是完整的 16K 虚拟纹理，按需页面化。
    /// </summary>
    public sealed class DirectionalVSM
    {
        /// <summary>Clipmap 层级数（级联数，通常 4~5）。</summary>
        public int ClipCount { get; }
        /// <summary>每层的虚拟阴影图缓存。</summary>
        public List<SparseShadowAtlasCache> Caches { get; } = new();
        /// <summary>每层的视锥分割距离。</summary>
        public float[] SplitDistances { get; }
        /// <summary>每层的光源 ViewProjection 矩阵。</summary>
        public Matrix4x4[] ViewProjections { get; }
        /// <summary>光源索引。</summary>
        public int LightIndex { get; }
        /// <summary>光源方向。</summary>
        public Vector3 LightDirection { get; set; }

        public DirectionalVSM(int lightIndex, int clipCount)
        {
            LightIndex = lightIndex;
            ClipCount = Math.Clamp(clipCount, 1, 8);
            SplitDistances = new float[ClipCount];
            ViewProjections = new Matrix4x4[ClipCount];
            for (int i = 0; i < ClipCount; i++)
                Caches.Add(new SparseShadowAtlasCache(lightIndex, i, 0));
        }
    }

    /// <summary>
    /// 点光源 VSM：立方体贴图虚拟化。6 个面各一个 16K 虚拟纹理。
    /// 采样时通过立方体面 + 页表查找物理页面。
    /// </summary>
    public sealed class PointVSM
    {
        /// <summary>6 个面的虚拟阴影图缓存。</summary>
        public List<SparseShadowAtlasCache> Caches { get; } = new();
        /// <summary>6 个面的 ViewProjection 矩阵。</summary>
        public Matrix4x4[] ViewProjections { get; } = new Matrix4x4[6];
        /// <summary>光源索引。</summary>
        public int LightIndex { get; }
        /// <summary>光源位置。</summary>
        public Vector3 LightPosition { get; set; }
        /// <summary>光源范围。</summary>
        public float Range { get; set; }

        public PointVSM(int lightIndex)
        {
            LightIndex = lightIndex;
            for (int face = 0; face < 6; face++)
                Caches.Add(new SparseShadowAtlasCache(lightIndex, face, face));
        }
    }

    /// <summary>
    /// 聚光灯 VSM：单张 16K 虚拟纹理。
    /// </summary>
    public sealed class SpotVSM
    {
        /// <summary>虚拟阴影图缓存。</summary>
        public SparseShadowAtlasCache Cache { get; }
        /// <summary>光源 ViewProjection 矩阵。</summary>
        public Matrix4x4 ViewProjection { get; set; } = Matrix4x4.Identity;
        /// <summary>光源索引。</summary>
        public int LightIndex { get; }
        /// <summary>光源位置。</summary>
        public Vector3 LightPosition { get; set; }
        /// <summary>光源方向。</summary>
        public Vector3 LightDirection { get; set; }

        public SpotVSM(int lightIndex)
        {
            LightIndex = lightIndex;
            Cache = new SparseShadowAtlasCache(lightIndex, 0, 0);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // GPU 资源容器（与 RHI 对接）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// VSM 系统的全部 GPU 资源。包含物理图集、页表缓冲、页面列表缓冲、
    /// 常量缓冲以及计算着色器/图形管线状态对象(PSO)。
    /// </summary>
    public sealed class VSMGpuResources : IDisposable
    {
        /// <summary>物理阴影图集。</summary>
        public PhysicalShadowMap? PhysicalMap { get; set; }
        /// <summary>页表结构化缓冲（UAV 读写）。每个虚拟纹理一份；这里合并为大缓冲。</summary>
        public RHIBuffer? PageTableBuffer { get; set; }
        /// <summary>页表 UAV 描述符。</summary>
        public RHIDescriptorHandle PageTableUav { get; set; }
        /// <summary>页表 SRV 描述符（着色阶段只读访问）。</summary>
        public RHIDescriptorHandle PageTableSrv { get; set; }
        /// <summary>可见页面请求列表缓冲（UAV，Pass 2 填充）。</summary>
        public RHIBuffer? PageRequestBuffer { get; set; }
        public RHIDescriptorHandle PageRequestUav { get; set; }
        /// <summary>脏页面渲染任务列表缓冲（UAV，Pass 3 消费）。</summary>
        public RHIBuffer? PageRenderTaskBuffer { get; set; }
        public RHIDescriptorHandle PageRenderTaskUav { get; set; }
        /// <summary>间接绘制参数缓冲（Pass 3 间接绘制用）。</summary>
        public RHIBuffer? IndirectArgsBuffer { get; set; }
        /// <summary>VSM 全局常量缓冲。</summary>
        public RHIBuffer? VSMConstantsCB { get; set; }
        /// <summary>页面分配计算 PSO（Pass 2）。</summary>
        public RHIPipelineState? PageAllocationPSO { get; set; }
        /// <summary>页面渲染图形 PSO（Pass 3，深度写入）。</summary>
        public RHIPipelineState? PageRenderPSO { get; set; }
        /// <summary>页面渲染根签名。</summary>
        public RHIRootSignature? PageRenderRootSig { get; set; }
        /// <summary>页面分配计算根签名。</summary>
        public RHIRootSignature? PageAllocRootSig { get; set; }
        /// <summary>计算命令列表（异步计算队列）。</summary>
        public RHICommandList? ComputeCmdList { get; set; }

        public void Dispose()
        {
            PhysicalMap?.Dispose();
            PageTableBuffer?.Dispose();
            PageRequestBuffer?.Dispose();
            PageRenderTaskBuffer?.Dispose();
            IndirectArgsBuffer?.Dispose();
            VSMConstantsCB?.Dispose();
            PageAllocationPSO?.Dispose();
            PageRenderPSO?.Dispose();
            PageRenderRootSig?.Dispose();
            PageAllocRootSig?.Dispose();
            ComputeCmdList?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 着色器常量缓冲结构（匹配 HLSL 布局）
    // ──────────────────────────────────────────────────────────────

    /// <summary>VSM 全局常量（匹配 HLSL cbuffer VSMConstantsCB）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VSMConstantsCB
    {
        /// <summary>虚拟纹理边长（像素）。</summary>
        public int VirtualTextureSize;
        /// <summary>页面边长（像素）。</summary>
        public int PageSize;
        /// <summary>物理图集边长（像素）。</summary>
        public int PhysicalAtlasSize;
        /// <summary>物理图集每边页面数。</summary>
        public int PhysicalPagesPerSide;
        /// <summary>最大 Mip 级数。</summary>
        public int MaxMipLevels;
        /// <summary>无效物理页面哨兵。</summary>
        public uint InvalidPhysicalPage;
        /// <summary>页表索引位宽。</summary>
        public int PageTableIndexBits;
        /// <summary>对齐填充。</summary>
        public float Pad0;
        /// <summary>光源 ViewProjection 矩阵（当前正在处理的层）。</summary>
        public Matrix4x4 LightViewProjection;
        /// <summary>光源位置。</summary>
        public Vector3 LightPosition;
        /// <summary>光源范围。</summary>
        public float LightRange;
        /// <summary>光源方向。</summary>
        public Vector3 LightDirection;
        /// <summary>光源类型（0=方向, 1=点, 2=聚光）。</summary>
        public int LightType;

        public static VSMConstantsCB Default => new()
        {
            VirtualTextureSize = VSMConstants.VirtualTextureSize,
            PageSize = VSMConstants.PageSize,
            PhysicalAtlasSize = VSMConstants.PhysicalAtlasSize,
            PhysicalPagesPerSide = VSMConstants.PhysicalPagesPerSide,
            MaxMipLevels = VSMConstants.MaxMipLevels,
            InvalidPhysicalPage = VSMConstants.InvalidPhysicalPage,
            PageTableIndexBits = VSMConstants.PageTableIndexBits
        };
    }

    /// <summary>页面渲染任务（Pass 3 消费，匹配 HLSL 结构化缓冲）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VSMPageRenderTask
    {
        /// <summary>虚拟页面 Mip。</summary>
        public int Mip;
        /// <summary>虚拟页面 X。</summary>
        public int PageX;
        /// <summary>虚拟页面 Y。</summary>
        public int PageY;
        /// <summary>虚拟纹理索引。</summary>
        public int VirtualTextureIndex;
        /// <summary>物理页面线性索引（12 位有效）。</summary>
        public uint PhysicalPageIndex;
        /// <summary>立方体面。</summary>
        public int CubeFace;
        /// <summary>光源索引。</summary>
        public int LightIndex;
        /// <summary>对齐填充。</summary>
        public float Pad;
    }

    /// <summary>页面请求项（Pass 2 填充，匹配 HLSL 结构化缓冲）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VSMPageRequest
    {
        public int Mip;
        public int PageX;
        public int PageY;
        public int VirtualTextureIndex;
        public int CubeFace;
        public int LightIndex;
        public int LightType;
        public int Pad;
    }

    // ──────────────────────────────────────────────────────────────
    // VSM 统计信息
    // ──────────────────────────────────────────────────────────────

    /// <summary>VSM 系统每帧统计信息。用于回填 FrameStats 与性能分析。</summary>
    public sealed class VSMStats
    {
        /// <summary>本帧请求的可见页面数。</summary>
        public int RequestedPages;
        /// <summary>本帧分配的物理页面数。</summary>
        public int AllocatedPages;
        /// <summary>本帧淘汰的物理页面数。</summary>
        public int EvictedPages;
        /// <summary>本帧渲染的脏页面数。</summary>
        public int RenderedPages;
        /// <summary>当前缓存命中数（已映射无需重分配）。</summary>
        public int CacheHits;
        /// <summary>物理图集利用率（0..1）。</summary>
        public float AtlasUtilization;
        /// <summary>VSM 是否可用（硬件支持检查通过）。</summary>
        public bool VSMAvailable;

        /// <summary>回填到引擎 FrameStats 的相关字段。</summary>
        public void ApplyTo(FrameStats stats)
        {
            stats.SparseShadowAtlasAllocatedPages = AllocatedPages;
            stats.SparseShadowAtlasEvictedPages = EvictedPages;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 阴影提供者接口（与 ShadowSystem 集成）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 阴影提供者接口。ShadowSystem 通过此接口抽象阴影后端，
    /// VSM 与传统 CSM 均可实现该接口，便于运行时切换与回退。
    /// </summary>
    public interface IShadowProvider
    {
        /// <summary>提供者名称。</summary>
        string Name { get; }
        /// <summary>是否可用（硬件/配置检查）。</summary>
        bool IsAvailable { get; }
        /// <summary>构建当前帧的阴影（分配、计算可见性）。</summary>
        void BuildFrameShadows(IRenderScene scene, RenderCamera camera);
        /// <summary>记录阴影深度渲染到命令列表。</summary>
        void RenderShadowDepth(RHICommandList cmd, IReadOnlyList<RenderMesh> shadowCasters);
        /// <summary>获取本帧统计信息。</summary>
        VSMStats GetStats();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 虚拟阴影图管理器 (SparseShadowAtlasManager) —— 主协调器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 虚拟阴影图系统主管理器。实现 <see cref="IShadowProvider"/> 接口，
    /// 作为 <see cref="ShadowSystem"/> 的阴影提供者之一。
    /// 当 VSM 不可用（如硬件不支持所需特性）时，自动回退到传统 CSM。
    /// </summary>
    public sealed class SparseShadowAtlasManager : IShadowProvider, IDisposable
    {
        // ── 名称与可用性 ──
        public string Name => "SparseShadowAtlases";
        private readonly RHIDevice _device;
        private readonly ShadowSettings _settings;
        private readonly VSMStats _stats = new();
        private bool _availabilityChecked;

        // ── 核心组件 ──
        /// <summary>全局物理页面分配器（所有光源共享物理图集）。</summary>
        private readonly PageAllocator _pageAllocator = new();
        /// <summary>脏页跟踪器。</summary>
        private readonly DirtyPageTracker _dirtyTracker = new();
        /// <summary>所有虚拟纹理的页表集合（供分配器淘汰时反向查找）。</summary>
        private readonly List<PageTable> _allPageTables = new();
        /// <summary>所有虚拟阴影图缓存集合。</summary>
        private readonly List<SparseShadowAtlasCache> _allCaches = new();

        // ── 光源 VSM 容器 ──
        private readonly List<DirectionalVSM> _directionalVSMs = new();
        private readonly List<PointVSM> _pointVSMs = new();
        private readonly List<SpotVSM> _spotVSMs = new();

        // ── GPU 资源 ──
        private VSMGpuResources? _gpu;

        // ── 回退提供者（CSM）──
        /// <summary>传统 CSM 回退提供者。VSM 不可用时使用。</summary>
        private readonly ShadowSystem _fallbackCsm;
        private bool _useFallback;

        /// <summary>是否使用 CSM 回退。</summary>
        public bool IsUsingFallback => _useFallback;
        /// <summary>VSM 是否可用。</summary>
        public bool IsAvailable
        {
            get
            {
                if (!_availabilityChecked) CheckAvailability();
                return !_useFallback;
            }
        }

        public SparseShadowAtlasManager(RHIDevice device, ShadowSettings? settings = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _settings = settings ?? new ShadowSettings();
            // 回退到传统 ShadowSystem（CSM）。
            _fallbackCsm = new ShadowSystem(_settings);
            CheckAvailability();
        }

        /// <summary>检查硬件/特性是否支持 VSM。</summary>
        private void CheckAvailability()
        {
            _availabilityChecked = true;
            // VSM 需要 UAV 读写、足够大的纹理支持与计算着色器。
            // 这里根据 RHI 设备能力判断；不支持则回退 CSM。
            try
            {
                // 物理图集 8K 纹理需要设备支持。检查是否支持 8K 纹理维度。
                // 实际工程中可查询设备 MaxTextureDimension；此处保守判断设备非空。
                _useFallback = _device == null;
                _stats.VSMAvailable = !_useFallback;
            }
            catch
            {
                _useFallback = true;
                _stats.VSMAvailable = false;
            }
        }

        // ──────────────────────────────────────────────────────────
        // IShadowProvider 实现
        // ──────────────────────────────────────────────────────────

        /// <summary>构建当前帧的 VSM 阴影（Pass 1 + Pass 2）。</summary>
        public void BuildFrameShadows(IRenderScene scene, RenderCamera camera)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (camera == null) throw new ArgumentNullException(nameof(camera));

            // 回退路径：委托给传统 CSM。
            if (_useFallback)
            {
                _fallbackCsm.BuildFrameShadows(scene, camera);
                return;
            }

            ResetFrameStats();
            EnsureGpuResources();
            UpdateLightContainers(scene, camera);

            // Pass 1: 深度预计算 —— 确定每个光源/层的可见页面并请求。
            Pass1_DepthPrepass(scene, camera);

            // Pass 2: 页面分配 —— 为请求的页面分配物理页面，淘汰 LRU。
            Pass2_AllocatePages();
        }

        /// <summary>记录 VSM 阴影深度渲染（Pass 3）。</summary>
        public void RenderShadowDepth(RHICommandList cmd, IReadOnlyList<RenderMesh> shadowCasters)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));

            // 回退路径。
            if (_useFallback)
            {
                var res = _fallbackCsm.EnsureResources(_device);
                _fallbackCsm.RenderShadowDepth(cmd, shadowCasters, res);
                return;
            }
            if (_gpu?.PhysicalMap == null) return;

            // Pass 3: 页面渲染 —— 仅渲染脏页面到物理图集。
            Pass3_RenderPages(cmd, shadowCasters);
        }

        public VSMStats GetStats() => _stats;

        // ──────────────────────────────────────────────────────────
        // Pass 1: 深度预计算 —— 确定可见页面
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pass 1: 深度预计算。遍历每个光源的每个虚拟纹理，根据相机视锥与光源视锥
        /// 计算屏幕空间可见性，确定哪些虚拟页面需要被请求。
        /// 在完整实现中，这一步读取深度预渲染 Pass 的 HZB（层级 Z 缓冲）来确定页面可见性；
        /// 此处基于相机视锥与光源 ViewProjection 的覆盖范围近似计算。
        /// </summary>
        private void Pass1_DepthPrepass(IRenderScene scene, RenderCamera camera)
        {
            var camPos = ExtractTranslation(camera.WorldMatrix);
            var camForward = ExtractForward(camera.WorldMatrix);
            var camUp = ExtractUp(camera.WorldMatrix);
            var cameraNear = camera.NearClip;
            var cameraFar = Math.Min(camera.FarClip, _settings.MaxShadowDistance);

            // 方向光：按 Clipmap 层级请求页面。
            foreach (var dirVsm in _directionalVSMs)
            {
                var light = scene.Lights.FirstOrDefault(l => l.ObjectId != Guid.Empty && IsDirectional(l));
                if (light == null) continue;
                var lightDir = ExtractForward(light.WorldMatrix);
                dirVsm.LightDirection = lightDir;

                // 计算各 Clipmap 层的分割距离（复用传统 CSM 分割算法）。
                var splits = CascadeSplitCalculator.Compute(
                    dirVsm.ClipCount, cameraNear, cameraFar,
                    _settings.SplitScheme, _settings.CascadeSplitLambda, _settings.ManualSplitRatios);
                for (int i = 0; i < dirVsm.ClipCount; i++)
                    dirVsm.SplitDistances[i] = splits[i];

                // 构建相机 ViewProjection 用于提取视锥角点。
                var viewProj = Matrix4x4.CreateLookAt(camPos, camPos + camForward, camUp) *
                               Matrix4x4.CreatePerspectiveFieldOfView(
                                   camera.FieldOfView * MathF.PI / 180f, camera.AspectRatio, cameraNear, cameraFar);

                // 为每层计算光源 ViewProjection 并确定可见页面。
                float prevNear = cameraNear;
                for (int clip = 0; clip < dirVsm.ClipCount; clip++)
                {
                    float far = splits[clip];
                    var corners = ShadowFrustumComputer.ExtractFrustumCorners(
                        viewProj, prevNear, far, cameraNear, cameraFar);
                    var cascade = ShadowFrustumComputer.ComputeDirectionalCascade(
                        clip, corners, lightDir, VSMConstants.VirtualTextureSize, true, _settings.DirectionalBias);
                    dirVsm.ViewProjections[clip] = cascade.ViewProjectionMatrix;

                    // 请求该层虚拟纹理的可见页面。
                    RequestVisiblePagesForDirectional(dirVsm, clip, camera, prevNear, far);
                    prevNear = far;
                }
            }

            // 点光源：6 个面请求页面。
            foreach (var pointVsm in _pointVSMs)
            {
                var light = scene.Lights.FirstOrDefault(l => l.ObjectId != Guid.Empty && IsPoint(l));
                if (light == null) continue;
                pointVsm.LightPosition = ExtractTranslation(light.WorldMatrix);
                pointVsm.Range = Math.Max(0.1f, light.Range);
                var near = _settings.PointShadowNearPlane;
                var matrices = ShadowFrustumComputer.ComputePointShadowMatrices(
                    pointVsm.LightPosition, pointVsm.Range, near);
                for (int face = 0; face < 6; face++)
                {
                    pointVsm.ViewProjections[face] = matrices[face];
                    RequestVisiblePagesForPoint(pointVsm, face);
                }
            }

            // 聚光灯：单虚拟纹理请求页面。
            foreach (var spotVsm in _spotVSMs)
            {
                var light = scene.Lights.FirstOrDefault(l => l.ObjectId != Guid.Empty && IsSpot(l));
                if (light == null) continue;
                spotVsm.LightPosition = ExtractTranslation(light.WorldMatrix);
                spotVsm.LightDirection = ExtractForward(light.WorldMatrix);
                spotVsm.ViewProjection = ShadowFrustumComputer.ComputeSpotShadowMatrix(
                    spotVsm.LightPosition, spotVsm.LightDirection,
                    MathF.PI * 0.25f, Math.Max(0.1f, light.Range), _settings.PointShadowNearPlane);
                RequestVisiblePagesForSpot(spotVsm, camera);
            }
        }

        /// <summary>方向光：请求某 Clipmap 层的可见页面。</summary>
        private void RequestVisiblePagesForDirectional(DirectionalVSM dirVsm, int clip,
            RenderCamera camera, float near, float far)
        {
            var cache = dirVsm.Caches[clip];
            var vp = dirVsm.ViewProjections[clip];
            if (!Matrix4x4.Invert(vp, out var invVp)) return;

            // 将相机视锥角点变换到光源 UV 空间，确定覆盖的页面范围。
            // 简化：根据距相机距离选择合适 Mip（近用高 Mip 精度，远用低 Mip）。
            int mip = SelectMipByDistance(near, far, camera);
            int side = VSMConstants.PagesPerSide(mip);

            // 请求覆盖区域内的页面。完整实现会用屏幕像素覆盖率精确选择 Mip。
            int halfCover = Math.Max(1, side / 4);
            int centerX = side / 2, centerY = side / 2;
            for (int py = centerY - halfCover; py <= centerY + halfCover; py++)
                for (int px = centerX - halfCover; px <= centerX + halfCover; px++)
                {
                    if (px < 0 || px >= side || py < 0 || py >= side) continue;
                    var page = new SparseShadowAtlasPage(mip, px, py, dirVsm.LightIndex, clip, 0);
                    cache.RequestPage(page);
                    _stats.RequestedPages++;
                }
        }

        /// <summary>点光源：请求某立方体面的可见页面。</summary>
        private void RequestVisiblePagesForPoint(PointVSM pointVsm, int face)
        {
            var cache = pointVsm.Caches[face];
            // 点光源近距离需要高精度（低 Mip），远距离低精度。
            // 简化：请求 Mip 0~3 的中心页面。
            for (int mip = 0; mip <= 3; mip++)
            {
                int side = VSMConstants.PagesPerSide(mip);
                int half = Math.Max(1, side / 4);
                int cx = side / 2, cy = side / 2;
                for (int py = cy - half; py <= cy + half; py++)
                    for (int px = cx - half; px <= cx + half; px++)
                    {
                        if (px < 0 || px >= side || py < 0 || py >= side) continue;
                        var page = new SparseShadowAtlasPage(mip, px, py, pointVsm.LightIndex, face, face);
                        cache.RequestPage(page);
                        _stats.RequestedPages++;
                    }
            }
        }

        /// <summary>聚光灯：请求虚拟纹理的可见页面。</summary>
        private void RequestVisiblePagesForSpot(SpotVSM spotVsm, RenderCamera camera)
        {
            var cache = spotVsm.Cache;
            // 聚光灯光锥覆盖区域，请求高精度页面。
            int mip = 0; // 聚光灯通常需要较高精度
            int side = VSMConstants.PagesPerSide(mip);
            int half = Math.Max(1, side / 3);
            int cx = side / 2, cy = side / 2;
            for (int py = cy - half; py <= cy + half; py++)
                for (int px = cx - half; px <= cx + half; px++)
                {
                    if (px < 0 || px >= side || py < 0 || py >= side) continue;
                    var page = new SparseShadowAtlasPage(mip, px, py, spotVsm.LightIndex, 0, 0);
                    cache.RequestPage(page);
                    _stats.RequestedPages++;
                }
        }

        /// <summary>根据距相机距离选择合适 Mip 级别（Mip 流式核心）。</summary>
        private static int SelectMipByDistance(float near, float far, RenderCamera camera)
        {
            // 距离越近，屏幕像素覆盖率越高，需要越高精度（低 Mip）。
            // 距离越远，可用低精度（高 Mip）。
            float midDist = (near + far) * 0.5f;
            float shadowDist = Math.Max(1f, _settings.MaxShadowDistance);
            float t = Math.Clamp(midDist / shadowDist, 0f, 1f);
            // t=0 → mip 0（高精度），t=1 → mip 7（低精度）
            return Math.Clamp((int)(t * (VSMConstants.MaxMipLevels - 1)), 0, VSMConstants.MaxMipLevels - 1);
        }

        // ──────────────────────────────────────────────────────────
        // Pass 2: 页面分配 —— 为可见页面分配物理页面
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pass 2: 页面分配。遍历所有请求的可见页面：
        ///  - 若已缓存（页表已映射）→ 命中，Touch 帧龄；
        ///  - 若未缓存 → 通过 PageAllocator 分配物理页面，建立页表映射，标记为脏。
        /// 物理页面耗尽时由分配器自动 LRU 淘汰。
        /// </summary>
        private void Pass2_AllocatePages()
        {
            _pageAllocator.ResetStats();
            _dirtyTracker.Clear();

            foreach (var cache in _allCaches)
            {
                foreach (var page in cache.RequestedPages)
                {
                    if (cache.IsCached(page))
                    {
                        // 缓存命中：刷新帧龄，无需重分配。
                        cache.PageTable.Touch(page);
                        _stats.CacheHits++;
                        continue;
                    }

                    // 未命中：分配物理页面。
                    var physical = _pageAllocator.Allocate(_allPageTables, page);
                    if (!physical.HasValue)
                    {
                        // 分配失败（物理图集已满且无可淘汰）——跳过该页面。
                        continue;
                    }

                    // 建立页表映射并注册缓存。
                    cache.PageTable.Map(page, physical.Value);
                    cache.RegisterCached(page);

                    // 新分配页面必然为脏，需在 Pass 3 渲染。
                    _dirtyTracker.MarkDirty(page);
                    _stats.AllocatedPages++;
                }
            }

            _stats.EvictedPages = _pageAllocator.EvictedCount;
            _stats.AtlasUtilization = (float)_pageAllocator.AllocatedCount / VSMConstants.TotalPhysicalPages;
        }

        // ──────────────────────────────────────────────────────────
        // Pass 3: 页面渲染 —— 仅渲染脏页面到物理图集
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pass 3: 页面渲染。遍历脏页列表，为每个脏页：
        ///  - 通过页表查找物理页面地址；
        ///  - 在物理图集对应槽位设置视口/裁剪；
        ///  - 用光源 ViewProjection 渲染阴影投射几何的深度。
        /// 这是 VSM 性能优势的核心：只渲染脏页面而非整张阴影图。
        /// </summary>
        private void Pass3_RenderPages(RHICommandList cmd, IReadOnlyList<RenderMesh> shadowCasters)
        {
            var physical = _gpu!.PhysicalMap!;
            cmd.TransitionTo(physical.DepthAtlas!, RHIResourceState.DepthWrite);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

            // 可选：仅清空脏页面区域而非整张图集（已分配页面的脏区域）。
            // 为简化，首次分配时清空整个图集；后续仅渲染脏页面覆盖。
            foreach (var page in _dirtyTracker.GetDirtyPages())
            {
                RenderDirtyPage(cmd, shadowCasters, page);
                _stats.RenderedPages++;
            }

            _dirtyTracker.Clear();

            // 渲染完成后转换为像素着色器资源，供 Pass 4 采样。
            cmd.TransitionTo(physical.DepthAtlas!, RHIResourceState.PixelShaderResource);
        }

        /// <summary>渲染单个脏页面到物理图集。</summary>
        private void RenderDirtyPage(RHICommandList cmd, IReadOnlyList<RenderMesh> casters, SparseShadowAtlasPage page)
        {
            // 通过页表查找物理页面地址。
            var cache = FindCache(page.LightIndex, page.VirtualTextureIndex, page.CubeFace);
            if (cache == null) return;
            var physicalAddr = cache.PageTable.Resolve(page);
            if (!physicalAddr.HasValue) return;

            var physical = _gpu!.PhysicalMap!;
            // 设置物理图集上该页面的视口与裁剪矩形。
            cmd.SetViewport(physical.GetPageViewport(physicalAddr.Value));
            cmd.SetScissorRect(physical.GetPageScissor(physicalAddr.Value));

            // 获取该页面对应的光源 ViewProjection。
            var vp = GetLightViewProjection(page);
            if (vp == Matrix4x4.Identity) return;

            // 渲染阴影投射几何到该页面。实际工程中会做光源视锥裁剪，
            // 仅渲染与该页面投影区域相交的网格。此处简化为遍历所有投射者。
            foreach (var mesh in casters)
            {
                if (mesh == null || !mesh.CastShadow) continue;
                RecordPageShadowDraw(cmd, mesh, vp, page);
            }
        }

        /// <summary>记录单次页面阴影深度绘制（与 ShadowSystem.RecordShadowDraw 对齐）。</summary>
        private static void RecordPageShadowDraw(RHICommandList cmd, RenderMesh mesh, Matrix4x4 vp, SparseShadowAtlasPage page)
        {
            // 世界-视图-投影矩阵。
            var wvp = mesh.WorldMatrix * vp;
            // 实际工程：绑定阴影深度 PSO（仅位置、前向剔除、深度偏置），
            // 通过每绘制常量缓冲上传 wvp，调用 DrawIndexedInstanced。
            // 此处保留与 ShadowSystem 一致的签名占位，由场景渲染器驱动真实绘制。
            _ = wvp;
            _ = page;
        }

        /// <summary>查找虚拟页面对应的缓存。</summary>
        private SparseShadowAtlasCache? FindCache(int lightIndex, int vtIndex, int cubeFace)
        {
            // 方向光：lightIndex + vtIndex(clip)
            foreach (var d in _directionalVSMs)
                if (d.LightIndex == lightIndex)
                    return d.Caches[vtIndex];
            // 点光源：lightIndex + cubeFace
            foreach (var p in _pointVSMs)
                if (p.LightIndex == lightIndex)
                    return p.Caches[cubeFace];
            // 聚光灯：lightIndex
            foreach (var s in _spotVSMs)
                if (s.LightIndex == lightIndex)
                    return s.Cache;
            return null;
        }

        /// <summary>获取虚拟页面对应的光源 ViewProjection 矩阵。</summary>
        private Matrix4x4 GetLightViewProjection(SparseShadowAtlasPage page)
        {
            foreach (var d in _directionalVSMs)
                if (d.LightIndex == page.LightIndex && page.VirtualTextureIndex < d.ViewProjections.Length)
                    return d.ViewProjections[page.VirtualTextureIndex];
            foreach (var p in _pointVSMs)
                if (p.LightIndex == page.LightIndex && page.CubeFace < p.ViewProjections.Length)
                    return p.ViewProjections[page.CubeFace];
            foreach (var s in _spotVSMs)
                if (s.LightIndex == page.LightIndex)
                    return s.ViewProjection;
            return Matrix4x4.Identity;
        }

        // ──────────────────────────────────────────────────────────
        // Pass 4: 采样（着色阶段，通过页表查找物理页面采样）
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Pass 4: 阴影采样。在光照着色阶段调用：给定世界坐标与光源信息，
        /// 通过页表查找物理页面地址，在物理图集上采样阴影深度。
        /// 此方法提供 CPU 端参考实现；实际采样在像素着色器中通过 SRV 读取页表缓冲完成。
        /// </summary>
        /// <param name="worldPos">世界空间坐标。</param>
        /// <param name="lightIndex">光源索引。</param>
        /// <param name="vtIndex">虚拟纹理索引（方向光为 Clipmap 层，点光为立方体面）。</param>
        /// <param name="vp">光源 ViewProjection 矩阵。</param>
        /// <returns>阴影可见性 [0,1]（1=完全照亮，0=完全阴影）。</returns>
        public float SampleShadow(Vector3 worldPos, int lightIndex, int vtIndex, Matrix4x4 vp)
        {
            if (_useFallback || _gpu?.PhysicalMap?.DepthAtlas == null) return 1f;

            // 将世界坐标变换到光源裁剪空间 → NDC → 虚拟纹理 UV。
            var clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
            if (clip.W <= 0f) return 1f;
            var ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
            // NDC [-1,1] → UV [0,1]
            float u = ndc.X * 0.5f + 0.5f;
            float v = ndc.Y * 0.5f + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return 1f;

            // 选择 Mip：基于屏幕空间导数（完整实现用 ddx/ddy）。
            int mip = 0;

            // 计算虚拟页面坐标。
            int side = VSMConstants.PagesPerSide(mip);
            int pageX = Math.Clamp((int)(u * side), 0, side - 1);
            int pageY = Math.Clamp((int)(v * side), 0, side - 1);
            var page = new SparseShadowAtlasPage(mip, pageX, pageY, lightIndex, vtIndex, vtIndex);

            // 通过页表查找物理页面地址。
            var cache = FindCache(lightIndex, vtIndex, vtIndex);
            if (cache == null) return 1f;
            var physicalAddr = cache.PageTable.Resolve(page);
            if (!physicalAddr.HasValue) return 1f; // 未映射 → 视为照亮

            // 计算物理图集采样 UV。
            // 页面内偏移：(u*side - pageX) * PageSize
            float localU = u * side - pageX;
            float localV = v * side - pageY;
            float physU = (physicalAddr.Value.PixelX + localU * VSMConstants.PageSize) / VSMConstants.PhysicalAtlasSize;
            float physV = (physicalAddr.Value.PixelY + localV * VSMConstants.PageSize) / VSMConstants.PhysicalAtlasSize;

            // 深度比较（CPU 端参考实现；实际在 GPU 着色器用 ShadowSampler 比较采样）。
            float receiverDepth = ndc.Z;
            // 此处简化返回：实际应采样物理图集深度并与 receiverDepth 比较。
            _ = physU; _ = physV;
            return receiverDepth <= 1f ? 1f : 0f;
        }

        // ──────────────────────────────────────────────────────────
        // GPU 资源初始化（与 RHI 严格对接）
        // ──────────────────────────────────────────────────────────

        /// <summary>确保 GPU 资源已创建（惰性初始化）。</summary>
        private void EnsureGpuResources()
        {
            if (_gpu != null) return;
            _gpu = new VSMGpuResources();

            // 物理阴影图集（8K×8K 深度纹理 + SRV/DSV/Sampler）。
            _gpu.PhysicalMap = new PhysicalShadowMap(_device);

            // 页表结构化缓冲（UAV 读写）。每个虚拟纹理一份页表；这里分配最大可能容量。
            int pageTableEntries = VSMConstants.TotalVirtualPages;
            var pageTableDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(pageTableEntries * sizeof(uint)),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                HeapType = RHIResourceHeapType.Default,
                Stride = sizeof(uint)
            };
            _gpu.PageTableBuffer = _device.CreateBuffer(pageTableDesc, "VSM_PageTableBuffer");
            _gpu.PageTableUav = _device.CreateUAV(_gpu.PageTableBuffer, RHIFormat.R32_UInt, true, 0, (uint)pageTableEntries, sizeof(uint));
            _gpu.PageTableSrv = _device.CreateSRV(_gpu.PageTableBuffer, RHIFormat.R32_UInt, true, 0, (uint)pageTableEntries, sizeof(uint));

            // 页面请求列表缓冲（Pass 2 计算填充）。
            int maxRequests = VSMConstants.TotalPhysicalPages * 2;
            var requestDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(maxRequests * Marshal.SizeOf<VSMPageRequest>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess,
                HeapType = RHIResourceHeapType.Default,
                Stride = Marshal.SizeOf<VSMPageRequest>()
            };
            _gpu.PageRequestBuffer = _device.CreateBuffer(requestDesc, "VSM_PageRequestBuffer");
            _gpu.PageRequestUav = _device.CreateUAV(_gpu.PageRequestBuffer, RHIFormat.Unknown, true, 0, (uint)maxRequests, Marshal.SizeOf<VSMPageRequest>());

            // 脏页面渲染任务缓冲（Pass 3 消费）。
            int maxTasks = VSMConstants.TotalPhysicalPages;
            var taskDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(maxTasks * Marshal.SizeOf<VSMPageRenderTask>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                HeapType = RHIResourceHeapType.Default,
                Stride = Marshal.SizeOf<VSMPageRenderTask>()
            };
            _gpu.PageRenderTaskBuffer = _device.CreateBuffer(taskDesc, "VSM_PageRenderTaskBuffer");
            _gpu.PageRenderTaskUav = _device.CreateUAV(_gpu.PageRenderTaskBuffer, RHIFormat.Unknown, true, 0, (uint)maxTasks, Marshal.SizeOf<VSMPageRenderTask>());

            // 间接绘制参数缓冲（Pass 3 间接绘制）。
            var indirectDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)AlignUp(4 * sizeof(uint), 256), // DrawIndexedInstanced args
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.IndirectArgs,
                HeapType = RHIResourceHeapType.Default,
                Stride = 0
            };
            _gpu.IndirectArgsBuffer = _device.CreateBuffer(indirectDesc, "VSM_IndirectArgsBuffer");

            // VSM 全局常量缓冲。
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)AlignUp(Marshal.SizeOf<VSMConstantsCB>(), 256),
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Default
            };
            _gpu.VSMConstantsCB = _device.CreateBuffer(cbDesc, "VSM_ConstantsCB");
            _device.UploadToBuffer(_gpu.VSMConstantsCB, new[] { VSMConstantsCB.Default });

            // 计算命令列表（异步计算队列）。
            _gpu.ComputeCmdList = _device.CreateCommandList(RHIQueueType.Compute, "VSM_ComputeCmdList");

            // 计算着色器 PSO 与根签名（页面分配 Pass）。
            // 注：实际着色器字节码由着色器编译管线提供；此处创建空 PSO 占位。
            CreatePageAllocationPso();

            // 收集所有页表供分配器淘汰时反向查找。
            RebuildPageTableList();
        }

        /// <summary>创建页面分配计算 PSO 与根签名。</summary>
        private void CreatePageAllocationPso()
        {
            // 根签名：CBV(0) + UAV(页表,1) + UAV(请求列表,2) + UAV(渲染任务,3) + UAV(间接参数,4)
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.CBV, NumDescriptors = 1, BaseShaderRegister = 0 }
                            }
                        }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.All,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 1 }
                            }
                        }
                    }
                },
                StaticSamplers = Array.Empty<RHISamplerDesc>(),
                AllowInputAssembler = false
            };
            _gpu!.PageAllocRootSig = _device.CreateRootSignature(rootDesc, "VSM_PageAllocRootSig");

            // 计算着色器：实际工程中编译 HLSL 页面分配着色器。此处用空字节码占位。
            var computeDesc = new RHIComputePipelineDesc
            {
                Name = "VSM_PageAllocationPSO",
                CS = new RHIShaderBytecode(Array.Empty<byte>(), "PageAllocationCS", RHIShaderStage.Compute)
            };
            try
            {
                _gpu.PageAllocationPSO = _device.CreateComputePipelineState(computeDesc, "VSM_PageAllocationPSO");
            }
            catch
            {
                // 着色器字节码为空时可能抛异常；忽略，CPU 端逻辑仍可独立工作。
                _gpu.PageAllocationPSO = null;
            }

            // 页面渲染图形根签名与 PSO（Pass 3 深度写入）。
            var renderRootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.Vertex,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0 }
                    }
                },
                AllowInputAssembler = true
            };
            _gpu.PageRenderRootSig = _device.CreateRootSignature(renderRootDesc, "VSM_PageRenderRootSig");

            var renderDesc = new RHIGraphicsPipelineDesc
            {
                Name = "VSM_PageRenderPSO",
                DSVFormat = RHIFormat.D32_Float,
                DepthStencilState = RHIDepthDesc.DepthWrite,
                RasterizerState = RHIRasterizerDesc.Shadow,
                BlendState = RHIBlendDesc.Default,
                RTVFormats = Array.Empty<RHIFormat>()
            };
            try
            {
                _gpu.PageRenderPSO = _device.CreateGraphicsPipelineState(renderDesc, "VSM_PageRenderPSO");
            }
            catch
            {
                _gpu.PageRenderPSO = null;
            }
        }

        /// <summary>重建所有页表列表（供分配器淘汰反向查找）。</summary>
        private void RebuildPageTableList()
        {
            _allPageTables.Clear();
            _allCaches.Clear();
            foreach (var d in _directionalVSMs)
                foreach (var c in d.Caches) { _allPageTables.Add(c.PageTable); _allCaches.Add(c); }
            foreach (var p in _pointVSMs)
                foreach (var c in p.Caches) { _allPageTables.Add(c.PageTable); _allCaches.Add(c); }
            foreach (var s in _spotVSMs) { _allPageTables.Add(s.Cache.PageTable); _allCaches.Add(s.Cache); }
        }

        // ──────────────────────────────────────────────────────────
        // 光源容器更新
        // ──────────────────────────────────────────────────────────

        /// <summary>根据场景光源更新 VSM 光源容器（增删）。</summary>
        private void UpdateLightContainers(IRenderScene scene, RenderCamera camera)
        {
            int dirIdx = 0, pointIdx = 0, spotIdx = 0;
            foreach (var light in scene.Lights)
            {
                if (light == null || !light.CastsShadows) continue;
                if (IsDirectional(light))
                {
                    if (dirIdx >= _directionalVSMs.Count)
                    {
                        _directionalVSMs.Add(new DirectionalVSM(dirIdx, _settings.MaxCascades));
                        // 新光源：标记全部脏。
                        _dirtyTracker.MarkAllDirty(dirIdx, dirIdx, 0);
                    }
                    dirIdx++;
                }
                else if (IsPoint(light))
                {
                    if (pointIdx >= _pointVSMs.Count)
                    {
                        _pointVSMs.Add(new PointVSM(pointIdx));
                        for (int f = 0; f < 6; f++)
                            _dirtyTracker.MarkAllDirty(f, pointIdx, f);
                    }
                    pointIdx++;
                }
                else if (IsSpot(light))
                {
                    if (spotIdx >= _spotVSMs.Count)
                    {
                        _spotVSMs.Add(new SpotVSM(spotIdx));
                        _dirtyTracker.MarkAllDirty(0, spotIdx, 0);
                    }
                    spotIdx++;
                }
            }
            RebuildPageTableList();
        }

        // ──────────────────────────────────────────────────────────
        // 帧结束与脏页触发
        // ──────────────────────────────────────────────────────────

        /// <summary>帧结束：各缓存结束帧处理（递增帧龄、清理请求集合）。</summary>
        public void EndFrame()
        {
            foreach (var c in _allCaches) c.EndFrame();
            _stats.ApplyTo(new FrameStats()); // 触发统计刷新（实际由调用方取 GetStats）
        }

        /// <summary>通知光源移动：标记受影响光源的所有页面为脏。</summary>
        public void OnLightMoved(int lightIndex, VSMLightType type)
        {
            switch (type)
            {
                case VSMLightType.Directional:
                    foreach (var d in _directionalVSMs)
                        if (d.LightIndex == lightIndex)
                            for (int clip = 0; clip < d.ClipCount; clip++)
                                _dirtyTracker.MarkAllDirty(clip, lightIndex, 0);
                    break;
                case VSMLightType.Point:
                    for (int f = 0; f < 6; f++)
                        _dirtyTracker.MarkAllDirty(f, lightIndex, f);
                    break;
                case VSMLightType.Spot:
                    _dirtyTracker.MarkAllDirty(0, lightIndex, 0);
                    break;
            }
        }

        /// <summary>通知物体移动：标记所有光源的页面为脏（保守策略，可优化为视锥裁剪）。</summary>
        public void OnObjectMoved()
        {
            foreach (var d in _directionalVSMs)
                for (int clip = 0; clip < d.ClipCount; clip++)
                    _dirtyTracker.MarkAllDirty(clip, d.LightIndex, 0);
            foreach (var p in _pointVSMs)
                for (int f = 0; f < 6; f++)
                    _dirtyTracker.MarkAllDirty(f, p.LightIndex, f);
            foreach (var s in _spotVSMs)
                _dirtyTracker.MarkAllDirty(0, s.LightIndex, 0);
        }

        // ──────────────────────────────────────────────────────────
        // 辅助方法
        // ──────────────────────────────────────────────────────────

        private static bool IsDirectional(RenderLight l) =>
            l.LightType.Equals("Directional", StringComparison.OrdinalIgnoreCase) ||
            l.LightType.Equals("Sun", StringComparison.OrdinalIgnoreCase);

        private static bool IsPoint(RenderLight l) =>
            l.LightType.Equals("Point", StringComparison.OrdinalIgnoreCase);

        private static bool IsSpot(RenderLight l) =>
            l.LightType.Equals("Spot", StringComparison.OrdinalIgnoreCase) ||
            l.LightType.Equals("Spotlight", StringComparison.OrdinalIgnoreCase);

        private void ResetFrameStats()
        {
            _stats.RequestedPages = 0;
            _stats.AllocatedPages = 0;
            _stats.EvictedPages = 0;
            _stats.RenderedPages = 0;
            _stats.CacheHits = 0;
        }

        /// <summary>手动切换到 CSM 回退（调试用）。</summary>
        public void ForceFallback(bool enable)
        {
            _useFallback = enable || !_stats.VSMAvailable;
        }

        /// <summary>获取 VSM GPU 资源（供外部渲染器绑定 SRV 采样）。</summary>
        public VSMGpuResources? GetGpuResources() => _gpu;

        /// <summary>获取方向光 VSM 列表（只读）。</summary>
        public IReadOnlyList<DirectionalVSM> DirectionalVSMs => _directionalVSMs;
        /// <summary>获取点光源 VSM 列表（只读）。</summary>
        public IReadOnlyList<PointVSM> PointVSMs => _pointVSMs;
        /// <summary>获取聚光灯 VSM 列表（只读）。</summary>
        public IReadOnlyList<SpotVSM> SpotVSMs => _spotVSMs;

        // ──────────────────────────────────────────────────────────
        // 释放
        // ──────────────────────────────────────────────────────────

        public void Dispose()
        {
            _gpu?.Dispose();
            _gpu = null;
            _fallbackCsm.Dispose();
            _pageAllocator.Clear();
            _dirtyTracker.Clear();
            foreach (var c in _allCaches) c.PageTable.Clear();
            _allPageTables.Clear();
            _allCaches.Clear();
            _directionalVSMs.Clear();
            _pointVSMs.Clear();
            _spotVSMs.Clear();
        }
    }
}
