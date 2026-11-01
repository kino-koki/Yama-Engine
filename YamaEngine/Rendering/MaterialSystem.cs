// ════════════════════════════════════════════════════════════════════════
// yama引擎 - 材质系统 (Material System)
//
// 命名空间: YamaEngine.Rendering
// 功能:
//   - MaterialTextureType: 纹理类型枚举
//   - MaterialTextureSlot: 纹理槽位封装
//   - MaterialProperties: PBR 材质属性
//   - MaterialAsset: 材质资产定义
//   - MaterialInstance: 运行时材质实例（属性覆盖）
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;
using YamaEngine.ShaderGraph;

namespace YamaEngine.Rendering
{
    // ══════════════════════════════════════════════════════════════
    // MaterialTextureType — 材质纹理类型枚举
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PBR 工作流中支持的纹理类型。
    /// </summary>
    public enum MaterialTextureType
    {
        Albedo = 0,
        Normal = 1,
        Metallic = 2,
        Roughness = 3,
        Occlusion = 4,
        Emissive = 5
    }

    // ══════════════════════════════════════════════════════════════
    // MaterialTextureSlot — 纹理槽位
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质纹理槽位。描述一种纹理类型的绑定信息。
    /// </summary>
    public sealed class MaterialTextureSlot
    {
        /// <summary>纹理类型</summary>
        public MaterialTextureType Type { get; set; }

        /// <summary>GPU 纹理资源</summary>
        public RHITexture? Texture { get; set; }

        /// <summary>纹理的 Shader Resource View 描述符句柄（由 TextureManager 创建）</summary>
        public RHIDescriptorHandle? SrvHandle { get; set; }

        /// <summary>采样器描述（如为 null 则使用默认采样器）</summary>
        public RHISamplerDesc? SamplerDesc { get; set; }

        /// <summary>UV 通道索引（默认 0）</summary>
        public int UVChannel { get; set; } = 0;

        /// <summary>UV 缩放</summary>
        public Vector2 Scale { get; set; } = Vector2.One;

        /// <summary>UV 偏移</summary>
        public Vector2 Offset { get; set; } = Vector2.Zero;

        /// <summary>是否已绑定有效纹理</summary>
        public bool IsValid => Texture != null;

        public MaterialTextureSlot(MaterialTextureType type)
        {
            Type = type;
        }

        public MaterialTextureSlot(MaterialTextureType type, RHITexture texture, RHISamplerDesc? sampler = null)
        {
            Type = type;
            Texture = texture ?? throw new ArgumentNullException(nameof(texture));
            SamplerDesc = sampler;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // MaterialProperties — PBR 材质属性
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// PBR 材质属性集合。与 <see cref="MaterialConstants" /> 布局对应。
    /// </summary>
    public struct MaterialProperties
    {
        /// <summary>基础颜色（RGBA）</summary>
        public Vector4 AlbedoColor;

        /// <summary>金属度（0 = 绝缘体, 1 = 金属）</summary>
        public float Metallic;

        /// <summary>粗糙度（0 = 镜面, 1 = 漫反射）</summary>
        public float Roughness;

        /// <summary>自发光颜色（RGB）</summary>
        public Vector3 EmissiveColor;

        /// <summary>自发光强度</summary>
        public float EmissiveIntensity;

        /// <summary>法线贴图强度</summary>
        public float NormalScale;

        /// <summary>AO 强度</summary>
        public float OcclusionStrength;

        /// <summary>Alpha 裁剪阈值</summary>
        public float AlphaCutoff;

        /// <summary>是否透明（开启 Alpha 混合）</summary>
        public bool IsTransparent;

        /// <summary>是否双面渲染</summary>
        public bool IsDoubleSided;

        /// <summary>是否无光照（Unlit）</summary>
        public bool IsUnlit;

        /// <summary>默认 PBR 材质属性</summary>
        public static MaterialProperties Default => new()
        {
            AlbedoColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f),
            Metallic = 0.0f,
            Roughness = 0.5f,
            EmissiveColor = Vector3.Zero,
            EmissiveIntensity = 0.0f,
            NormalScale = 1.0f,
            OcclusionStrength = 1.0f,
            AlphaCutoff = 0.5f,
            IsTransparent = false,
            IsDoubleSided = false,
            IsUnlit = false
        };

        /// <summary>转换为着色器常量结构</summary>
        public MaterialConstants ToConstants()
        {
            uint flags = 0;
            return new MaterialConstants
            {
                AlbedoColor = AlbedoColor,
                EmissiveColor = EmissiveColor,
                EmissiveIntensity = EmissiveIntensity,
                Metallic = Metallic,
                Roughness = Roughness,
                NormalScale = NormalScale,
                AlphaCutoff = AlphaCutoff,
                TextureFlags = flags
            };
        }
    }

