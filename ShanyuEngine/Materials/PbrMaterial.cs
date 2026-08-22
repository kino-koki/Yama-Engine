using System.Numerics;
using System.Text.Json;
using ShanyuEngine.Assets;

namespace ShanyuEngine.Materials
{
    public sealed class PbrMaterial
    {
        public int Version { get; set; } = 1;
        public string Guid { get; set; } = System.Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "PBR Material";

        // -------------------------------------------------------------------
        //  PBR 标量参数
        // -------------------------------------------------------------------

        /// <summary>反照率 (Albedo) 基色, 线性空间 RGB</summary>
        public Vector3 BaseColor { get; set; } = Vector3.One;
        /// <summary>金属度 [0..1], 0 = 电介质, 1 = 金属</summary>
        public float Metallic { get; set; } = 0f;
        /// <summary>粗糙度 [0..1], 0 = 镜面反射, 1 = 完全漫反射</summary>
        public float Roughness { get; set; } = 0.5f;
        /// <summary>不透明度 [0..1]</summary>
        public float Alpha { get; set; } = 1f;
        /// <summary>环境光遮蔽 (AO) 标量 [0..1], 与 OcclusionMap 相乘</summary>
        public float AmbientOcclusion { get; set; } = 1f;
        /// <summary>自发光颜色, 线性空间 RGB</summary>
        public Vector3 EmissiveColor { get; set; } = Vector3.Zero;
        /// <summary>自发光强度 (>= 0)</summary>
        public float EmissiveIntensity { get; set; } = 0f;
        /// <summary>法线贴图强度 (默认 1.0, 负值翻转)</summary>
        public float NormalScale { get; set; } = 1f;
        /// <summary>Alpha 截断阈值 (BlendMode == Cutout 时使用)</summary>
        public float AlphaCutoff { get; set; } = 0.5f;

        // -------------------------------------------------------------------
        //  纹理引用 (资源路径)
        // -------------------------------------------------------------------

        public string BaseColorMap { get; set; } = string.Empty;
        public string NormalMap { get; set; } = string.Empty;
        public string MetallicMap { get; set; } = string.Empty;
        public string RoughnessMap { get; set; } = string.Empty;
        public string OcclusionMap { get; set; } = string.Empty;
        public string EmissiveMap { get; set; } = string.Empty;

        // -------------------------------------------------------------------
        //  UV 变换 (纹理平铺与偏移)
        // -------------------------------------------------------------------

        /// <summary>UV 平铺 (默认 1,1)</summary>
        public Vector2 UVTiling { get; set; } = Vector2.One;
        /// <summary>UV 偏移 (默认 0,0)</summary>
        public Vector2 UVOffset { get; set; } = Vector2.Zero;

        // -------------------------------------------------------------------
        //  渲染状态
        // -------------------------------------------------------------------

        public bool DoubleSided { get; set; }
        public PbrBlendMode BlendMode { get; set; } = PbrBlendMode.Opaque;
        public Dictionary<string, string> Keywords { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNameCaseInsensitive = true
        };

        public static PbrMaterial Create(string name)
        {
            return new PbrMaterial
            {
                Name = string.IsNullOrWhiteSpace(name) ? "PBR Material" : name.Trim()
            };
        }

