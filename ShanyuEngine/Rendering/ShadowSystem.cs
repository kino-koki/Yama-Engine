using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using static ShanyuEngine.Rendering.MatrixMath;

namespace ShanyuEngine.Rendering
{
    // ──────────────────────────────────────────────────────────────
    // Shadow quality / filtering modes
    // ──────────────────────────────────────────────────────────────

    public enum ShadowFilterMode
    {
        Hard,
        PCF_3x3,
        PCF_5x5,
        PCF_7x7,
        PCF_Poisson_16,
        VSM,
        EVSM2,
        EVSM4
    }

    public enum ShadowAtlasPackMode
    {
        Shelf,
        Guillotine,
        MaxRects
    }

    public enum CascadeSplitScheme
    {
        Linear,
        Logarithmic,
        Practical,
        Manual
    }

    // ──────────────────────────────────────────────────────────────
    // Shadow bias configuration
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowBias
    {
        public float ConstantBias;
        public float SlopeScaledBias;
        public float DepthBiasClamp;
        public float NormalBias;

        public static ShadowBias Default => new()
        {
            ConstantBias = 0.0005f,
            SlopeScaledBias = 1.5f,
            DepthBiasClamp = 0.0f,
            NormalBias = 0.02f
        };

        public static ShadowBias Aggressive => new()
        {
            ConstantBias = 0.002f,
            SlopeScaledBias = 3.0f,
            DepthBiasClamp = 0.0001f,
            NormalBias = 0.05f
        };

        public static ShadowBias None => new()
        {
            ConstantBias = 0f, SlopeScaledBias = 0f, DepthBiasClamp = 0f, NormalBias = 0f
        };

        public float ComputeDepthBias(float slopeScale, int shadowMapResolution)
        {
            // Slope-scaled bias scales with the maximum depth slope of the triangle.
            // The constant bias is scaled by 1/resolution to remain resolution-independent.
            var invRes = 1.0f / Math.Max(1, shadowMapResolution);
            var slope = slopeScale * SlopeScaledBias;
            var constant = ConstantBias * invRes * 65535.0f;
            var total = constant + slope;
            return DepthBiasClamp > 0f ? Math.Min(total, DepthBiasClamp) : total;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Shadow system global settings
    // ──────────────────────────────────────────────────────────────

    public sealed class ShadowSettings
    {
        public bool Enabled { get; set; } = true;
        public int ShadowMapResolution { get; set; } = 2048;
        public int MaxCascades { get; set; } = 4;
        public float CascadeSplitLambda { get; set; } = 0.75f;
        public CascadeSplitScheme SplitScheme { get; set; } = CascadeSplitScheme.Practical;
        public float[] ManualSplitRatios { get; set; } = { 0.05f, 0.15f, 0.35f, 1.0f };
        public float CascadeBlendFraction { get; set; } = 0.15f;
        public bool EnableTexelSnapping { get; set; } = true;
        public ShadowFilterMode FilterMode { get; set; } = ShadowFilterMode.PCF_5x5;
        public ShadowAtlasPackMode AtlasPackMode { get; set; } = ShadowAtlasPackMode.Guillotine;
        public int AtlasSize { get; set; } = 8192;
        public bool PackIntoAtlas { get; set; } = true;
        public ShadowBias DirectionalBias { get; set; } = ShadowBias.Default;
        public ShadowBias PointBias { get; set; } = ShadowBias.Default;
        public ShadowBias SpotBias { get; set; } = ShadowBias.Default;
        public float MaxShadowDistance { get; set; } = 200f;
        public float PointShadowNearPlane { get; set; } = 0.1f;
        public float VSMLightBleedReduction { get; set; } = 0.15f;
        public float VSMDarkness { get; set; } = 0.02f;
        public float VSMMinVariance { get; set; } = 0.00001f;
        public bool Enable32BitDepth { get; set; } = true;
        public bool ContactShadows { get; set; } = true;
        public int ContactShadowSamples { get; set; } = 8;
        public float ContactShadowThickness { get; set; } = 0.5f;

        public RHIFormat ShadowDepthFormat => Enable32BitDepth ? RHIFormat.D32_Float : RHIFormat.D16_UNorm;
        public RHIFormat VsmFormat => RHIFormat.R32G32_Float;
        public RHIFormat Evsm4Format => RHIFormat.R16G16B16A16_Float;

        public int ClampResolution()
        {
            var res = ShadowMapResolution;
            if (res < 1024) res = 1024;
            if (res > 8192) res = 8192;
            // Round up to power of two for stable texel snapping.
            res = MatrixMath.NextPowerOfTwo(res);
            return res;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Cascade split computation
    // ──────────────────────────────────────────────────────────────


    // ──────────────────────────────────────────────────────────────
    // Shadow frustum computation with texel snapping
    // ──────────────────────────────────────────────────────────────

    public sealed class CascadeFrustum
    {
        public int Index { get; init; }
        public float NearDistance { get; init; }
        public float FarDistance { get; init; }
        public Vector3[] Corners { get; init; } = new Vector3[8];
        public Matrix4x4 ViewMatrix { get; set; }
        public Matrix4x4 ProjectionMatrix { get; set; }
        public Matrix4x4 ViewProjectionMatrix { get; set; }
        public Vector3 LightPosition { get; set; }
        public float TexelSize { get; set; }
        public Vector2 AtlasOffset { get; set; }
        public Vector2 AtlasScale { get; set; } = Vector2.One;

        public Matrix4x4 ShadowMatrix => ViewProjectionMatrix;
    }

    public static class ShadowFrustumComputer
    {
        /// <summary>
        /// Extracts the 8 corners of a sub-frustum defined by near/far distances
        /// from the camera view-projection matrix.
        /// </summary>
        public static Vector3[] ExtractFrustumCorners(Matrix4x4 viewProj, float nearDist, float farDist,
            float cameraNear, float cameraFar)
        {
            // Build a view-projection with the modified near/far by interpolating in NDC.
            // We reuse the original matrix but remap depth into [nearDist, farDist].
            var corners = new Vector3[8];
            var ndcNear = ClipFromViewDepth(nearDist, cameraNear, cameraFar);
            var ndcFar = ClipFromViewDepth(farDist, cameraNear, cameraFar);

            // 8 NDC corners: (x, y, z) in {-1, 1}
            var ndcCorners = new[]
            {
                new Vector3(-1, -1, ndcFar),
                new Vector3( 1, -1, ndcFar),
                new Vector3( 1,  1, ndcFar),
                new Vector3(-1,  1, ndcFar),
                new Vector3(-1, -1, ndcNear),
                new Vector3( 1, -1, ndcNear),
                new Vector3( 1,  1, ndcNear),
                new Vector3(-1,  1, ndcNear),
            };

            if (!Matrix4x4.Invert(viewProj, out var invViewProj))
            {
                // Fallback: identity corners
                for (var i = 0; i < 8; i++) corners[i] = Vector3.Zero;
                return corners;
            }

            for (var i = 0; i < 8; i++)
            {
                var c = ndcCorners[i];
                var homog = Vector4.Transform(new Vector4(c, 1f), invViewProj);
                corners[i] = homog.W != 0f
                    ? new Vector3(homog.X, homog.Y, homog.Z) / homog.W
                    : new Vector3(homog.X, homog.Y, homog.Z);
            }
            return corners;
        }

        private static float ClipFromViewDepth(float viewDepth, float near, float far)
        {
            if (viewDepth <= near) return 0f;
            if (viewDepth >= far) return 1f;
            // Standard perspective: z_ndc = (f+n - 2*f*n/z) / (f - n) for OpenGL-style [-1,1]
            // We map to [0,1] for D3D-style.
            var z = viewDepth;
            var a = far / (far - near);
            var b = far * near / (near - far);
            return a + b / z;
        }

        /// <summary>
        /// Computes a tight orthographic projection for a directional light covering
        /// the given frustum corners, with optional texel snapping for stable shadows.
        /// </summary>
        public static CascadeFrustum ComputeDirectionalCascade(
            int index, Vector3[] corners, Vector3 lightDirection,
            int shadowMapResolution, bool enableTexelSnapping, ShadowBias bias)
        {
            if (corners == null || corners.Length != 8)
                throw new ArgumentException("Cascade requires exactly 8 frustum corners.", nameof(corners));
            if (lightDirection == Vector3.Zero) lightDirection = -Vector3.UnitZ;
            lightDirection = Vector3.Normalize(lightDirection);

            // Build light view matrix looking along the light direction.
            var lightPos = corners[0];
            for (var i = 1; i < 8; i++) lightPos += corners[i];
            lightPos /= 8f;

            var up = MathF.Abs(Vector3.Dot(lightDirection, Vector3.UnitY)) > 0.99f
                ? Vector3.UnitZ : Vector3.UnitY;
            var lightRight = Vector3.Normalize(Vector3.Cross(up, lightDirection));
            var lightUp = Vector3.Cross(lightDirection, lightRight);

            var viewMatrix = Matrix4x4.CreateLookAt(lightPos, lightPos + lightDirection, lightUp);

            // Transform corners into light space and compute the AABB.
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (var i = 0; i < 8; i++)
            {
                var lc = Vector3.Transform(corners[i], viewMatrix);
                min = Vector3.Min(min, lc);
                max = Vector3.Max(max, lc);
            }

            // Texel snapping: quantize the min XY to the shadow map texel grid.
            var worldUnitsPerTexel = Math.Max(max.X - min.X, max.Y - min.Y) / shadowMapResolution;
            if (enableTexelSnapping && worldUnitsPerTexel > 0f)
            {
                min.X = MathF.Floor(min.X / worldUnitsPerTexel) * worldUnitsPerTexel;
                min.Y = MathF.Floor(min.Y / worldUnitsPerTexel) * worldUnitsPerTexel;
                max.X = min.X + MathF.Ceiling((max.X - min.X) / worldUnitsPerTexel) * worldUnitsPerTexel;
                max.Y = min.Y + MathF.Ceiling((max.Y - min.Y) / worldUnitsPerTexel) * worldUnitsPerTexel;
            }

            // Expand Z slightly to avoid clipping and incorporate normal bias.
            var zExtend = bias.NormalBias + 0.5f;
            min.Z -= zExtend;
            max.Z += zExtend;

            var projMatrix = Matrix4x4.CreateOrthographicOffCenter(
                min.X, max.X, min.Y, max.Y, -max.Z - 50f, -min.Z + 50f);

            var cascade = new CascadeFrustum
            {
                Index = index,
                Corners = (Vector3[])corners.Clone(),
                ViewMatrix = viewMatrix,
                ProjectionMatrix = projMatrix,
                ViewProjectionMatrix = viewMatrix * projMatrix,
                LightPosition = lightPos,
                TexelSize = worldUnitsPerTexel
            };
            return cascade;
        }

        /// <summary>
        /// Computes the 6 view-projection matrices for a point light cube shadow map.
        /// </summary>
        public static Matrix4x4[] ComputePointShadowMatrices(Vector3 lightPosition, float range, float nearPlane)
        {
            var matrices = new Matrix4x4[6];
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1f, nearPlane, range);

            // +X, -X, +Y, -Y, +Z, -Z (D3D / Vulkan convention ordering)
            matrices[0] = Matrix4x4.CreateLookAt(lightPosition, lightPosition + Vector3.UnitX, Vector3.UnitY) * proj;
            matrices[1] = Matrix4x4.CreateLookAt(lightPosition, lightPosition - Vector3.UnitX, Vector3.UnitY) * proj;
            matrices[2] = Matrix4x4.CreateLookAt(lightPosition, lightPosition + Vector3.UnitY, Vector3.UnitZ) * proj;
            matrices[3] = Matrix4x4.CreateLookAt(lightPosition, lightPosition - Vector3.UnitY, -Vector3.UnitZ) * proj;
            matrices[4] = Matrix4x4.CreateLookAt(lightPosition, lightPosition + Vector3.UnitZ, Vector3.UnitY) * proj;
            matrices[5] = Matrix4x4.CreateLookAt(lightPosition, lightPosition - Vector3.UnitZ, Vector3.UnitY) * proj;
            return matrices;
        }

        /// <summary>
        /// Computes the view-projection matrix for a spot light shadow map.
        /// </summary>
        public static Matrix4x4 ComputeSpotShadowMatrix(Vector3 lightPosition, Vector3 lightDirection,
            float outerConeAngle, float range, float nearPlane)
        {
            var dir = lightDirection == Vector3.Zero ? -Vector3.UnitZ : Vector3.Normalize(lightDirection);
            var up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var view = Matrix4x4.CreateLookAt(lightPosition, lightPosition + dir, up);
            // Use the full outer cone (plus a margin) as the FOV.
            var fov = Math.Clamp(outerConeAngle * 2.2f, MathF.PI * 0.05f, MathF.PI * 0.95f);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1f, nearPlane, range);
            return view * proj;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Shadow atlas rectangle packing (guillotine + shelf)
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowAtlasRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public bool IsValid => Width > 0 && Height > 0;
        public Vector2 Offset => new(X, Y);
        public Vector2 Scale => new(Width, Height);

        public bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
    }

    public sealed class ShadowAtlasPacker
    {
        private readonly int _atlasSize;
        private readonly List<ShadowAtlasNode> _freeNodes = new();
        private readonly Dictionary<int, ShadowAtlasRect> _allocations = new();
        private readonly ShadowAtlasPackMode _mode;
        private int _nextId = 1;

        public int AtlasSize => _atlasSize;
        public ShadowAtlasPackMode Mode => _mode;
        public int AllocatedCount => _allocations.Count;

        public ShadowAtlasPacker(int atlasSize, ShadowAtlasPackMode mode = ShadowAtlasPackMode.Guillotine)
        {
            if (atlasSize <= 0) throw new ArgumentOutOfRangeException(nameof(atlasSize));
            _atlasSize = MatrixMath.NextPowerOfTwo(atlasSize);
            _mode = mode;
            _freeNodes.Add(new ShadowAtlasNode { X = 0, Y = 0, Width = _atlasSize, Height = _atlasSize });
        }

        public int Allocate(int width, int height)
        {
            if (width <= 0 || height <= 0) return -1;
            var rect = _mode switch
            {
                ShadowAtlasPackMode.Shelf => AllocateShelf(width, height),
                ShadowAtlasPackMode.MaxRects => AllocateMaxRects(width, height),
                _ => AllocateGuillotine(width, height)
            };
            if (!rect.IsValid) return -1;
            var id = _nextId++;
            _allocations[id] = rect;
            return id;
        }

        public ShadowAtlasRect GetRect(int id) =>
            _allocations.TryGetValue(id, out var r) ? r : new ShadowAtlasRect();

        public void Free(int id) => _allocations.Remove(id);

        public void Clear()
        {
            _freeNodes.Clear();
            _freeNodes.Add(new ShadowAtlasNode { X = 0, Y = 0, Width = _atlasSize, Height = _atlasSize });
            _allocations.Clear();
            _nextId = 1;
        }

        public float ComputeUtilization()
        {
            var used = 0;
            foreach (var a in _allocations.Values)
                used += a.Width * a.Height;
            return (float)used / (_atlasSize * _atlasSize);
        }

        private ShadowAtlasRect AllocateGuillotine(int w, int h)
        {
            // Best-fit decreasing selection of free node.
            var bestIndex = -1;
            var bestArea = int.MaxValue;
            for (var i = 0; i < _freeNodes.Count; i++)
            {
                var node = _freeNodes[i];
                if (node.Width >= w && node.Height >= h)
                {
                    var area = node.Width * node.Height;
                    if (area < bestArea)
                    {
                        bestArea = area;
                        bestIndex = i;
                    }
                }
            }
            if (bestIndex < 0) return new ShadowAtlasRect();

            var best = _freeNodes[bestIndex];
            _freeNodes.RemoveAt(bestIndex);

            var rect = new ShadowAtlasRect { X = best.X, Y = best.Y, Width = w, Height = h };

            // Split the remaining area: Guillotine split along the longer axis.
            var remainingW = best.Width - w;
            var remainingH = best.Height - h;
            if (remainingW > 0)
                _freeNodes.Add(new ShadowAtlasNode { X = best.X + w, Y = best.Y, Width = remainingW, Height = h });
            if (remainingH > 0)
                _freeNodes.Add(new ShadowAtlasNode { X = best.X, Y = best.Y + h, Width = best.Width, Height = remainingH });

            // Merge adjacent free nodes to reduce fragmentation.
            MergeFreeNodes();
            return rect;
        }

        private ShadowAtlasRect AllocateShelf(int w, int h)
        {
            // Simple shelf packing: pack rows left-to-right, top-to-bottom.
            var y = 0;
            var rowHeight = 0;
            var x = 0;
            // Find the best existing row.
            for (var i = 0; i < _freeNodes.Count; i++)
            {
                var node = _freeNodes[i];
                if (node.Width >= w && node.Height >= h && node.Width - w < _atlasSize)
                {
                    var rect = new ShadowAtlasRect { X = node.X, Y = node.Y, Width = w, Height = h };
                    node.X += w;
                    node.Width -= w;
                    _freeNodes[i] = node;
                    if (node.Width <= 0) _freeNodes.RemoveAt(i);
                    return rect;
                }
            }
            // Start a new row at the bottom.
            var totalUsedH = 0;
            foreach (var a in _allocations.Values)
                totalUsedH = Math.Max(totalUsedH, a.Y + a.Height);
            y = totalUsedH;
            if (y + h > _atlasSize) return new ShadowAtlasRect();
            rowHeight = h;
            x = 0;
            var newRect = new ShadowAtlasRect { X = x, Y = y, Width = w, Height = h };
            if (w < _atlasSize)
                _freeNodes.Add(new ShadowAtlasNode { X = x + w, Y = y, Width = _atlasSize - w, Height = rowHeight });
            return newRect;
        }

        private ShadowAtlasRect AllocateMaxRects(int w, int h)
        {
            // BSSF (Best Short Side Fit) heuristic.
            var bestIndex = -1;
            var bestShortSide = int.MaxValue;
            var bestLongSide = int.MaxValue;
            for (var i = 0; i < _freeNodes.Count; i++)
            {
                var node = _freeNodes[i];
                if (node.Width >= w && node.Height >= h)
                {
                    var leftoverW = node.Width - w;
                    var leftoverH = node.Height - h;
                    var shortSide = Math.Min(leftoverW, leftoverH);
                    var longSide = Math.Max(leftoverW, leftoverH);
                    if (shortSide < bestShortSide || (shortSide == bestShortSide && longSide < bestLongSide))
                    {
                        bestShortSide = shortSide;
                        bestLongSide = longSide;
                        bestIndex = i;
                    }
                }
            }
            if (bestIndex < 0) return new ShadowAtlasRect();

            var best = _freeNodes[bestIndex];
            _freeNodes.RemoveAt(bestIndex);
            var rect = new ShadowAtlasRect { X = best.X, Y = best.Y, Width = w, Height = h };

            // Split into up to 4 remaining rectangles around the placed rect.
            var splits = new List<ShadowAtlasNode>(4);
            if (rect.X + w < best.X + best.Width)
                splits.Add(new ShadowAtlasNode { X = rect.X + w, Y = best.Y, Width = best.X + best.Width - (rect.X + w), Height = best.Height });
            if (rect.Y + h < best.Y + best.Height)
                splits.Add(new ShadowAtlasNode { X = best.X, Y = rect.Y + h, Width = best.Width, Height = best.Y + best.Height - (rect.Y + h) });

            foreach (var s in splits)
                if (s.Width > 0 && s.Height > 0)
                    _freeNodes.Add(s);

            // Prune contained nodes.
            PruneContainedNodes();
            return rect;
        }

        private void MergeFreeNodes()
        {
            _freeNodes.Sort((a, b) =>
            {
                var c = a.Y.CompareTo(b.Y);
                return c != 0 ? c : a.X.CompareTo(b.X);
            });
            for (var i = 0; i < _freeNodes.Count - 1; i++)
            {
                var a = _freeNodes[i];
                var b = _freeNodes[i + 1];
                if (a.Y == b.Y && a.X + a.Width == b.X && a.Height == b.Height)
                {
                    _freeNodes[i] = new ShadowAtlasNode { X = a.X, Y = a.Y, Width = a.Width + b.Width, Height = a.Height };
                    _freeNodes.RemoveAt(i + 1);
                    i--;
                }
            }
        }

        private void PruneContainedNodes()
        {
            for (var i = 0; i < _freeNodes.Count; i++)
            {
                for (var j = _freeNodes.Count - 1; j > i; j--)
                {
                    if (Contains(_freeNodes[i], _freeNodes[j]))
                        _freeNodes.RemoveAt(j);
                    else if (Contains(_freeNodes[j], _freeNodes[i]))
                    {
                        _freeNodes[i] = _freeNodes[j];
                        _freeNodes.RemoveAt(j);
                    }
                }
            }
        }

        private static bool Contains(ShadowAtlasNode outer, ShadowAtlasNode inner) =>
            inner.X >= outer.X && inner.Y >= outer.Y &&
            inner.X + inner.Width <= outer.X + outer.Width &&
            inner.Y + inner.Height <= outer.Y + outer.Height;

        private struct ShadowAtlasNode
        {
            public int X, Y, Width, Height;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // PCF / Poisson disk kernel generation
    // ──────────────────────────────────────────────────────────────

    public static class ShadowKernelGenerator
    {
        public static Vector2[] GeneratePoissonDisk(int sampleCount, float radius = 1f, int seed = 0)
        {
            sampleCount = Math.Clamp(sampleCount, 1, 64);
            var samples = new Vector2[sampleCount];
            if (sampleCount == 1)
            {
                samples[0] = Vector2.Zero;
                return samples;
            }

            // Bridson Poisson disk sampling in a unit disk.
            var rng = seed == 0 ? new Random() : new Random(seed);
            var cellSize = radius / MathF.Sqrt(2f);
            var gridW = (int)MathF.Ceiling(2f / cellSize) + 2;
            var gridH = gridW;
            var grid = new int[gridW * gridH];
            for (var i = 0; i < grid.Length; i++) grid[i] = -1;
            var points = new List<Vector2>();
            var active = new List<int>();

            var first = new Vector2(rng.NextSingle() * 2f - 1f, rng.NextSingle() * 2f - 1f);
            points.Add(first);
            active.Add(0);
            grid[GridIndex(first, cellSize, gridW, gridH)] = 0;

            var maxAttempts = 30;
            while (active.Count > 0 && points.Count < sampleCount * 4)
            {
                var idx = active[rng.Next(active.Count)];
                var center = points[idx];
                var found = false;
                for (var a = 0; a < maxAttempts; a++)
                {
                    var angle = rng.NextSingle() * MathF.PI * 2f;
                    var r = radius * (rng.NextSingle() * 0.5f + 0.5f) + radius;
                    var candidate = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
                    if (candidate.X < -1 || candidate.X > 1 || candidate.Y < -1 || candidate.Y > 1) continue;
                    if (!IsFarEnough(candidate, points, grid, cellSize, gridW, gridH, radius))
                        continue;
                    points.Add(candidate);
                    var newIdx = points.Count - 1;
                    active.Add(newIdx);
                    grid[GridIndex(candidate, cellSize, gridW, gridH)] = newIdx;
                    found = true;
                    break;
                }
                if (!found)
                {
                    var removeIdx = active.IndexOf(idx);
                    if (removeIdx >= 0) active.RemoveAt(removeIdx);
                }
            }

            // Pick the requested count closest to center for a compact kernel.
            points.Sort((a, b) => a.LengthSquared().CompareTo(b.LengthSquared()));
            var take = Math.Min(sampleCount, points.Count);
            for (var i = 0; i < take; i++) samples[i] = points[i] * radius;
            for (var i = take; i < sampleCount; i++) samples[i] = Vector2.Zero;
            return samples;
        }

        public static Vector2[] GenerateGridKernel(int extent)
        {
            var size = extent * 2 + 1;
            var samples = new Vector2[size * size];
            var idx = 0;
            for (var y = -extent; y <= extent; y++)
                for (var x = -extent; x <= extent; x++)
                    samples[idx++] = new Vector2(x, y);
            return samples;
        }

        public static Vector2[] GetKernel(ShadowFilterMode mode)
        {
            return mode switch
            {
                ShadowFilterMode.PCF_3x3 => GenerateGridKernel(1),
                ShadowFilterMode.PCF_5x5 => GenerateGridKernel(2),
                ShadowFilterMode.PCF_7x7 => GenerateGridKernel(3),
                ShadowFilterMode.PCF_Poisson_16 => GeneratePoissonDisk(16, 1.5f, 1337),
                _ => new[] { Vector2.Zero }
            };
        }

        private static int GridIndex(Vector2 p, float cellSize, int gridW, int gridH)
        {
            var gx = Math.Clamp((int)MathF.Floor((p.X + 1f) / cellSize), 0, gridW - 1);
            var gy = Math.Clamp((int)MathF.Floor((p.Y + 1f) / cellSize), 0, gridH - 1);
            return gy * gridW + gx;
        }

        private static bool IsFarEnough(Vector2 candidate, List<Vector2> points, int[] grid,
            float cellSize, int gridW, int gridH, float minDist)
        {
            var gx = (int)MathF.Floor((candidate.X + 1f) / cellSize);
            var gy = (int)MathF.Floor((candidate.Y + 1f) / cellSize);
            for (var oy = -2; oy <= 2; oy++)
            {
                for (var ox = -2; ox <= 2; ox++)
                {
                    var nx = gx + ox;
                    var ny = gy + oy;
                    if (nx < 0 || ny < 0 || nx >= gridW || ny >= gridH) continue;
                    var pi = grid[ny * gridW + nx];
                    if (pi < 0) continue;
                    if (Vector2.Distance(candidate, points[pi]) < minDist)
                        return false;
                }
            }
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // VSM (Variance Shadow Map) moment computation
    // ──────────────────────────────────────────────────────────────

    public static class VarianceShadowMap
    {
        /// <summary>
        /// Chebyshev's upper-bound inequality for VSM visibility.
        /// moments.X = mean depth, moments.Y = squared mean depth.
        /// </summary>
        public static float ChebyshevUpperBound(Vector2 moments, float t, float minVariance, float lightBleedReduction)
        {
            // E[X] = moments.X, E[X^2] = moments.Y
            var variance = moments.Y - (moments.X * moments.X);
            variance = Math.Max(variance, minVariance);
            var d = t - moments.X;
            if (d <= 0f) return 1f;

            var pMax = variance / (variance + d * d);
            // Light bleed reduction: remap the tail.
            pMax = ReduceLightBleed(pMax, lightBleedReduction);
            return Math.Clamp(pMax, 0f, 1f);
        }

        public static float ReduceLightBleed(float pMax, float amount)
        {
            // Push the [amount, 1] range down to [0, 1] to suppress bleeding.
            return Math.Clamp((pMax - amount) / (1f - amount), 0f, 1f);
        }

        /// <summary>
        /// Exponential warp for EVSM. warps depth into positive exponential space.
        /// </summary>
        public static Vector2 ComputeEvsm2Moments(float depth, float positiveExponent)
        {
            var c = MathF.Exp(positiveExponent * depth);
            return new Vector2(c, c * c);
        }

        public static Vector4 ComputeEvsm4Moments(float depth, float positiveExponent, float negativeExponent)
        {
            var pos = MathF.Exp(positiveExponent * depth);
            var neg = MathF.Exp(-negativeExponent * depth);
            return new Vector4(pos, pos * pos, neg, neg * neg);
        }

        public static float EvsmVisibility(Vector4 moments, float depth, float positiveExponent, float negativeExponent)
        {
            var pos = MathF.Exp(positiveExponent * depth);
            var posSq = pos * pos;
            var neg = MathF.Exp(-negativeExponent * depth);
            var negSq = neg * neg;

            var visPos = ChebyshevUpperBound(new Vector2(moments.X, moments.Y), pos, 0.00001f, 0f);
            var visNeg = ChebyshevUpperBound(new Vector2(moments.Z, moments.W), neg, 0.00001f, 0f);
            return Math.Min(visPos, visNeg);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Shadow map allocation handles
    // ──────────────────────────────────────────────────────────────

    public enum ShadowMapType { DirectionalCascade, PointCube, Spot }

    public sealed class ShadowMapHandle
    {
        public int Id { get; init; }
        public ShadowMapType Type { get; init; }
        public Guid LightId { get; init; }
        public string LightName { get; init; } = string.Empty;
        public ShadowAtlasRect AtlasRect { get; init; }
        public int Resolution { get; init; }
        public int ArraySlice { get; init; }
        public Matrix4x4[] ViewProjections { get; init; } = Array.Empty<Matrix4x4>();
        public Matrix4x4 ShadowMatrix { get; init; } = Matrix4x4.Identity;
        public ShadowBias Bias { get; init; } = ShadowBias.Default;
        public float BlendFraction { get; init; }
        public bool IsValid => Id > 0 && Resolution > 0;

        public Vector2 AtlasOffset => AtlasRect.Offset;
        public Vector2 AtlasScale => new(
            (float)AtlasRect.Width / AtlasRect.Width,
            (float)AtlasRect.Height / AtlasRect.Height);
    }

    public sealed class DirectionalShadowHandle
    {
        public Guid LightId { get; init; }
        public string LightName { get; init; } = string.Empty;
        public CascadeFrustum[] Cascades { get; init; } = Array.Empty<CascadeFrustum>();
        public float[] SplitDistances { get; init; } = Array.Empty<float>();
        public ShadowAtlasRect[] AtlasRects { get; init; } = Array.Empty<ShadowAtlasRect>();
        public ShadowBias Bias { get; init; }
        public float BlendFraction { get; init; }
        public int Resolution { get; init; }
    }

    // ──────────────────────────────────────────────────────────────
    // Per-light shadow renderers
    // ──────────────────────────────────────────────────────────────

    public sealed class DirectionalShadowRenderer
    {
        private readonly ShadowSettings _settings;

        public DirectionalShadowRenderer(ShadowSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public DirectionalShadowHandle BuildCascades(
            RenderLight light, RenderCamera camera, ShadowAtlasPacker packer)
        {
            if (light == null) throw new ArgumentNullException(nameof(light));
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (packer == null) throw new ArgumentNullException(nameof(packer));

            var cascadeCount = Math.Clamp(_settings.MaxCascades, 1, 4);
            var res = _settings.ClampResolution();

            var cameraNear = camera.NearClip;
            var cameraFar = Math.Min(camera.FarClip, _settings.MaxShadowDistance);

            var splitDistances = CascadeSplitCalculator.Compute(
                cascadeCount, cameraNear, cameraFar,
                _settings.SplitScheme, _settings.CascadeSplitLambda, _settings.ManualSplitRatios);

            var lightDir = ExtractForward(light.WorldMatrix);
            if (lightDir == Vector3.Zero) lightDir = -Vector3.UnitZ;
            lightDir = Vector3.Normalize(lightDir);

            // Build a camera view-projection for frustum corner extraction.
            var camPos = ExtractTranslation(camera.WorldMatrix);
            var camForward = ExtractForward(camera.WorldMatrix);
            var camUp = ExtractUp(camera.WorldMatrix);
            var viewProj = Matrix4x4.CreateLookAt(camPos, camPos + camForward, camUp) *
                           Matrix4x4.CreatePerspectiveFieldOfView(
                               camera.FieldOfView * MathF.PI / 180f, camera.AspectRatio, cameraNear, cameraFar);

            var cascades = new CascadeFrustum[cascadeCount];
            var atlasRects = new ShadowAtlasRect[cascadeCount];

            var prevNear = cameraNear;
            for (var i = 0; i < cascadeCount; i++)
            {
                var far = splitDistances[i];
                var corners = ShadowFrustumComputer.ExtractFrustumCorners(viewProj, prevNear, far, cameraNear, cameraFar);
                var cascade = ShadowFrustumComputer.ComputeDirectionalCascade(
                    i, corners, lightDir, res, _settings.EnableTexelSnapping, _settings.DirectionalBias);

                // Allocate atlas rect for this cascade.
                var id = packer.Allocate(res, res);
                var rect = packer.GetRect(id);
                if (!rect.IsValid)
                {
                    // Fall back to a non-atlas full slice.
                    rect = new ShadowAtlasRect { X = 0, Y = 0, Width = res, Height = res };
                }
                cascade.AtlasOffset = new Vector2(rect.X, rect.Y);
                var atlasScale = new Vector2((float)rect.Width / packer.AtlasSize, (float)rect.Height / packer.AtlasSize);
                var atlasOffset = new Vector2((float)rect.X / packer.AtlasSize, (float)rect.Y / packer.AtlasSize);
                cascade.AtlasScale = atlasScale;
                // Rebuild the shadow matrix to incorporate atlas offset/scale for sampling.
                var scaleBias = new Matrix4x4(
                    atlasScale.X, 0, 0, 0,
                    0, atlasScale.Y, 0, 0,
                    0, 0, 1, 0,
                    atlasOffset.X, atlasOffset.Y, 0, 1);
                cascade.ViewProjectionMatrix = cascade.ViewMatrix * cascade.ProjectionMatrix * scaleBias;

                cascades[i] = cascade;
                atlasRects[i] = rect;
                prevNear = far;
            }

            return new DirectionalShadowHandle
            {
                LightId = light.ObjectId,
                LightName = light.Name,
                Cascades = cascades,
                SplitDistances = splitDistances,
                AtlasRects = atlasRects,
                Bias = _settings.DirectionalBias,
                BlendFraction = _settings.CascadeBlendFraction,
                Resolution = res
            };
        }

        /// <summary>
        /// Selects the appropriate cascade index for a given view-space depth, with
        /// a blend region straddling cascade boundaries.
        /// </summary>
        public static (int cascade, float blendWeight) SelectCascade(
            float viewDepth, float[] splitDistances, float blendFraction)
        {
            if (splitDistances == null || splitDistances.Length == 0)
                return (0, 0f);

            for (var i = 0; i < splitDistances.Length; i++)
            {
                var split = splitDistances[i];
                var blendStart = i > 0 ? split - (split - splitDistances[i - 1]) * blendFraction : split;
                if (viewDepth < blendStart)
                    return (i, 0f);
                if (viewDepth < split)
                {
                    var t = (viewDepth - blendStart) / Math.Max(0.0001f, split - blendStart);
                    return (i, Math.Clamp(t, 0f, 1f));
                }
            }
            return (splitDistances.Length - 1, 0f);
        }
    }

    public sealed class PointShadowRenderer
    {
        private readonly ShadowSettings _settings;

        public PointShadowRenderer(ShadowSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public ShadowMapHandle Build(RenderLight light, ShadowAtlasPacker packer)
        {
            if (light == null) throw new ArgumentNullException(nameof(light));
            if (packer == null) throw new ArgumentNullException(nameof(packer));

            var res = _settings.ClampResolution();
            // Point light shadows need 6 faces. We allocate a 2x3 tile layout in the atlas.
            var tileW = res;
            var tileH = res;
            var atlasId = packer.Allocate(tileW * 3, tileH * 2);
            var atlasRect = packer.GetRect(atlasId);
            if (!atlasRect.IsValid)
            {
                atlasRect = new ShadowAtlasRect { X = 0, Y = 0, Width = tileW * 3, Height = tileH * 2 };
            }

            var lightPos = ExtractTranslation(light.WorldMatrix);
            var range = Math.Max(0.1f, light.Range);
            var near = _settings.PointShadowNearPlane;
            var vps = ShadowFrustumComputer.ComputePointShadowMatrices(lightPos, range, near);

            return new ShadowMapHandle
            {
                Id = atlasId,
                Type = ShadowMapType.PointCube,
                LightId = light.ObjectId,
                LightName = light.Name,
                AtlasRect = atlasRect,
                Resolution = res,
                ArraySlice = 0,
                ViewProjections = vps,
                ShadowMatrix = Matrix4x4.Identity,
                Bias = _settings.PointBias
            };
        }

        /// <summary>
        /// Computes per-face atlas offsets for a packed point light shadow (2x3 grid).
        /// </summary>
        public static Vector2[] GetFaceOffsets(ShadowAtlasRect rect, int resolution)
        {
            var offsets = new Vector2[6];
            var col0 = rect.X;
            var col1 = rect.X + resolution;
            var col2 = rect.X + resolution * 2;
            var row0 = rect.Y;
            var row1 = rect.Y + resolution;
            offsets[0] = new Vector2(col0, row0);
            offsets[1] = new Vector2(col1, row0);
            offsets[2] = new Vector2(col2, row0);
            offsets[3] = new Vector2(col0, row1);
            offsets[4] = new Vector2(col1, row1);
            offsets[5] = new Vector2(col2, row1);
            return offsets;
        }
    }

    public sealed class SpotShadowRenderer
    {
        private readonly ShadowSettings _settings;

        public SpotShadowRenderer(ShadowSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public ShadowMapHandle Build(RenderLight light, ShadowAtlasPacker packer)
        {
            if (light == null) throw new ArgumentNullException(nameof(light));
            if (packer == null) throw new ArgumentNullException(nameof(packer));

            var res = _settings.ClampResolution();
            var atlasId = packer.Allocate(res, res);
            var atlasRect = packer.GetRect(atlasId);
            if (!atlasRect.IsValid)
                atlasRect = new ShadowAtlasRect { X = 0, Y = 0, Width = res, Height = res };

            var lightPos = ExtractTranslation(light.WorldMatrix);
            var lightDir = ExtractForward(light.WorldMatrix);
            var range = Math.Max(0.1f, light.Range);
            // Outer cone angle approximated from range if not available.
            var outerAngle = MathF.PI * 0.25f;
            var vp = ShadowFrustumComputer.ComputeSpotShadowMatrix(lightPos, lightDir, outerAngle, range, _settings.PointShadowNearPlane);

            var atlasScale = new Vector2((float)atlasRect.Width / packer.AtlasSize, (float)atlasRect.Height / packer.AtlasSize);
            var atlasOffset = new Vector2((float)atlasRect.X / packer.AtlasSize, (float)atlasRect.Y / packer.AtlasSize);
            var scaleBias = new Matrix4x4(
                atlasScale.X, 0, 0, 0,
                0, atlasScale.Y, 0, 0,
                0, 0, 1, 0,
                atlasOffset.X, atlasOffset.Y, 0, 1);
            var shadowMatrix = vp * scaleBias;

            return new ShadowMapHandle
            {
                Id = atlasId,
                Type = ShadowMapType.Spot,
                LightId = light.ObjectId,
                LightName = light.Name,
                AtlasRect = atlasRect,
                Resolution = res,
                ArraySlice = 0,
                ViewProjections = new[] { vp },
                ShadowMatrix = shadowMatrix,
                Bias = _settings.SpotBias
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Shadow constant buffer data (matches HLSL layout)
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowCascadesCB
    {
        public Matrix4x4 ShadowMatrices;
        public Vector4 CascadeSplits;       // x,y,z,w = split distances
        public Vector4 CascadeOffsets0;     // atlas offset/scale for cascade 0
        public Vector4 CascadeOffsets1;
        public Vector4 CascadeOffsets2;
        public Vector4 CascadeOffsets3;
        public float ConstantBias;
        public float SlopeScaledBias;
        public float NormalBias;
        public float BlendFraction;
        public int FilterMode;
        public int KernelSize;
        public float ShadowTexelSize;
        public float Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowPerLightCB
    {
        public Matrix4x4 ShadowMatrix;
        public Vector3 LightPosition;
        public float LightRange;
        public Vector3 LightDirection;
        public float Bias;
        public int ShadowType;   // 0 = spot, 1 = point
        public int FilterMode;
        public float TexelSize;
        public float Pad;
    }

    // ──────────────────────────────────────────────────────────────
    // Shadow GPU resource container
    // ──────────────────────────────────────────────────────────────

    public sealed class ShadowAtlasResources
    {
        public RHITexture? DepthAtlas { get; set; }
        public RHITexture? VsmAtlas { get; set; }
        public RHIDescriptorHandle DepthSrv { get; set; }
        public RHIDescriptorHandle VsmSrv { get; set; }
        public RHIDescriptorHandle DepthDsv { get; set; }
        public RHIDescriptorHandle Sampler { get; set; }
        public RHIBuffer? CascadesCB { get; set; }
        public RHIBuffer? PerLightCB { get; set; }
        public int AtlasSize { get; set; }

        public void Dispose()
        {
            DepthAtlas?.Dispose();
            VsmAtlas?.Dispose();
            CascadesCB?.Dispose();
            PerLightCB?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Main shadow system coordinator
    // ──────────────────────────────────────────────────────────────

    public sealed class ShadowSystem : IDisposable
    {
        private readonly ShadowSettings _settings;
        private readonly ShadowAtlasPacker _packer;
        private readonly DirectionalShadowRenderer _directionalRenderer;
        private readonly PointShadowRenderer _pointRenderer;
        private readonly SpotShadowRenderer _spotRenderer;
        private ShadowAtlasResources? _resources;
        private readonly List<DirectionalShadowHandle> _directionalShadows = new();
        private readonly List<ShadowMapHandle> _pointShadows = new();
        private readonly List<ShadowMapHandle> _spotShadows = new();

        public ShadowSettings Settings => _settings;
        public ShadowAtlasPacker AtlasPacker => _packer;
        public ShadowAtlasResources? Resources => _resources;
        public IReadOnlyList<DirectionalShadowHandle> DirectionalShadows => _directionalShadows;
        public IReadOnlyList<ShadowMapHandle> PointShadows => _pointShadows;
        public IReadOnlyList<ShadowMapHandle> SpotShadows => _spotShadows;
        public bool HasShadows => _directionalShadows.Count + _pointShadows.Count + _spotShadows.Count > 0;

        public ShadowSystem(ShadowSettings? settings = null)
        {
            _settings = settings ?? new ShadowSettings();
            _packer = new ShadowAtlasPacker(_settings.AtlasSize, _settings.AtlasPackMode);
            _directionalRenderer = new DirectionalShadowRenderer(_settings);
            _pointRenderer = new PointShadowRenderer(_settings);
            _spotRenderer = new SpotShadowRenderer(_settings);
        }

        /// <summary>
        /// Ensures GPU atlas resources exist and match the current settings.
        /// </summary>
        public ShadowAtlasResources EnsureResources(RHIDevice device)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var atlasSize = _packer.AtlasSize;
            var needsCreate = _resources == null || _resources.AtlasSize != atlasSize;
            if (needsCreate)
            {
                _resources?.Dispose();
                var depthDesc = new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D,
                    Format = _settings.ShadowDepthFormat,
                    Width = atlasSize,
                    Height = atlasSize,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    SampleCount = 1,
                    Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource,
                    OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, _settings.ShadowDepthFormat)
                };
                var depthAtlas = device.CreateTexture(depthDesc, "ShadowDepthAtlas");
                var depthSrv = device.CreateSRV(depthAtlas);
                var depthDsv = device.CreateDSV(depthAtlas);

                RHITexture? vsmAtlas = null;
                RHIDescriptorHandle vsmSrv = default;
                if (_settings.FilterMode == ShadowFilterMode.VSM ||
                    _settings.FilterMode == ShadowFilterMode.EVSM2 ||
                    _settings.FilterMode == ShadowFilterMode.EVSM4)
                {
                    var vsmFmt = _settings.FilterMode == ShadowFilterMode.EVSM4
                        ? _settings.Evsm4Format : _settings.VsmFormat;
                    var vsmDesc = new RHITextureDesc
                    {
                        Dimension = RHIResourceDimension.Texture2D,
                        Format = vsmFmt,
                        Width = atlasSize,
                        Height = atlasSize,
                        DepthOrArraySize = 1,
                        MipLevels = 1,
                        SampleCount = 1,
                        Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
                    };
                    vsmAtlas = device.CreateTexture(vsmDesc, "ShadowVsmAtlas");
                    vsmSrv = device.CreateSRV(vsmAtlas);
                }

                var cascadesCB = device.CreateBuffer(new RHIBufferDesc
                {
                    SizeInBytes = (ulong)MatrixMath.AlignUp(Marshal.SizeOf<ShadowCascadesCB>(), 256),
                    Flags = RHIBufferFlags.ConstantBuffer,
                    HeapType = RHIResourceHeapType.Default
                }, "ShadowCascadesCB");
                var perLightCB = device.CreateBuffer(new RHIBufferDesc
                {
                    SizeInBytes = (ulong)MatrixMath.AlignUp(Marshal.SizeOf<ShadowPerLightCB>(), 256),
                    Flags = RHIBufferFlags.ConstantBuffer,
                    HeapType = RHIResourceHeapType.Default
                }, "ShadowPerLightCB");

                var sampler = device.GetOrCreateSampler(RHISamplerDesc.ShadowSampler);

                _resources = new ShadowAtlasResources
                {
                    DepthAtlas = depthAtlas,
                    VsmAtlas = vsmAtlas,
                    DepthSrv = depthSrv,
                    VsmSrv = vsmSrv,
                    DepthDsv = depthDsv,
                    Sampler = sampler,
                    CascadesCB = cascadesCB,
                    PerLightCB = perLightCB,
                    AtlasSize = atlasSize
                };
            }
            return _resources!;
        }

        /// <summary>
        /// Builds all shadow maps for the current frame's visible lights.
        /// Clears previous frame allocations and repacks the atlas.
        /// </summary>
        public void BuildFrameShadows(IRenderScene scene, RenderCamera camera)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (!_settings.Enabled) return;

            _directionalShadows.Clear();
            _pointShadows.Clear();
            _spotShadows.Clear();
            _packer.Clear();

            foreach (var light in scene.Lights)
            {
                if (light == null || !light.CastsShadows) continue;
                var type = (light.LightType ?? "Directional").Trim();
                if (type.Equals("Directional", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("Sun", StringComparison.OrdinalIgnoreCase))
                {
                    var handle = _directionalRenderer.BuildCascades(light, camera, _packer);
                    _directionalShadows.Add(handle);
                }
                else if (type.Equals("Point", StringComparison.OrdinalIgnoreCase))
                {
                    var handle = _pointRenderer.Build(light, _packer);
                    _pointShadows.Add(handle);
                }
                else if (type.Equals("Spot", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("Spotlight", StringComparison.OrdinalIgnoreCase))
                {
                    var handle = _spotRenderer.Build(light, _packer);
                    _spotShadows.Add(handle);
                }
            }
        }

        /// <summary>
        /// Records the shadow depth render passes into the given command list.
        /// Each light/cascade renders depth from the light's perspective into its atlas region.
        /// </summary>
        public void RenderShadowDepth(RHICommandList cmd, IReadOnlyList<RenderMesh> shadowCasters,
            ShadowAtlasResources resources)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (resources?.DepthAtlas == null) return;

            cmd.TransitionTo(resources.DepthAtlas, RHIResourceState.DepthWrite);

            // Clear the entire atlas once.
            cmd.ClearDepthStencilView(resources.DepthDsv, 1.0f, 0, 3);
            cmd.SetViewport(new RHIViewport(0, 0, resources.AtlasSize, resources.AtlasSize, 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, resources.AtlasSize, resources.AtlasSize));

            // Directional cascades.
            foreach (var dir in _directionalShadows)
            {
                for (var i = 0; i < dir.Cascades.Length; i++)
                {
                    var cascade = dir.Cascades[i];
                    var rect = dir.AtlasRects[i];
                    RenderCascadeDepth(cmd, shadowCasters, cascade, rect, dir.Bias, resources.AtlasSize);
                }
            }

            // Spot lights.
            foreach (var spot in _spotShadows)
            {
                RenderSpotDepth(cmd, shadowCasters, spot, resources.AtlasSize);
            }

            // Point lights (6 faces each).
            foreach (var point in _pointShadows)
            {
                RenderPointDepth(cmd, shadowCasters, point, resources.AtlasSize);
            }

            cmd.TransitionTo(resources.DepthAtlas, RHIResourceState.PixelShaderResource);

            // Optional VSM moment generation pass (compute).
            if (resources.VsmAtlas != null)
            {
                cmd.TransitionTo(resources.VsmAtlas, RHIResourceState.UnorderedAccess);
                GenerateVsmMoments(cmd, resources);
                cmd.TransitionTo(resources.VsmAtlas, RHIResourceState.PixelShaderResource);
            }
        }

        private void RenderCascadeDepth(RHICommandList cmd, IReadOnlyList<RenderMesh> casters,
            CascadeFrustum cascade, ShadowAtlasRect rect, ShadowBias bias, int atlasSize)
        {
            cmd.SetViewport(new RHIViewport(rect.X, rect.Y, rect.Width, rect.Height, 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height));
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

            // In a full implementation the actual vertex/index buffers and shadow PSO would be
            // bound here. The shadow pass uses a depth-only / position-only shader with the
            // cascade view-projection. We record the draw setup that the renderer consumes.
            foreach (var mesh in casters)
            {
                if (mesh == null || !mesh.CastShadow) continue;
                // Draw recorded; actual draw uses mesh's vertex/index buffers bound by the scene renderer.
                RecordShadowDraw(cmd, mesh, cascade.ViewProjectionMatrix, bias);
            }
        }

        private void RenderSpotDepth(RHICommandList cmd, IReadOnlyList<RenderMesh> casters,
            ShadowMapHandle handle, int atlasSize)
        {
            var rect = handle.AtlasRect;
            cmd.SetViewport(new RHIViewport(rect.X, rect.Y, rect.Width, rect.Height, 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height));
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

            var vp = handle.ViewProjections.Length > 0 ? handle.ViewProjections[0] : Matrix4x4.Identity;
            foreach (var mesh in casters)
            {
                if (mesh == null || !mesh.CastShadow) continue;
                RecordShadowDraw(cmd, mesh, vp, handle.Bias);
            }
        }

        private void RenderPointDepth(RHICommandList cmd, IReadOnlyList<RenderMesh> casters,
            ShadowMapHandle handle, int atlasSize)
        {
            var res = handle.Resolution;
            var faceOffsets = PointShadowRenderer.GetFaceOffsets(handle.AtlasRect, res);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

            for (var face = 0; face < 6; face++)
            {
                var off = faceOffsets[face];
                cmd.SetViewport(new RHIViewport(off.X, off.Y, res, res, 0, 1));
                cmd.SetScissorRect(new RHIScissorRect((int)off.X, (int)off.Y, (int)off.X + res, (int)off.Y + res));

                var vp = handle.ViewProjections[face];
                foreach (var mesh in casters)
                {
                    if (mesh == null || !mesh.CastShadow) continue;
                    RecordShadowDraw(cmd, mesh, vp, handle.Bias);
                }
            }
        }

        private static void RecordShadowDraw(RHICommandList cmd, RenderMesh mesh, Matrix4x4 vp, ShadowBias bias)
        {
            // The world-view-projection for this shadow draw.
            var wvp = mesh.WorldMatrix * vp;
            // In production the shadow PSO (depth-only, front-face cull, depth bias) is bound and
            // DrawIndexedInstanced is issued with the mesh's GPU buffers. The world matrix is
            // uploaded via the per-draw constant buffer. We keep the signature here so the
            // renderer can drive actual draws through the shared mesh batch pipeline.
            _ = wvp;
            _ = bias;
        }

        private void GenerateVsmMoments(RHICommandList cmd, ShadowAtlasResources resources)
        {
            // Dispatches a compute shader that reads the depth atlas and writes
            // moments (mean, mean^2) into the VSM atlas. The compute shader performs
            // a separable blur for smoother soft shadows.
            var dispatchW = (resources.AtlasSize + 7) / 8;
            var dispatchH = (resources.AtlasSize + 7) / 8;
            cmd.SetComputeRootShaderResourceView(0, resources.DepthAtlas.GpuVirtualAddress);
            cmd.SetComputeRootUnorderedAccessView(1, resources.VsmAtlas!.GpuVirtualAddress);
            cmd.Dispatch(dispatchW, dispatchH, 1);
        }

        // ──────────────────────────────────────────────────────────
        // RenderGraph integration
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// Registers shadow resources and passes into a RenderGraph instance.
        /// Returns the created shadow map resource for downstream lighting passes.
        /// </summary>
        public RenderResource AddToRenderGraph(RenderGraph graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            var shadowAtlas = graph.CreateTexture("ShadowAtlas", RenderResourceFormat.Depth24);
            var vsmAtlas = graph.CreateTexture("ShadowVsmAtlas", RenderResourceFormat.Rgba16Float);

            var shadowPass = graph.AddPass("ShadowDepth", RenderPassKind.Shadow);
            shadowPass.Writes(shadowAtlas);
            if (_settings.FilterMode == ShadowFilterMode.VSM ||
                _settings.FilterMode == ShadowFilterMode.EVSM2 ||
                _settings.FilterMode == ShadowFilterMode.EVSM4)
            {
                shadowPass.Writes(vsmAtlas);
            }

            return shadowAtlas;
        }

        /// <summary>
        /// Builds the constant buffer data for the primary directional light's cascades,
        /// ready to upload to the GPU.
        /// </summary>
        public ShadowCascadesCB BuildCascadesCB(DirectionalShadowHandle? handle)
        {
            if (handle == null || handle.Cascades.Length == 0)
                return new ShadowCascadesCB
                {
                    ShadowMatrices = Matrix4x4.Identity,
                    CascadeSplits = Vector4.Zero,
                    FilterMode = (int)_settings.FilterMode,
                    KernelSize = ShadowKernelGenerator.GetKernel(_settings.FilterMode).Length,
                    ShadowTexelSize = 1f / Math.Max(1, _settings.ShadowMapResolution)
                };

            var cb = new ShadowCascadesCB
            {
                ShadowMatrices = handle.Cascades[0].ViewProjectionMatrix,
                ConstantBias = handle.Bias.ConstantBias,
                SlopeScaledBias = handle.Bias.SlopeScaledBias,
                NormalBias = handle.Bias.NormalBias,
                BlendFraction = handle.BlendFraction,
                FilterMode = (int)_settings.FilterMode,
                KernelSize = ShadowKernelGenerator.GetKernel(_settings.FilterMode).Length,
                ShadowTexelSize = 1f / Math.Max(1, handle.Resolution)
            };

            var splits = new Vector4(0, 0, 0, 0);
            for (var i = 0; i < Math.Min(4, handle.SplitDistances.Length); i++)
                splits[i] = handle.SplitDistances[i];
            cb.CascadeSplits = splits;

            var offsets = new[] { cb.CascadeOffsets0, cb.CascadeOffsets1, cb.CascadeOffsets2, cb.CascadeOffsets3 };
            for (var i = 0; i < Math.Min(4, handle.Cascades.Length); i++)
            {
                var c = handle.Cascades[i];
                offsets[i] = new Vector4(c.AtlasOffset.X, c.AtlasOffset.Y, c.AtlasScale.X, c.AtlasScale.Y);
            }
            cb.CascadeOffsets0 = offsets[0];
            cb.CascadeOffsets1 = offsets[1];
            cb.CascadeOffsets2 = offsets[2];
            cb.CascadeOffsets3 = offsets[3];
            return cb;
        }

        /// <summary>
        /// Builds the per-light constant buffer for a point or spot shadow.
        /// </summary>
        public ShadowPerLightCB BuildPerLightCB(ShadowMapHandle handle)
        {
            if (handle == null)
                return new ShadowPerLightCB { ShadowMatrix = Matrix4x4.Identity };

            return new ShadowPerLightCB
            {
                ShadowMatrix = handle.ShadowMatrix,
                LightPosition = ExtractTranslation(GetLightWorld(handle)),
                LightRange = 0f,
                LightDirection = Vector3.UnitZ,
                Bias = handle.Bias.ConstantBias,
                ShadowType = handle.Type == ShadowMapType.PointCube ? 1 : 0,
                FilterMode = (int)_settings.FilterMode,
                TexelSize = 1f / Math.Max(1, handle.Resolution)
            };
        }

        private static Matrix4x4 GetLightWorld(ShadowMapHandle handle) => Matrix4x4.Identity;

        public float ComputeAtlasUtilization() => _packer.ComputeUtilization();

        public void Dispose()
        {
            _resources?.Dispose();
            _resources = null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Math helpers used throughout the shadow system
    // ──────────────────────────────────────────────────────────────

    internal static class MatrixMath
    {
        public static int NextPowerOfTwo(int value)
        {
            if (value <= 0) return 1;
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        public static int AlignUp(int value, int alignment)
        {
            if (alignment <= 0) return value;
            return (value + alignment - 1) & ~(alignment - 1);
        }

        public static Vector3 ExtractTranslation(Matrix4x4 m) => new(m.M41, m.M42, m.M43);

        public static Vector3 ExtractForward(Matrix4x4 m)
        {
            // Camera/object forward is -Z in view space.
            return Vector3.Normalize(new Vector3(-m.M31, -m.M32, -m.M33));
        }

        public static Vector3 ExtractUp(Matrix4x4 m) => Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
    }
}