    // ══════════════════════════════════════════════════════════════
    // MaterialAsset — 材质资产
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质资产。定义材质名称、属性以及关联的纹理字典。
    /// </summary>
    public sealed class MaterialAsset
    {
        /// <summary>材质名称</summary>
        public string Name { get; set; } = "";

        /// <summary>全局唯一标识</summary>
        public Guid Guid { get; set; } = Guid.NewGuid();

        /// <summary>PBR 材质属性</summary>
        public MaterialProperties Properties { get; set; } = MaterialProperties.Default;

        /// <summary>纹理字典（类型 -> 槽位）</summary>
        public Dictionary<MaterialTextureType, MaterialTextureSlot> Textures { get; } = new();

        /// <summary>是否已释放</summary>
        public bool IsDisposed { get; private set; }

        public MaterialAsset() { }

        public MaterialAsset(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        /// <summary>
        /// 设置指定类型的纹理槽位。
        /// </summary>
        public void SetTexture(MaterialTextureType type, RHITexture texture, RHISamplerDesc? sampler = null)
        {
            Textures[type] = new MaterialTextureSlot(type, texture, sampler);
        }

        /// <summary>
        /// 移除指定类型的纹理。
        /// </summary>
        public void RemoveTexture(MaterialTextureType type)
        {
            Textures.Remove(type);
        }

        /// <summary>
        /// 获取指定类型的纹理槽位（如不存在返回 null）。
        /// </summary>
        public MaterialTextureSlot? GetTexture(MaterialTextureType type)
        {
            return Textures.TryGetValue(type, out var slot) ? slot : null;
        }

        /// <summary>
        /// 创建默认 Lit 材质（灰色，无纹理）。
        /// </summary>
        public static MaterialAsset CreateDefaultLit(string name = "DefaultLit")
        {
            return new MaterialAsset(name)
            {
                Properties = MaterialProperties.Default
            };
        }

        /// <summary>
        /// 创建无光照材质（自发光，不受光照影响）。
        /// </summary>
        public static MaterialAsset CreateUnlit(string name = "Unlit", Vector4? color = null)
        {
            var mat = new MaterialAsset(name)
            {
                Properties = MaterialProperties.Default
            };
            var props = mat.Properties;
            props.IsUnlit = true;
            props.AlbedoColor = color ?? new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            mat.Properties = props;
            return mat;
        }

        /// <summary>
        /// 创建线框材质（红色，用于调试）。
        /// </summary>
        public static MaterialAsset CreateWireframe(string name = "Wireframe")
        {
            var mat = new MaterialAsset(name)
            {
                Properties = new MaterialProperties
                {
                    AlbedoColor = new Vector4(1.0f, 0.2f, 0.2f, 1.0f),
                    Metallic = 0.0f,
                    Roughness = 1.0f,
                    EmissiveColor = new Vector3(1.0f, 0.2f, 0.2f),
                    EmissiveIntensity = 0.5f,
                    IsUnlit = true
                }
            };
            return mat;
        }

        /// <summary>
        /// 创建金属材质。
        /// </summary>
        public static MaterialAsset CreateMetal(string name = "Metal", Vector4? color = null)
        {
            var mat = new MaterialAsset(name)
            {
                Properties = new MaterialProperties
                {
                    AlbedoColor = color ?? new Vector4(0.72f, 0.73f, 0.75f, 1.0f),
                    Metallic = 1.0f,
                    Roughness = 0.3f
                }
            };
            return mat;
        }

        /// <summary>
        /// 创建塑料/绝缘体材质。
        /// </summary>
        public static MaterialAsset CreatePlastic(string name = "Plastic", Vector4? color = null)
        {
            var mat = new MaterialAsset(name)
            {
                Properties = new MaterialProperties
                {
                    AlbedoColor = color ?? new Vector4(0.9f, 0.1f, 0.1f, 1.0f),
                    Metallic = 0.0f,
                    Roughness = 0.6f
                }
            };
            return mat;
        }

        /// <summary>
        /// 计算纹理标志位掩码（用于着色器常量）。
        /// </summary>
        public uint ComputeTextureFlags()
        {
            uint flags = 0;
            if (Textures.ContainsKey(MaterialTextureType.Albedo)) flags |= 0x01;
            if (Textures.ContainsKey(MaterialTextureType.Normal)) flags |= 0x02;
            if (Textures.ContainsKey(MaterialTextureType.Metallic)) flags |= 0x04;
            if (Textures.ContainsKey(MaterialTextureType.Roughness)) flags |= 0x08;
            if (Textures.ContainsKey(MaterialTextureType.Occlusion)) flags |= 0x10;
            if (Textures.ContainsKey(MaterialTextureType.Emissive)) flags |= 0x20;
            return flags;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Textures.Clear();
        }
    }