        public List<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Name)) errors.Add("Material name is required.");
            if (!System.Guid.TryParse(Guid, out _)) errors.Add("Material GUID is invalid.");
            if (Metallic is < 0f or > 1f) errors.Add("Metallic must be in 0..1.");
            if (Roughness is < 0f or > 1f) errors.Add("Roughness must be in 0..1.");
            if (Alpha is < 0f or > 1f) errors.Add("Alpha must be in 0..1.");
            if (AmbientOcclusion is < 0f or > 1f) errors.Add("AmbientOcclusion must be in 0..1.");
            if (EmissiveIntensity < 0f) errors.Add("Emissive intensity must be >= 0.");
            if (AlphaCutoff is < 0f or > 1f) errors.Add("AlphaCutoff must be in 0..1.");
            if (UVTiling.X <= 0f || UVTiling.Y <= 0f) errors.Add("UVTiling must be positive.");
            return errors;
        }

        // -------------------------------------------------------------------
        //  GPU 常量缓冲区打包 (Material Property Packing)
        // -------------------------------------------------------------------

        /// <summary>
        /// 打包后的 PBR 材质常量缓冲区, 遵循 HLSL cbuffer 16 字节对齐规则。
        /// 总大小: 80 字节 (5 x float4)。
        /// <code>
        /// cbuffer PbrMaterialCB : register(b1)
        /// {
        ///     float4 BaseColorAlpha;    // rgb = albedo, a = alpha
        ///     float4 Emissive;          // rgb = emissiveColor, a = emissiveIntensity
        ///     float4 PBRParams;         // x = metallic, y = roughness, z = AO, w = normalScale
        ///     float4 UVTransform0;      // xy = tiling, zw = offset
        ///     float4 BlendParams;       // x = alphaCutoff, y = blendMode, z = doubleSided, w = flags
        /// };
        /// </code>
        /// </summary>
        public struct PbrMaterialConstants
        {
            /// <summary>rgb = Albedo 基色, a = Alpha 不透明度</summary>
            public Vector4 BaseColorAlpha;
            /// <summary>rgb = 自发光颜色, a = 自发光强度</summary>
            public Vector4 Emissive;
            /// <summary>x = Metallic, y = Roughness, z = AmbientOcclusion, w = NormalScale</summary>
            public Vector4 PBRParams;
            /// <summary>xy = UV 平铺, zw = UV 偏移</summary>
            public Vector4 UVTransform;
            /// <summary>x = AlphaCutoff, y = BlendMode, z = DoubleSided(0/1), w = 保留</summary>
            public Vector4 BlendParams;

            /// <summary>常量缓冲区总大小 (字节)</summary>
            public const int SizeInBytes = 80;
        }

        /// <summary>
        /// 将所有标量材质参数打包到 GPU 友好的常量缓冲区结构中。
        /// 纹理引用不包含在此结构中, 需在渲染管线中单独绑定 SRV。
        /// </summary>
        /// <returns>填充完毕的 <see cref="PbrMaterialConstants"/></returns>
        public PbrMaterialConstants GetPackedConstants()
        {
            return new PbrMaterialConstants
            {
                BaseColorAlpha = new Vector4(BaseColor, Alpha),
                Emissive = new Vector4(EmissiveColor, EmissiveIntensity),
                PBRParams = new Vector4(Metallic, Roughness, AmbientOcclusion, NormalScale),
                UVTransform = new Vector4(UVTiling, UVOffset),
                BlendParams = new Vector4(
                    AlphaCutoff,
                    (float)BlendMode,
                    DoubleSided ? 1f : 0f,
                    0f)
            };
        }

        /// <summary>
        /// 将常量缓冲区写入字节数组, 用于直接上传到 GPU constant buffer。
        /// 采用小端序 (x86/x64 平台默认)。
        /// </summary>
        /// <returns>80 字节的原始数据</returns>
        public byte[] GetPackedConstantsBytes()
        {
            var cb = GetPackedConstants();
            var bytes = new byte[PbrMaterialConstants.SizeInBytes];
            int offset = 0;
            WriteVector4(bytes, ref offset, cb.BaseColorAlpha);
            WriteVector4(bytes, ref offset, cb.Emissive);
            WriteVector4(bytes, ref offset, cb.PBRParams);
            WriteVector4(bytes, ref offset, cb.UVTransform);
            WriteVector4(bytes, ref offset, cb.BlendParams);
            return bytes;
        }

        private static void WriteVector4(byte[] buf, ref int offset, Vector4 v)
        {
            BitConverter.TryWriteBytes(new Span<byte>(buf, offset, 4), v.X);
            offset += 4;
            BitConverter.TryWriteBytes(new Span<byte>(buf, offset, 4), v.Y);
            offset += 4;
            BitConverter.TryWriteBytes(new Span<byte>(buf, offset, 4), v.Z);
            offset += 4;
            BitConverter.TryWriteBytes(new Span<byte>(buf, offset, 4), v.W);
            offset += 4;
        }

        public void Write(string path)
        {
            var errors = Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Cannot write invalid material: " + string.Join("; ", errors));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

            AssetMeta.FromMetadata(new AssetMetadata
            {
                Guid = System.Guid.Parse(Guid),
                Name = Name,
                AssetPath = ToProjectAssetPath(path),
                SourceFile = path,
                Type = AssetType.Material,
                FileSize = new FileInfo(path).Length,
                ImportedAt = DateTime.Now,
                LastModified = File.GetLastWriteTime(path),
                Hash = Guid,
                Properties =
                {
                    ["workflow"] = "metallic-roughness",
                    ["doubleSided"] = DoubleSided,
                    ["blendMode"] = BlendMode.ToString(),
                    ["metallic"] = Metallic,
                    ["roughness"] = Roughness,
                    ["ambientOcclusion"] = AmbientOcclusion,
                    ["normalScale"] = NormalScale,
                    ["alphaCutoff"] = AlphaCutoff,
                    ["emissiveIntensity"] = EmissiveIntensity,
                    ["uvTiling"] = $"{UVTiling.X},{UVTiling.Y}",
                    ["uvOffset"] = $"{UVOffset.X},{UVOffset.Y}"
                }
            }).Write(path + ".shmeta");
        }

        public static PbrMaterial Read(string path)
        {
            var material = JsonSerializer.Deserialize<PbrMaterial>(File.ReadAllText(path), JsonOptions)
                           ?? throw new InvalidOperationException($"Invalid PBR material: {path}");
            var errors = material.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Invalid PBR material: " + string.Join("; ", errors));
            return material;
        }

        private static string ToProjectAssetPath(string path)
        {
            var normalized = Path.GetFullPath(path).Replace('\\', '/');
            var marker = "/Assets/";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return normalized[(index + 1)..];
            return Path.GetFileName(path);
        }
    }

    public enum PbrBlendMode
    {
        Opaque,
        Cutout,
        Transparent,
        Additive
    }
}
