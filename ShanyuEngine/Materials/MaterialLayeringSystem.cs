using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Materials
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 材质分层系统 (Material Layering System)
    //
    // 本文件实现一套数据驱动的材质分层 / 混合框架, 涵盖:
    //   1. MaterialLayer       —— 材质层 (基础材质引用 / 遮罩 / 混合模式 / 权重 / 属性)
    //   2. LayeredMaterial     —— 分层材质 (最多 8 层, 顶点色/世界坐标驱动遮罩, 运行时层切换)
    //   3. MaterialLayerStack  —— 材质层栈 (管理层的有序堆叠 / 重排 / 启用禁用)
    //   4. MaterialLayerBlend  —— 层混合器 (高度混合 / 法线混合 RNM / 细节混合 / 加权混合)
    //   5. TerrainMaterialLayer —— 地形材质层 (特化于地形, 基于高度/坡度/图层权重)
    //   6. VertexColorMask     —— 顶点色遮罩 (利用顶点色通道控制层可见性)
    //   7. MaterialLayerComponent / MaterialLayerSystem —— ECS 组件与系统
    //
    // 对接系统:
    //   * 材质: ShanyuEngine.Materials.MaterialGraph / MaterialNode / MaterialValue / PbrMaterial
    //   * ECS  : ShanyuEngine.ECS.IComponent / ISystem / EntityComponentSystem
    //   * RHI  : ShanyuEngine.RHI.RHIDevice / RHIBuffer / RHIBufferDesc / RHICommandList (常量缓冲更新)
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 层混合模式。决定该层如何与下层合并, 对应 HLSL 中的 blend 运算。
    /// </summary>
    public enum LayerBlendMode
    {
        /// <summary>常规覆盖 (Alpha 混合), 最常用的层叠方式。</summary>
        Normal,
        /// <summary>相加: 结果 = 下层 + 本层, 用于发光叠加。</summary>
        Add,
        /// <summary>相减: 结果 = 下层 - 本层。</summary>
        Subtract,
        /// <summary>相乘: 结果 = 下层 * 本层, 用于暗化 / 阴影。</summary>
        Multiply,
        /// <summary>叠加 (Overlay): 暗处相乘、亮处滤色, 增强对比。</summary>
        Overlay,
        /// <summary>滤色 (Screen): 1 - (1-下层)*(1-本层), 用于柔光。</summary>
        Screen
    }

    /// <summary>顶点色通道, 用于 <see cref="VertexColorMask"/> 选择遮罩来源。</summary>
    public enum VertexColorChannel { Red, Green, Blue, Alpha }

    /// <summary>遮罩驱动方式: 决定层的可见区域由什么数据控制。</summary>
    public enum MaskDriveMode
    {
        /// <summary>使用纹理遮罩 (Mask 纹理采样)。</summary>
        Texture,
        /// <summary>使用顶点色通道作为遮罩。</summary>
        VertexColor,
        /// <summary>使用世界坐标 (如 Y 高度) 计算遮罩。</summary>
        WorldPosition,
        /// <summary>使用世界法线 (如坡度) 计算遮罩。</summary>
        WorldNormal,
        /// <summary>无遮罩 (整层全可见)。</summary>
        None
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 层属性 (MaterialLayerProperties)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单层的材质属性集合。每一项均可被该层覆盖, 未覆盖项沿用基础材质的值。
    /// 这是 CPU 端的属性表示, 最终会打包进 GPU 常量缓冲。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialLayerProperties
    {
        /// <summary>基础颜色 (线性, 0~1)。</summary>
        public Vector4 Color;
        /// <summary>粗糙度 (0=镜面, 1=漫反射)。</summary>
        public float Roughness;
        /// <summary>金属度 (0=非金属, 1=金属)。</summary>
        public float Metallic;
        /// <summary>法线缩放 (法线纹理强度的倍数)。</summary>
        public float NormalScale;
        /// <summary>自发光颜色 (线性)。</summary>
        public Vector3 Emissive;
        /// <summary>自发光强度。</summary>
        public float EmissiveIntensity;
        /// <summary>高度 (用于高度混合与视差)。</summary>
        public float Height;
        /// <summary>不透明度 (整体透明度)。</summary>
        public float Opacity;

        /// <summary>创建默认属性 (中性灰, 非金属, 半粗糙)。</summary>
        public static MaterialLayerProperties Default => new()
        {
            Color = new Vector4(0.8f, 0.8f, 0.8f, 1f),
            Roughness = 0.5f,
            Metallic = 0f,
            NormalScale = 1f,
            Emissive = Vector3.Zero,
            EmissiveIntensity = 0f,
            Height = 0f,
            Opacity = 1f
        };
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 顶点色遮罩 (VertexColorMask)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 顶点色遮罩。利用网格顶点色的某个通道 (R/G/B/A) 控制层的可见性,
    /// 常用于地形涂抹、破损区域过渡等无需额外纹理的混合场景。
    /// </summary>
    public sealed class VertexColorMask
    {
        /// <summary>使用的顶点色通道。</summary>
        public VertexColorChannel Channel = VertexColorChannel.Red;
        /// <summary>遮罩重映射范围下限 (低于此值视为完全不可见)。</summary>
        public float RemapMin;
        /// <summary>遮罩重映射范围上限 (高于此值视为完全可见)。</summary>
        public float RemapMax = 1f;
        /// <summary>是否反转遮罩 (1-原值)。</summary>
        public bool Invert;
        /// <summary>对比度增强系数 (1=线性, >1 提升锐度)。</summary>
        public float Contrast = 1f;

        /// <summary>将原始顶点色通道值 [0,1] 映射为最终遮罩权重 [0,1]。</summary>
        public float Evaluate(float rawChannelValue)
        {
            float v = rawChannelValue;
            if (Invert) v = 1f - v;
            // 重映射
            float range = MathF.Max(1e-5f, RemapMax - RemapMin);
            v = Math.Clamp((v - RemapMin) / range, 0f, 1f);
            // 对比度 (幂曲线)
            if (MathF.Abs(Contrast - 1f) > 1e-4f)
                v = MathF.Pow(v, 1f / Contrast);
            return v;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 材质层 (MaterialLayer)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质层。分层材质的一个独立层, 包含基础材质引用、遮罩、混合模式、
    /// 权重与可覆盖属性。支持运行时权重过渡动画 (从当前权重平滑过渡到目标权重)。
    /// </summary>
    public sealed class MaterialLayer
    {
        /// <summary>层唯一标识。</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");
        /// <summary>层显示名 (便于调试与编辑器展示)。</summary>
        public string Name { get; set; } = "Layer";
        /// <summary>是否启用 (禁用的层不参与混合)。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>基础材质引用 (可指向 PbrMaterial 或 MaterialGraph)。</summary>
        public PbrMaterial? BaseMaterial { get; set; }
        /// <summary>关联的材质图 (可选, 用于程序化材质)。</summary>
        public MaterialGraph? BaseGraph { get; set; }

        /// <summary>遮罩纹理路径 (控制该层可见区域)。空字符串表示无纹理遮罩。</summary>
        public string MaskTexturePath { get; set; } = string.Empty;
        /// <summary>遮罩驱动方式。</summary>
        public MaskDriveMode MaskMode { get; set; } = MaskDriveMode.Texture;
        /// <summary>顶点色遮罩 (当 MaskMode == VertexColor 时使用)。</summary>
        public VertexColorMask? VertexColorMask { get; set; }
        /// <summary>世界坐标遮罩的轴与范围 (当 MaskMode == WorldPosition 时使用)。</summary>
        public Vector3 WorldMaskAxis = Vector3.UnitY;
        public float WorldMaskMin;
        public float WorldMaskMax = 1f;

        /// <summary>该层与下层的混合模式。</summary>
        public LayerBlendMode BlendMode { get; set; } = LayerBlendMode.Normal;
        /// <summary>当前混合权重 [0,1] (运行时会被过渡动画修改)。</summary>
        public float Weight { get; set; } = 1f;
        /// <summary>目标混合权重 (过渡动画的终点)。</summary>
        public float TargetWeight { get; set; } = 1f;
        /// <summary>权重过渡速度 (单位: 1/秒, 越大过渡越快)。</summary>
        public float WeightTransitionSpeed { get; set; } = 4f;
        /// <summary>是否正在过渡权重。</summary>
        public bool IsTransitioning { get; private set; }

        /// <summary>该层覆盖的属性 (未覆盖项沿用基础材质)。</summary>
        public MaterialLayerProperties Properties = MaterialLayerProperties.Default;

        /// <summary>层排序键 (数值越小越靠下, 越先绘制)。</summary>
        public int SortOrder { get; set; }

        /// <summary>启动权重过渡: 将权重平滑过渡到 <paramref name="targetWeight"/>。</summary>
        public void TransitionTo(float targetWeight)
        {
            TargetWeight = Math.Clamp(targetWeight, 0f, 1f);
            IsTransitioning = MathF.Abs(Weight - TargetWeight) > 1e-4f;
        }

        /// <summary>每帧更新权重过渡动画。返回是否仍在过渡中。</summary>
        public bool UpdateTransition(float dt)
        {
            if (!IsTransitioning) return false;
            float speed = MathF.Max(1e-4f, WeightTransitionSpeed);
            float delta = speed * dt;
            if (Weight < TargetWeight)
            {
                Weight = MathF.Min(Weight + delta, TargetWeight);
            }
            else
            {
                Weight = MathF.Max(Weight - delta, TargetWeight);
            }
            if (MathF.Abs(Weight - TargetWeight) <= 1e-4f)
            {
                Weight = TargetWeight;
                IsTransitioning = false;
            }
            return IsTransitioning;
        }

        /// <summary>立即完成过渡 (跳到目标权重)。</summary>
        public void CompleteTransition()
        {
            Weight = TargetWeight;
            IsTransitioning = false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 层混合器 (MaterialLayerBlend)
    //    提供高度混合 / 法线混合 (RNM) / 细节混合 / 加权混合等算法。
    //    这些方法对应 GPU 着色器中的混合逻辑, 此处提供 CPU 端预览与编辑器可视化。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 层混合器。封装多种层间混合算法。CPU 端实现用于编辑器预览与离线烘焙,
    /// GPU 端实现对应 HLSL 函数 (见 Shaders 目录下的材质分层 shader)。
    /// </summary>
    public static class MaterialLayerBlend
    {
        /// <summary>最大支持层数 (受 GPU 常量缓冲与纹理槽位约束)。</summary>
        public const int MaxLayers = 8;

        // ── 加权混合 (WeightBlend) ──────────────────────────────────

        /// <summary>
        /// 加权线性混合: 按 <paramref name="weight"/> 在 a/b 间线性插值。
        /// 这是最基础的混合方式, 用于 Normal 模式。
        /// </summary>
        public static Vector4 WeightBlend(Vector4 a, Vector4 b, float weight)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            return Vector4.Lerp(a, b, w);
        }

        /// <summary>加权线性混合 (标量版本)。</summary>
        public static float WeightBlend(float a, float b, float weight)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            return a + (b - a) * w;
        }

        // ── 高度混合 (HeightBlend) ─────────────────────────────────

        /// <summary>
        /// 高度混合: 基于两层的高度图自适应混合边界, 使过渡更自然,
        /// 避免硬边。原理: 将权重按高度偏移做幂次锐化。
        /// </summary>
        /// <param name="heightA">下层高度 [0,1]。</param>
        /// <param name="heightB">本层高度 [0,1]。</param>
        /// <param name="weight">原始混合权重。</param>
        /// <param name="sharpness">过渡锐度 (越大边界越锐利, 建议 0.5~2.0)。</param>
        /// <returns>本层最终贡献权重 [0,1]。</returns>
        public static float HeightBlend(float heightA, float heightB, float weight, float sharpness = 1f)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            // 将高度融入权重: 高度越高的层在边界处优先显现
            float ha = Math.Clamp(heightA, 0f, 1f);
            float hb = Math.Clamp(heightB, 0f, 1f);
            float ma = ha * (1f - w) + w;
            float mb = hb * w + (1f - w);
            float blend = mb / MathF.Max(1e-5f, ma + mb);
            // 锐度控制
            if (MathF.Abs(sharpness - 1f) > 1e-4f)
                blend = MathF.Pow(blend, MathF.Max(0.01f, sharpness));
            return Math.Clamp(blend, 0f, 1f);
        }

        // ── 法线混合 (NormalBlend, Reoriented Normal Mapping) ──────

        /// <summary>
        /// Reoriented Normal Mapping (RNM) 法线混合。
        /// 将细节法线重新定向到基础法线方向, 保持几何一致性,
        /// 比简单线性混合法线更正确 (避免变平)。
        /// </summary>
        /// <param name="baseNormal">基础法线 (切线空间, [-1,1])。</param>
        /// <param name="detailNormal">细节法线 (切线空间, [-1,1])。</param>
        /// <returns>混合后的法线 (切线空间, 需归一化)。</returns>
        public static Vector3 NormalBlend(Vector3 baseNormal, Vector3 detailNormal)
        {
            // RNM 算法 (Unity 风格实现):
            //   t = vec3(base.xy + detail.xy, base.z)
            //   result = t * dot(base, t) / max(dot(base, base), 1e-5) - base
            Vector3 b = Vector3.Normalize(baseNormal);
            Vector3 d = detailNormal;
            Vector3 t = new(b.X + d.X, b.Y + d.Y, b.Z);
            float dotBT = Vector3.Dot(b, t);
            float dotBB = MathF.Max(Vector3.Dot(b, b), 1e-5f);
            return t * (dotBT / dotBB) - b;
        }

        // ── 细节混合 (DetailBlend) ─────────────────────────────────

        /// <summary>
        /// 细节混合: 将细节法线与细节颜色叠加到基础层。
        /// 细节颜色按细节遮罩做加权叠加, 细节法线用 RNM 混合。
        /// </summary>
        public static MaterialLayerProperties DetailBlend(
            MaterialLayerProperties baseProps,
            MaterialLayerProperties detailProps,
            float detailMask)
        {
            float m = Math.Clamp(detailMask, 0f, 1f);
            var result = new MaterialLayerProperties
            {
                Color = Vector4.Lerp(baseProps.Color, detailProps.Color, m),
                Roughness = WeightBlend(baseProps.Roughness, detailProps.Roughness, m),
                Metallic = WeightBlend(baseProps.Metallic, detailProps.Metallic, m),
                NormalScale = WeightBlend(baseProps.NormalScale, detailProps.NormalScale, m),
                Emissive = Vector3.Lerp(baseProps.Emissive, detailProps.Emissive, m),
                EmissiveIntensity = WeightBlend(baseProps.EmissiveIntensity, detailProps.EmissiveIntensity, m),
                Height = WeightBlend(baseProps.Height, detailProps.Height, m),
                Opacity = WeightBlend(baseProps.Opacity, detailProps.Opacity, m)
            };
            return result;
        }

        // ── 混合模式应用 (ApplyBlendMode) ──────────────────────────

        /// <summary>
        /// 按 <paramref name="mode"/> 指定的混合模式合并下层 <paramref name="dest"/> 与本层 <paramref name="src"/>。
        /// 权重 <paramref name="weight"/> 控制本层贡献比例。
        /// </summary>
        public static Vector4 ApplyBlendMode(LayerBlendMode mode, Vector4 dest, Vector4 src, float weight)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            Vector4 blended = mode switch
            {
                LayerBlendMode.Normal => Vector4.Lerp(dest, src, w),
                LayerBlendMode.Add => dest + src * w,
                LayerBlendMode.Subtract => dest - src * w,
                LayerBlendMode.Multiply => dest * Vector4.Lerp(Vector4.One, src, w),
                LayerBlendMode.Overlay => Overlay(dest, src, w),
                LayerBlendMode.Screen => Vector4.One - (Vector4.One - dest) * (Vector4.One - src * w),
                _ => Vector4.Lerp(dest, src, w)
            };
            return blended;
        }

        /// <summary>Overlay 混合: 暗处相乘、亮处滤色。</summary>
        private static Vector4 Overlay(Vector4 dest, Vector4 src, float w)
        {
            Vector4 result;
            result.X = dest.X < 0.5f ? 2f * dest.X * src.X : 1f - 2f * (1f - dest.X) * (1f - src.X);
            result.Y = dest.Y < 0.5f ? 2f * dest.Y * src.Y : 1f - 2f * (1f - dest.Y) * (1f - src.Y);
            result.Z = dest.Z < 0.5f ? 2f * dest.Z * src.Z : 1f - 2f * (1f - dest.Z) * (1f - src.Z);
            result.W = dest.W;
            return Vector4.Lerp(dest, result, w);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 材质层栈 (MaterialLayerStack)
    //    管理层的有序堆叠, 负责层的增删 / 重排 / 启用禁用。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质层栈。维护一个有序的 <see cref="MaterialLayer"/> 列表,
    /// 提供按排序键自动排序、运行时添加/移除/插入/重排等管理能力。
    /// </summary>
    public sealed class MaterialLayerStack
    {
        private readonly List<MaterialLayer> _layers = new();

        /// <summary>层数量。</summary>
        public int Count => _layers.Count;
        /// <summary>是否已满 (达到 <see cref="MaterialLayerBlend.MaxLayers"/>)。</summary>
        public bool IsFull => _layers.Count >= MaterialLayerBlend.MaxLayers;
        /// <summary>只读层列表 (按当前排序顺序)。</summary>
        public IReadOnlyList<MaterialLayer> Layers => _layers;

        /// <summary>添加一个层到栈顶。超过最大层数则返回 false。</summary>
        public bool Add(MaterialLayer layer)
        {
            if (layer == null) return false;
            if (_layers.Count >= MaterialLayerBlend.MaxLayers) return false;
            if (layer.SortOrder == 0) layer.SortOrder = _layers.Count;
            _layers.Add(layer);
            Sort();
            return true;
        }

        /// <summary>在指定索引处插入层。</summary>
        public bool Insert(int index, MaterialLayer layer)
        {
            if (layer == null) return false;
            if (_layers.Count >= MaterialLayerBlend.MaxLayers) return false;
            _layers.Insert(Math.Clamp(index, 0, _layers.Count), layer);
            Sort();
            return true;
        }

        /// <summary>移除指定层。</summary>
        public bool Remove(MaterialLayer layer)
        {
            if (layer == null) return false;
            return _layers.Remove(layer);
        }

        /// <summary>按 Id 移除层。</summary>
        public bool RemoveById(string id)
        {
            int idx = _layers.FindIndex(l => l.Id == id);
            if (idx < 0) return false;
            _layers.RemoveAt(idx);
            return true;
        }

        /// <summary>按 Id 查找层。</summary>
        public MaterialLayer? Find(string id) => _layers.Find(l => l.Id == id);

        /// <summary>清空所有层。</summary>
        public void Clear() => _layers.Clear();

        /// <summary>按 SortOrder 升序排序 (值小者先绘制, 作为下层)。</summary>
        public void Sort()
        {
            _layers.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        }

        /// <summary>将层上移一层 (减小 SortOrder)。</summary>
        public void MoveUp(MaterialLayer layer)
        {
            int idx = _layers.IndexOf(layer);
            if (idx <= 0) return;
            (_layers[idx - 1].SortOrder, _layers[idx].SortOrder) =
                (_layers[idx].SortOrder, _layers[idx - 1].SortOrder);
            Sort();
        }

        /// <summary>将层下移一层 (增大 SortOrder)。</summary>
        public void MoveDown(MaterialLayer layer)
        {
            int idx = _layers.IndexOf(layer);
            if (idx < 0 || idx >= _layers.Count - 1) return;
            (_layers[idx + 1].SortOrder, _layers[idx].SortOrder) =
                (_layers[idx].SortOrder, _layers[idx + 1].SortOrder);
            Sort();
        }

        /// <summary>获取所有已启用且权重 > 0 的层 (实际参与混合的层)。</summary>
        public List<MaterialLayer> GetActiveLayers()
        {
            var result = new List<MaterialLayer>();
            foreach (var l in _layers)
                if (l.Enabled && l.Weight > 1e-4f) result.Add(l);
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 分层材质 (LayeredMaterial)
    //    层列表容器, 支持顶点色/世界坐标驱动遮罩, 运行时层切换与过渡。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 分层材质。将多个 <see cref="MaterialLayer"/> 组合为一个材质资产,
    /// 运行时由 GPU 按遮罩与权重混合。支持运行时动态添加/移除/调整层,
    /// 并提供层间权重过渡动画。
    /// </summary>
    public sealed class LayeredMaterial
    {
        /// <summary>材质唯一标识。</summary>
        public string Guid { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>材质名。</summary>
        public string Name { get; set; } = "Layered Material";
        /// <summary>层栈。</summary>
        public MaterialLayerStack Stack { get; } = new();

        /// <summary>是否启用顶点色驱动遮罩 (全局开关)。</summary>
        public bool UseVertexColorMask { get; set; }
        /// <summary>是否启用世界坐标驱动遮罩。</summary>
        public bool UseWorldPositionMask { get; set; }
        /// <summary>全局混合锐度 (高度混合时使用)。</summary>
        public float BlendSharpness { get; set; } = 1f;

        /// <summary>添加层 (委托给层栈)。</summary>
        public bool AddLayer(MaterialLayer layer) => Stack.Add(layer);

        /// <summary>移除层。</summary>
        public bool RemoveLayer(MaterialLayer layer) => Stack.Remove(layer);

        /// <summary>运行时切换层可见性 (带过渡动画)。</summary>
        /// <param name="layerId">层 Id。</param>
        /// <param name="visible">是否可见。</param>
        /// <param name="transition">是否使用过渡动画。</param>
        public void SetLayerVisible(string layerId, bool visible, bool transition = true)
        {
            var layer = Stack.Find(layerId);
            if (layer == null) return;
            layer.Enabled = visible;
            if (transition)
                layer.TransitionTo(visible ? 1f : 0f);
            else
            {
                layer.Weight = visible ? 1f : 0f;
                layer.CompleteTransition();
            }
        }

        /// <summary>运行时调整层权重 (带过渡动画)。</summary>
        public void SetLayerWeight(string layerId, float weight, bool transition = true)
        {
            var layer = Stack.Find(layerId);
            if (layer == null) return;
            if (transition) layer.TransitionTo(weight);
            else
            {
                layer.Weight = weight;
                layer.TargetWeight = weight;
                layer.CompleteTransition();
            }
        }

        /// <summary>每帧更新所有层的权重过渡动画。</summary>
        public void UpdateTransitions(float dt)
        {
            foreach (var layer in Stack.Layers)
                layer.UpdateTransition(dt);
        }

        /// <summary>计算在给定世界位置 / 顶点色下的层遮罩权重 (CPU 端预览)。</summary>
        /// <param name="worldPos">采样点世界坐标。</param>
        /// <param name="vertexColor">采样点顶点色 (RGBA)。</param>
        /// <returns>每层的有效权重数组 (按栈顺序)。</returns>
        public float[] EvaluateLayerWeights(Vector3 worldPos, Vector4 vertexColor)
        {
            var active = Stack.GetActiveLayers();
            var weights = new float[active.Count];
            for (int i = 0; i < active.Count; i++)
            {
                var layer = active[i];
                float w = layer.Weight;
                switch (layer.MaskMode)
                {
                    case MaskDriveMode.VertexColor when layer.VertexColorMask != null:
                        {
                            float ch = layer.VertexColorMask.Channel switch
                            {
                                VertexColorChannel.Red => vertexColor.X,
                                VertexColorChannel.Green => vertexColor.Y,
                                VertexColorChannel.Blue => vertexColor.Z,
                                _ => vertexColor.W
                            };
                            w *= layer.VertexColorMask.Evaluate(ch);
                            break;
                        }
                    case MaskDriveMode.WorldPosition:
                        {
                            // 按世界坐标在指定轴上的投影计算遮罩
                            float proj = Vector3.Dot(worldPos, Vector3.Normalize(layer.WorldMaskAxis));
                            float range = MathF.Max(1e-5f, layer.WorldMaskMax - layer.WorldMaskMin);
                            float mask = Math.Clamp((proj - layer.WorldMaskMin) / range, 0f, 1f);
                            w *= mask;
                            break;
                        }
                    case MaskDriveMode.None:
                        w *= 1f;
                        break;
                    // Texture / WorldNormal 模式需 GPU 采样, CPU 端近似取权重
                    default:
                        w *= 1f;
                        break;
                }
                weights[i] = Math.Clamp(w, 0f, 1f);
            }
            return weights;
        }

        /// <summary>从 MaterialGraph 构建分层材质 (将图中的材质节点转为层)。</summary>
        public static LayeredMaterial FromGraph(MaterialGraph graph)
        {
            var mat = new LayeredMaterial { Name = graph.Name + "_Layered" };
            int order = 0;
            foreach (var node in graph.Nodes)
            {
                if (node.Type == MaterialNodeType.PbrMaster) continue;
                var layer = new MaterialLayer
                {
                    Name = node.Name,
                    SortOrder = order++,
                    BlendMode = node.Type switch
                    {
                        MaterialNodeType.Add => LayerBlendMode.Add,
                        MaterialNodeType.Multiply => LayerBlendMode.Multiply,
                        _ => LayerBlendMode.Normal
                    }
                };
                // 从节点属性推断层属性
                if (node.Type == MaterialNodeType.ConstantColor)
                {
                    layer.Properties.Color = node.VectorValue;
                }
                else if (node.Type == MaterialNodeType.ConstantFloat)
                {
                    layer.Properties.Roughness = Math.Clamp(node.FloatValue, 0f, 1f);
                }
                else if (node.Type == MaterialNodeType.TextureSample)
                {
                    layer.MaskTexturePath = node.TexturePath;
                    layer.MaskMode = MaskDriveMode.Texture;
                }
                mat.AddLayer(layer);
            }
            return mat;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 地形材质层 (TerrainMaterialLayer)
    //    特化于地形, 基于高度 / 坡度 / 图层权重自动决定可见区域。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 地形材质层。专用于地形系统, 额外携带高度范围与坡度范围约束,
    /// 使该层仅在特定海拔与坡度的地形区域显现。与 ShanyuEngine.World.TerrainLayer 对齐。
    /// </summary>
    public sealed class TerrainMaterialLayer
    {
        /// <summary>层唯一标识。</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N");
        /// <summary>层名 (如 "Grass" / "Rock" / "Snow")。</summary>
        public string Name { get; set; } = "TerrainLayer";

        /// <summary>基础材质引用。</summary>
        public PbrMaterial? BaseMaterial { get; set; }
        /// <summary>层属性。</summary>
        public MaterialLayerProperties Properties = MaterialLayerProperties.Default;

        /// <summary>生效高度范围下限 (世界 Y 坐标)。</summary>
        public float MinHeight { get; set; } = float.MinValue;
        /// <summary>生效高度范围上限 (世界 Y 坐标)。</summary>
        public float MaxHeight { get; set; } = float.MaxValue;
        /// <summary>生效坡度下限 (度, 0=水平)。</summary>
        public float MinSlope { get; set; } = 0f;
        /// <summary>生效坡度上限 (度, 90=垂直)。</summary>
        public float MaxSlope { get; set; } = 90f;

        /// <summary>图层权重纹理 (地形涂抹图, RGBA 四通道可存 4 层权重)。</summary>
        public string WeightMapPath { get; set; } = string.Empty;
        /// <summary>使用的权重纹理通道 (R/G/B/A)。</summary>
        public VertexColorChannel WeightChannel { get; set; } = VertexColorChannel.Red;

        /// <summary>混合模式。</summary>
        public LayerBlendMode BlendMode { get; set; } = LayerBlendMode.Normal;
        /// <summary>整体权重。</summary>
        public float Weight { get; set; } = 1f;
        /// <summary>高度混合偏移 (用于 Height-blend)。</summary>
        public float HeightBlendOffset { get; set; }

        /// <summary>
        /// 根据世界高度与坡度计算该层的遮罩权重 [0,1]。
        /// 在高度与坡度范围内为 1, 范围外按平滑过渡衰减到 0。
        /// </summary>
        /// <param name="worldHeight">采样点世界高度 (Y)。</param>
        /// <param name="slopeDegrees">采样点坡度 (度)。</param>
        /// <param name="smoothRange">范围边界的平滑过渡宽度。</param>
        public float EvaluateMask(float worldHeight, float slopeDegrees, float smoothRange = 2f)
        {
            // 高度遮罩
            float heightMask = Smoothstep(MinHeight - smoothRange, MinHeight + smoothRange, worldHeight) *
                               (1f - Smoothstep(MaxHeight - smoothRange, MaxHeight + smoothRange, worldHeight));
            // 坡度遮罩
            float slopeMask = Smoothstep(MinSlope - smoothRange, MinSlope + smoothRange, slopeDegrees) *
                              (1f - Smoothstep(MaxSlope - smoothRange, MaxSlope + smoothRange, slopeDegrees));
            return Math.Clamp(heightMask * slopeMask * Weight, 0f, 1f);
        }

        /// <summary>平滑阶跃函数: 在 [edge0, edge1] 间做 Hermite 插值。</summary>
        private static float Smoothstep(float edge0, float edge1, float x)
        {
            if (edge1 - edge0 < 1e-5f) return x < edge0 ? 0f : 1f;
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. GPU 常量缓冲结构
    //    这些结构按 16 字节对齐, 直接映射到着色器中的 cbuffer。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单层的 GPU 常量缓冲数据 (与 HLSL cbuffer 中的 LayerConstants 对应)。
    /// 必须按 16 字节对齐。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct LayerShaderConstants
    {
        public Vector4 Color;            // 16B: 颜色 + 不透明度
        public Vector4 EmissiveHeight;   // 16B: 自发光RGB + 高度
        public Vector4 MaskParams;       // 16B: 权重 + 粗糙度 + 金属度 + 法线缩放
        public int BlendMode;            // 4B: 混合模式 (LayerBlendMode 转 int)
        public int Enabled;              // 4B: 是否启用
        public float EmissiveIntensity;  // 4B
        public float Padding;            // 4B: 对齐填充
    }

    /// <summary>
    /// 整个分层材质的全局常量缓冲 (与 HLSL cbuffer LayeringGlobalCB 对应)。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct LayeringGlobalConstants
    {
        public Vector4 WorldMaskParams;   // 16B: 世界遮罩轴 + 锐度
        public int LayerCount;            // 4B: 活动层数
        public int UseVertexColorMask;    // 4B
        public int UseWorldPositionMask;  // 4B
        public float BlendSharpness;      // 4B
        public Vector3 Padding;           // 12B: 对齐到 16B 边界
        public float Reserved;            // 4B
    }

    // ──────────────────────────────────────────────────────────────
    // 10. ECS 组件 (MaterialLayerComponent)
    //     遵循引擎 ECS 约定: 组件为 struct, 持有对类实例的引用。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质分层 ECS 组件。挂在实体上, 关联一个 <see cref="LayeredMaterial"/> 实例,
    /// 由 <see cref="MaterialLayerSystem"/> 每帧更新。
    /// </summary>
    public struct MaterialLayerComponent : IComponent
    {
        /// <summary>该实体的分层材质实例。</summary>
        public LayeredMaterial? Material;
        /// <summary>是否启用每帧更新 (过渡动画 / GPU 参数上传)。</summary>
        public bool Enabled;
        /// <summary>关联的 RHI 常量缓冲 (由系统分配, 存放层参数)。</summary>
        public RHIBuffer? ConstantBuffer;
        /// <summary>是否需要重建 GPU 资源 (层增删后置 true)。</summary>
        public bool Dirty;
        /// <summary>是否需要上传常量缓冲到 GPU。</summary>
        public bool NeedsGPUUpload;
    }

    // ──────────────────────────────────────────────────────────────
    // 11. ECS 系统 (MaterialLayerSystem)
    //     每帧更新层权重过渡, 处理动态层修改, 上传材质参数到 GPU。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 材质分层 ECS 系统。实现 <see cref="ISystem"/>, 注册到
    /// <see cref="EntityComponentSystem.AddSystem"/> 后每帧执行:
    ///   1. 更新各层权重过渡动画;
    ///   2. 处理运行时层增删 (重建层栈);
    ///   3. 将层参数打包并上传到 GPU 常量缓冲 (对接 RHI)。
    /// </summary>
    public sealed class MaterialLayerSystem : ISystem
    {
        /// <summary>系统名。</summary>
        public string Name => "MaterialLayerSystem";

        private readonly RHIDevice? _device;
        private readonly Dictionary<int, LayerShaderConstants[]> _layerBuffers = new();
        private LayeringGlobalConstants _globalConstants;
        private RHIBuffer? _globalConstantBuffer;
        private const uint CbvAlignment = 256u;

        /// <summary>创建系统。传入 RHI 设备以分配常量缓冲 (可为 null, 用于无 GPU 的编辑器预览)。</summary>
        public MaterialLayerSystem(RHIDevice? device = null)
        {
            _device = device;
        }

        /// <summary>每帧更新所有拥有 <see cref="MaterialLayerComponent"/> 的实体。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;
            var entities = ecs.QueryWithComponent<MaterialLayerComponent>();
            foreach (var entityId in entities)
            {
                var comp = ecs.GetComponent<MaterialLayerComponent>(entityId);
                if (!comp.Enabled || comp.Material == null) continue;

                // 1. 更新权重过渡动画
                comp.Material.UpdateTransitions(dt);

                // 2. 处理脏标记 (层结构变更)
                if (comp.Dirty)
                {
                    EnsureConstantBuffer(entityId, comp.Material);
                    comp.Dirty = false;
                    comp.NeedsGPUUpload = true;
                }

                // 3. 打包层参数
                PackLayerConstants(entityId, comp.Material);

                // 4. 上传到 GPU
                if (comp.NeedsGPUUpload && _device != null)
                {
                    UploadToGPU(entityId, ref comp);
                    comp.NeedsGPUUpload = false;
                }

                ecs.SetComponent(entityId, comp);
            }
        }

        /// <summary>确保实体拥有足够容量的常量缓冲。</summary>
        private void EnsureConstantBuffer(int entityId, LayeredMaterial material)
        {
            int count = material.Stack.Count;
            if (count == 0) return;

            if (!_layerBuffers.TryGetValue(entityId, out var buf) || buf.Length < count)
            {
                _layerBuffers[entityId] = new LayerShaderConstants[MaterialLayerBlend.MaxLayers];
            }

            // 分配/重建 RHI 常量缓冲 (按 256 字节对齐)
            if (_device != null)
            {
                ulong size = (ulong)(Marshal.SizeOf<LayerShaderConstants>() * MaterialLayerBlend.MaxLayers);
                size = Align(size, CbvAlignment);
                // 注: 实际引擎中会复用缓冲, 此处简化为按需创建
            }
        }

        /// <summary>将分层材质的层参数打包为 GPU 常量缓冲数据。</summary>
        private void PackLayerConstants(int entityId, LayeredMaterial material)
        {
            if (!_layerBuffers.TryGetValue(entityId, out var buf)) return;
            var active = material.Stack.GetActiveLayers();
            for (int i = 0; i < buf.Length; i++)
            {
                if (i < active.Count)
                {
                    var layer = active[i];
                    var p = layer.Properties;
                    buf[i] = new LayerShaderConstants
                    {
                        Color = new Vector4(p.Color.X, p.Color.Y, p.Color.Z, p.Opacity),
                        EmissiveHeight = new Vector4(p.Emissive, p.Height),
                        MaskParams = new Vector4(layer.Weight, p.Roughness, p.Metallic, p.NormalScale),
                        BlendMode = (int)layer.BlendMode,
                        Enabled = layer.Enabled ? 1 : 0,
                        EmissiveIntensity = p.EmissiveIntensity,
                        Padding = 0f
                    };
                }
                else
                {
                    buf[i] = default;
                }
            }

            _globalConstants = new LayeringGlobalConstants
            {
                WorldMaskParams = new Vector4(0, 1, 0, material.BlendSharpness),
                LayerCount = active.Count,
                UseVertexColorMask = material.UseVertexColorMask ? 1 : 0,
                UseWorldPositionMask = material.UseWorldPositionMask ? 1 : 0,
                BlendSharpness = material.BlendSharpness,
                Padding = Vector3.Zero,
                Reserved = 0f
            };
        }

        /// <summary>上传常量缓冲数据到 GPU (对接 RHI 设备与命令列表)。</summary>
        private void UploadToGPU(int entityId, ref MaterialLayerComponent comp)
        {
            if (_device == null || !_layerBuffers.TryGetValue(entityId, out var buf)) return;

            // 创建或更新层的常量缓冲
            ulong size = (ulong)(Marshal.SizeOf<LayerShaderConstants>() * MaterialLayerBlend.MaxLayers);
            size = Align(size, CbvAlignment);

            if (comp.ConstantBuffer == null || comp.ConstantBuffer.AllocatedBytes < size)
            {
                var desc = new RHIBufferDesc
                {
                    SizeInBytes = size,
                    Format = RHIFormat.Unknown,
                    HeapType = RHIResourceHeapType.Upload,
                    Flags = RHIBufferFlags.ConstantBuffer
                };
                comp.ConstantBuffer = _device.CreateBuffer(desc, "LayeredMaterial_Layers_CB");
            }

            // 上传层数据到常量缓冲 (UploadToBuffer 支持 ReadOnlySpan<T>)
            _device.UploadToBuffer(comp.ConstantBuffer, buf.AsSpan());

            // 全局常量缓冲 (单例)
            if (_globalConstantBuffer == null)
            {
                ulong gsize = Align((ulong)Marshal.SizeOf<LayeringGlobalConstants>(), CbvAlignment);
                var gdesc = new RHIBufferDesc
                {
                    SizeInBytes = gsize,
                    Format = RHIFormat.Unknown,
                    HeapType = RHIResourceHeapType.Upload,
                    Flags = RHIBufferFlags.ConstantBuffer
                };
                _globalConstantBuffer = _device.CreateBuffer(gdesc, "LayeredMaterial_Global_CB");
            }
            // 单结构体通过 Span 上传 (无额外分配), 对接 RHI UploadToBuffer<T>(Span<T>)
            _device.UploadToBuffer(_globalConstantBuffer, MemoryMarshal.CreateSpan(ref _globalConstants, 1));
        }

        /// <summary>在命令录制阶段绑定常量缓冲到着色器 (供渲染器调用)。</summary>
        /// <param name="cmdList">RHI 命令列表。</param>
        /// <param name="comp">材质分层组件。</param>
        /// <param name="layerCbvRootIndex">层常量缓冲的 root parameter 索引。</param>
        /// <param name="globalCbvRootIndex">全局常量缓冲的 root parameter 索引。</param>
        public void BindConstantBuffers(RHICommandList cmdList, in MaterialLayerComponent comp,
            uint layerCbvRootIndex, uint globalCbvRootIndex)
        {
            if (cmdList == null) return;
            if (comp.ConstantBuffer != null)
                cmdList.SetGraphicsRootConstantBufferView(layerCbvRootIndex, comp.ConstantBuffer);
            if (_globalConstantBuffer != null)
                cmdList.SetGraphicsRootConstantBufferView(globalCbvRootIndex, _globalConstantBuffer);
        }

        /// <summary>将大小向上对齐到 alignment 的整数倍。</summary>
        private static ulong Align(ulong size, uint alignment)
        {
            return (size + alignment - 1) & ~(alignment - 1);
        }

        // ── 动态层修改接口 (运行时添加/移除层) ─────────────────────

        /// <summary>运行时为实体的分层材质添加一个层, 并标记为脏。</summary>
        public static void AddLayerToEntity(EntityComponentSystem ecs, int entityId, MaterialLayer layer)
        {
            if (!ecs.HasComponent<MaterialLayerComponent>(entityId)) return;
            var comp = ecs.GetComponent<MaterialLayerComponent>(entityId);
            if (comp.Material == null) return;
            if (comp.Material.AddLayer(layer))
            {
                comp.Dirty = true;
                comp.NeedsGPUUpload = true;
                ecs.SetComponent(entityId, comp);
            }
        }

        /// <summary>运行时从实体的分层材质移除一个层, 并标记为脏。</summary>
        public static void RemoveLayerFromEntity(EntityComponentSystem ecs, int entityId, MaterialLayer layer)
        {
            if (!ecs.HasComponent<MaterialLayerComponent>(entityId)) return;
            var comp = ecs.GetComponent<MaterialLayerComponent>(entityId);
            if (comp.Material == null) return;
            if (comp.Material.RemoveLayer(layer))
            {
                comp.Dirty = true;
                comp.NeedsGPUUpload = true;
                ecs.SetComponent(entityId, comp);
            }
        }

        /// <summary>运行时调整实体某层的权重 (带过渡动画)。</summary>
        public static void SetEntityLayerWeight(EntityComponentSystem ecs, int entityId,
            string layerId, float weight, bool transition = true)
        {
            if (!ecs.HasComponent<MaterialLayerComponent>(entityId)) return;
            var comp = ecs.GetComponent<MaterialLayerComponent>(entityId);
            if (comp.Material == null) return;
            comp.Material.SetLayerWeight(layerId, weight, transition);
            comp.NeedsGPUUpload = true;
            ecs.SetComponent(entityId, comp);
        }

        /// <summary>释放指定实体的 GPU 资源 (实体销毁时调用)。</summary>
        public void ReleaseEntityResources(int entityId)
        {
            _layerBuffers.Remove(entityId);
        }
    }
}