    // ══════════════════════════════════════════════════════════════
    // MaterialInstance — 运行时材质实例
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 材质实例。引用父材质资产并允许运行时覆盖属性，不修改父材质。
    /// </summary>
    public sealed class MaterialInstance
    {
        /// <summary>父材质资产</summary>
        public MaterialAsset Parent { get; }

        /// <summary>运行时覆盖的属性（仅覆盖非默认值字段）</summary>
        public MaterialProperties OverrideProperties { get; set; }

        /// <summary>运行时覆盖的纹理（优先于父材质）</summary>
        public Dictionary<MaterialTextureType, MaterialTextureSlot> OverrideTextures { get; } = new();

        /// <summary>合并后的材质属性（覆盖 + 父材质回退）</summary>
        public MaterialProperties ResolvedProperties
        {
            get
            {
                var resolved = Parent.Properties;
                var ov = OverrideProperties;

                // 简单覆盖策略：只要 override 不是默认值就覆盖
                if (ov.AlbedoColor != default) resolved.AlbedoColor = ov.AlbedoColor;
                if (ov.Metallic != 0.0f || OverrideProperties.Metallic != MaterialProperties.Default.Metallic) resolved.Metallic = ov.Metallic;
                if (ov.Roughness != 0.0f || OverrideProperties.Roughness != MaterialProperties.Default.Roughness) resolved.Roughness = ov.Roughness;
                if (ov.EmissiveColor != default) resolved.EmissiveColor = ov.EmissiveColor;
                if (ov.EmissiveIntensity != 0.0f) resolved.EmissiveIntensity = ov.EmissiveIntensity;
                if (ov.NormalScale != 0.0f || OverrideProperties.NormalScale != MaterialProperties.Default.NormalScale) resolved.NormalScale = ov.NormalScale;
                if (ov.OcclusionStrength != 0.0f || OverrideProperties.OcclusionStrength != MaterialProperties.Default.OcclusionStrength) resolved.OcclusionStrength = ov.OcclusionStrength;
                if (ov.AlphaCutoff != 0.0f || OverrideProperties.AlphaCutoff != MaterialProperties.Default.AlphaCutoff) resolved.AlphaCutoff = ov.AlphaCutoff;
                resolved.IsTransparent = ov.IsTransparent || Parent.Properties.IsTransparent;
                resolved.IsDoubleSided = ov.IsDoubleSided || Parent.Properties.IsDoubleSided;
                resolved.IsUnlit = ov.IsUnlit || Parent.Properties.IsUnlit;

                return resolved;
            }
        }

