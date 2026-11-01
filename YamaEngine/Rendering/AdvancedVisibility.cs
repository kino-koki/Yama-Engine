using System.Numerics;

namespace YamaEngine.Rendering
{
    public sealed class HzbOcclusionBuffer
    {
        private readonly int _width;
        private readonly int _height;
        private readonly List<float[,]> _mips = new();

        public int Width => _width;
        public int Height => _height;
        public int MipCount => _mips.Count;

        public HzbOcclusionBuffer(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "HZB size must be positive.");
            _width = width;
            _height = height;
            _mips.Add(new float[height, width]);
            Clear(1.0f);
        }

        public void Clear(float depth)
        {
            _mips.Clear();
            var baseMip = new float[_height, _width];
            for (var y = 0; y < _height; y++)
            {
                for (var x = 0; x < _width; x++)
                {
                    baseMip[y, x] = depth;
                }
            }
            _mips.Add(baseMip);
        }

        public void SetDepth(int x, int y, float depth)
        {
            if (x < 0 || y < 0 || x >= _width || y >= _height) return;
            _mips[0][y, x] = Math.Clamp(depth, 0f, 1f);
        }

        public void BuildMips()
        {
            while (_mips.Count > 1) _mips.RemoveAt(_mips.Count - 1);

            var current = _mips[0];
            var currentWidth = _width;
            var currentHeight = _height;
            while (currentWidth > 1 || currentHeight > 1)
            {
                var nextWidth = Math.Max(1, (currentWidth + 1) / 2);
                var nextHeight = Math.Max(1, (currentHeight + 1) / 2);
                var next = new float[nextHeight, nextWidth];
                for (var y = 0; y < nextHeight; y++)
                {
                    for (var x = 0; x < nextWidth; x++)
                    {
                        var minDepth = 1f;
                        for (var oy = 0; oy < 2; oy++)
                        {
                            for (var ox = 0; ox < 2; ox++)
                            {
                                var sx = Math.Min(currentWidth - 1, x * 2 + ox);
                                var sy = Math.Min(currentHeight - 1, y * 2 + oy);
                                minDepth = Math.Min(minDepth, current[sy, sx]);
                            }
                        }
                        next[y, x] = minDepth;
                    }
                }
                _mips.Add(next);
                current = next;
                currentWidth = nextWidth;
                currentHeight = nextHeight;
            }
        }