        /// <summary>合并后的纹理标志</summary>
        public uint ResolvedTextureFlags
        {
            get
            {
                uint flags = Parent.ComputeTextureFlags();
                // OverrideTextures 中的纹理也计入
                if (OverrideTextures.ContainsKey(MaterialTextureType.Albedo)) flags |= 0x01;
                if (OverrideTextures.ContainsKey(MaterialTextureType.Normal)) flags |= 0x02;
                if (OverrideTextures.ContainsKey(MaterialTextureType.Metallic)) flags |= 0x04;
                if (OverrideTextures.ContainsKey(MaterialTextureType.Roughness)) flags |= 0x08;
                if (OverrideTextures.ContainsKey(MaterialTextureType.Occlusion)) flags |= 0x10;
                if (OverrideTextures.ContainsKey(MaterialTextureType.Emissive)) flags |= 0x20;
                return flags;
            }
        }

        public MaterialInstance(MaterialAsset parent)
        {
            Parent = parent ?? throw new ArgumentNullException(nameof(parent));
            OverrideProperties = MaterialProperties.Default;
        }

        /// <summary>
        /// 获取合并后的纹理槽位（优先使用 override，否则回退到父材质）。
        /// </summary>
        public MaterialTextureSlot? GetResolvedTexture(MaterialTextureType type)
        {
            if (OverrideTextures.TryGetValue(type, out var ovSlot))
                return ovSlot;
            return Parent.GetTexture(type);
        }

        /// <summary>
        /// 设置运行时覆盖纹理。
        /// </summary>
        public void SetOverrideTexture(MaterialTextureType type, RHITexture texture, RHISamplerDesc? sampler = null)
        {
            OverrideTextures[type] = new MaterialTextureSlot(type, texture, sampler);
        }

        /// <summary>
        /// 移除运行时覆盖纹理。
        /// </summary>
        public void RemoveOverrideTexture(MaterialTextureType type)
        {
            OverrideTextures.Remove(type);
        }

        /// <summary>将覆盖值应用到着色器图属性（材质继承用）。</summary>
        public void ApplyTo(ShaderProperty prop)
        {
            if (prop == null) return;
            _ = prop.Name; // 预留：按属性名映射覆盖值
        }

        /// <summary>判断指定名称的属性是否被显式覆盖。</summary>
        public bool HasOverride(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var def = MaterialProperties.Default;
            return OverrideProperties.Metallic != def.Metallic
                || OverrideProperties.Roughness != def.Roughness
                || OverrideProperties.NormalScale != def.NormalScale
                || OverrideProperties.OcclusionStrength != def.OcclusionStrength
                || OverrideProperties.AlphaCutoff != def.AlphaCutoff
                || OverrideProperties.IsTransparent != def.IsTransparent
                || OverrideProperties.IsDoubleSided != def.IsDoubleSided
                || OverrideProperties.IsUnlit != def.IsUnlit
                || OverrideTextures.Count > 0;
        }

        /// <summary>
        /// 将合并后的属性转换为着色器常量结构。
        /// </summary>
        public MaterialConstants ToConstants()
        {
            var props = ResolvedProperties;
            return new MaterialConstants
            {
                AlbedoColor = props.AlbedoColor,
                EmissiveColor = props.EmissiveColor,
                EmissiveIntensity = props.EmissiveIntensity,
                Metallic = props.Metallic,
                Roughness = props.Roughness,
                NormalScale = props.NormalScale,
                AlphaCutoff = props.AlphaCutoff,
                TextureFlags = ResolvedTextureFlags
            };
        }

        /// <summary>材质显示名。</summary>
        public string Name { get; set; } = "";

        /// <summary>材质类型（用于角色/数字人类材质分类）。</summary>
        public object? Type { get; set; }

        /// <summary>无参构造（供对象初始化器创建占位材质实例）。</summary>
        public MaterialInstance() : this(MaterialAsset.CreateDefaultLit("Instance")) { }

        /// <summary>创建皮肤材质实例。</summary>
        public static MaterialInstance CreateSkinMaterial(object dna) =>
            new MaterialInstance { Name = "Skin", Type = YamaEngine.Character.MaterialType.Skin };

        /// <summary>创建眼睛材质实例。</summary>
        public static MaterialInstance CreateEyeMaterial(object dna) =>
            new MaterialInstance { Name = "Eye", Type = YamaEngine.Character.MaterialType.Eye };

        /// <summary>创建头发材质实例。</summary>
        public static MaterialInstance CreateHairMaterial(object dna) =>
            new MaterialInstance { Name = "Hair", Type = YamaEngine.Character.MaterialType.Hair };
    }
}