        public bool IsOccluded(Vector2 screenMin, Vector2 screenMax, float objectNearDepth)
        {
            if (_mips.Count == 1) BuildMips();

            var minX = ClampToPixelX(screenMin.X);
            var minY = ClampToPixelY(screenMin.Y);
            var maxX = ClampToPixelX(screenMax.X);
            var maxY = ClampToPixelY(screenMax.Y);
            if (maxX < minX) (minX, maxX) = (maxX, minX);
            if (maxY < minY) (minY, maxY) = (maxY, minY);

            var nearest = 1f;
            var baseMip = _mips[0];
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    nearest = Math.Min(nearest, baseMip[y, x]);
                }
            }
            return objectNearDepth > nearest + 0.0001f;
        }

        private int ClampToPixelX(float u) => Math.Clamp((int)MathF.Floor(u * _width), 0, _width - 1);
        private int ClampToPixelY(float v) => Math.Clamp((int)MathF.Floor(v * _height), 0, _height - 1);
    }

    public sealed class GpuDrawCandidate
    {
        public string Name { get; }
        public string MeshAssetPath { get; }
        public string MaterialAssetPath { get; }
        public Vector2 ScreenMin { get; set; }
        public Vector2 ScreenMax { get; set; }
        public float Depth { get; set; }
        public ulong SortKey { get; set; }
        public int InstanceCount { get; set; } = 1;

        public GpuDrawCandidate(string name, string meshAssetPath, string materialAssetPath)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "DrawCandidate" : name;
            MeshAssetPath = meshAssetPath ?? string.Empty;
            MaterialAssetPath = materialAssetPath ?? string.Empty;
        }
    }

    public sealed class GpuDrawCommand
    {
        public string Name { get; set; } = string.Empty;
        public string MeshAssetPath { get; set; } = string.Empty;
        public string MaterialAssetPath { get; set; } = string.Empty;
        public int InstanceCount { get; set; }
        public ulong SortKey { get; set; }
    }

    public sealed class GpuDrivenDrawList
    {
        public IReadOnlyList<GpuDrawCommand> Commands { get; init; } = Array.Empty<GpuDrawCommand>();
        public int InputCount { get; init; }
        public int OccludedCount { get; init; }
        public int VisibleCount => Commands.Count;
    }

    public static class GpuDrivenDrawListBuilder
    {
        public static GpuDrivenDrawList Build(IEnumerable<GpuDrawCandidate> candidates, HzbOcclusionBuffer? hzb = null)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));

            var inputCount = 0;
            var occludedCount = 0;
            var commands = new List<GpuDrawCommand>();
            foreach (var c in candidates)
            {
                inputCount++;
                if (hzb != null && hzb.IsOccluded(c.ScreenMin, c.ScreenMax, c.Depth))
                {
                    occludedCount++;
                    continue;
                }

                commands.Add(new GpuDrawCommand
                {
                    Name = c.Name,
                    MeshAssetPath = c.MeshAssetPath,
                    MaterialAssetPath = c.MaterialAssetPath,
                    InstanceCount = Math.Max(1, c.InstanceCount),
                    SortKey = c.SortKey
                });
            }

            commands.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));
            return new GpuDrivenDrawList
            {
                Commands = commands,
                InputCount = inputCount,
                OccludedCount = occludedCount
            };
        }
    }

    public static class AdvancedVisibilityStats
    {
        public static void WriteTo(FrameStats stats, GpuDrivenDrawList drawList, SparseShadowAtlasPlan shadowPlan)
        {
            if (stats == null) throw new ArgumentNullException(nameof(stats));
            if (drawList == null) throw new ArgumentNullException(nameof(drawList));
            if (shadowPlan == null) throw new ArgumentNullException(nameof(shadowPlan));

            stats.VisibilityCandidateCount = drawList.InputCount;
            stats.GpuVisibleDrawCount = drawList.Commands.Count;
            stats.HzbOccludedCount = drawList.OccludedCount;
            stats.SparseShadowAtlasAllocatedPages = shadowPlan.AllocatedPages.Count;
            stats.SparseShadowAtlasEvictedPages = shadowPlan.EvictedPageCount;
        }
    }

    public sealed record SparseShadowAtlasRequest(string LightName, int RequestedPages, float Priority);

    public sealed record VSMAllocatedPage(string LightName, int PageIndex, float Priority);

    public sealed class SparseShadowAtlasPlan
    {
        public IReadOnlyList<VSMAllocatedPage> AllocatedPages { get; init; } = Array.Empty<VSMAllocatedPage>();
        public int PhysicalPageBudget { get; init; }
        public int RequestedPageCount { get; init; }
        public int EvictedPageCount => Math.Max(0, RequestedPageCount - AllocatedPages.Count);
    }

    public sealed class SparseShadowAtlasScheduler
    {
        public int MaxPhysicalPages { get; }

        public SparseShadowAtlasScheduler(int maxPhysicalPages)
        {
            if (maxPhysicalPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPhysicalPages), "Virtual Shadow Map page budget must be positive.");
            MaxPhysicalPages = maxPhysicalPages;
        }

        public SparseShadowAtlasPlan Schedule(IEnumerable<SparseShadowAtlasRequest> requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));

            var ordered = requests
                .Where(r => r.RequestedPages > 0)
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.LightName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var requestedCount = ordered.Sum(r => r.RequestedPages);
            var pages = new List<VSMAllocatedPage>(MaxPhysicalPages);
            foreach (var request in ordered)
            {
                for (var i = 0; i < request.RequestedPages && pages.Count < MaxPhysicalPages; i++)
                {
                    pages.Add(new VSMAllocatedPage(request.LightName, pages.Count, request.Priority));
                }
                if (pages.Count >= MaxPhysicalPages) break;
            }

            return new SparseShadowAtlasPlan
            {
                AllocatedPages = pages,
                PhysicalPageBudget = MaxPhysicalPages,
                RequestedPageCount = requestedCount
            };
        }
    }
}
