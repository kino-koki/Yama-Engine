using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 贴花系统
    //
    // 类似 UE Decal Actor / Decal Component:
    //   * DecalComponent: 贴花组件 (材质/大小/旋转/衰减/排序/投影类型)
    //   * DecalSortOrder: 渲染排序 (Before/After/AfterBasePass)
    //   * DecalActor: 贴花 Actor
    //   * DecalMaterial: 贴花材质 (漫射/法线/粗糙度/自发光)
    //   * 贴花投影 (球面/盒体/定向投影)
    //   * 贴花管理 (池化/距离剔除/最大贴花数限制)
    // ════════════════════════════════════════════════════════════════════════

    #region DecalSortOrder

    /// <summary>贴花渲染排序顺序。</summary>
    public enum DecalSortOrder
    {
        /// <summary>在基础通道之前渲染。</summary>
        BeforeBasePass,
        /// <summary>在基础通道之后渲染 (Deferred Only)。</summary>
        AfterBasePass,
        /// <summary>在常规贴花之后渲染。</summary>
        AfterDeferred
    }

    #endregion

    #region DecalProjectionType

    /// <summary>贴花投影类型。</summary>
    public enum DecalProjectionType
    {
        /// <summary>球面投影 (从贴花中心向球面投影)。</summary>
        Sphere,
        /// <summary>盒体投影 (从贴花中心向 AABB 盒体投影)。</summary>
        Box,
        /// <summary>定向投影 (沿指定方向投影)。</summary>
        Directional,
        /// <summary>平面投影 (向平面投影)。</summary>
        Planar
    }

    #endregion

    #region DecalMaterial

    /// <summary>
    /// 贴花材质。支持漫射/法线/粗糙度/自发光贴花通道。
    /// </summary>
    public class DecalMaterial
    {
        // ── 材质属性 ──
        public string Name { get; set; } = "DecalMaterial";
        public string DiffuseTexture { get; set; }
        public string NormalTexture { get; set; }
        public string RoughnessTexture { get; set; }
        public string EmissiveTexture { get; set; }
        public string ORMTexture { get; set; } // Occlusion/Roughness/Metallic 打包

        // ── 颜色 ──
        public Vector4 DiffuseColor { get; set; } = Vector4.One;
        public Vector3 EmissiveColor { get; set; } = Vector3.Zero;
        public float EmissiveIntensity { get; set; } = 1f;

        // ── 参数 ──
        public float Opacity { get; set; } = 1f;
        public float NormalIntensity { get; set; } = 1f;
        public float RoughnessScale { get; set; } = 1f;
        public float MetallicScale { get; set; } = 0f;
        public float AOIntensity { get; set; } = 1f;

        // ── 混合 ──
        public bool UseDitheredOpacity { get; set; }
        public float FadeStart { get; set; } = 0f;
        public float FadeEnd { get; set; } = 1f;
        public float FadePower { get; set; } = 1f;

        // ── UV ──
        public Vector2 Tiling { get; set; } = Vector2.One;
        public Vector2 Offset { get; set; } = Vector2.Zero;
        public float Rotation { get; set; } = 0f;

        /// <summary>是否包含法线贴图。</summary>
        public bool HasNormal => !string.IsNullOrEmpty(NormalTexture);

        /// <summary>是否包含粗糙度贴图。</summary>
        public bool HasRoughness => !string.IsNullOrEmpty(RoughnessTexture) || !string.IsNullOrEmpty(ORMTexture);

        /// <summary>是否包含自发光贴图。</summary>
        public bool HasEmissive => !string.IsNullOrEmpty(EmissiveTexture) || EmissiveColor.LengthSquared() > 0.001f;
    }

    #endregion

    #region DecalComponent

    /// <summary>
    /// 贴花组件。挂载到 Actor 上实现贴花投影。
    /// 类似 UE UDecalComponent。
    /// </summary>
    public class DecalComponent
    {
        // ── 标识 ──
        public string Name { get; set; } = "DecalComponent";
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsEnabled { get; set; } = true;
        public bool IsVisible { get; set; } = true;

        // ── 变换 ──
        public Vector3 Position { get; set; } = Vector3.Zero;
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Forward { get; set; } = -Vector3.UnitZ; // 投影方向
        public Vector3 Up { get; set; } = Vector3.UnitY;
        public Vector3 Right { get; set; } = Vector3.UnitX;

        // ── 投影 ──
        public DecalProjectionType ProjectionType { get; set; } = DecalProjectionType.Directional;
        public DecalSortOrder SortOrder { get; set; } = DecalSortOrder.AfterBasePass;

        // ── 大小 ──
        public Vector3 DecalSize { get; set; } = new Vector3(128f, 128f, 128f);
        public float BoxWidth => DecalSize.X;
        public float BoxHeight => DecalSize.Y;
        public float BoxDepth => DecalSize.Z;
        public float SphereRadius => DecalSize.X * 0.5f;

        // ── 材质 ──
        public DecalMaterial Material { get; set; } = new DecalMaterial();
        public string MaterialAsset { get; set; }

        // ── 衰减 ──
        public float FadeScreenSize { get; set; } = 0f;
        public float FadeStartDelay { get; set; } = 0f;
        public float FadeDuration { get; set; } = 0f;
        public float Lifetime { get; set; } = -1f; // -1 = 永久
        private float _age;

        // ── 排序 ──
        public int SortOrderValue { get; set; }

        // ── 权重/优先级 ──
        public float Priority { get; set; } = 0f;

        // ── 更新 ──
        public void Update(float dt)
        {
            if (!IsEnabled) return;
            _age += dt;

            if (Lifetime > 0f && _age >= Lifetime)
            {
                IsEnabled = false;
                return;
            }
        }

        /// <summary>获取当前透明度 (考虑衰减)。</summary>
        public float GetCurrentOpacity()
        {
            float opacity = Material.Opacity;

            // 延迟淡入
            if (FadeStartDelay > 0f && _age < FadeStartDelay + FadeDuration)
            {
                if (_age < FadeStartDelay)
                    return 0f;

                float fadeProgress = (_age - FadeStartDelay) / FadeDuration;
                opacity *= Math.Clamp(fadeProgress, 0f, 1f);
            }

            // 生命周期结束淡出
            if (Lifetime > 0f && FadeDuration > 0f)
            {
                float remaining = Lifetime - _age;
                if (remaining < FadeDuration)
                {
                    opacity *= Math.Clamp(remaining / FadeDuration, 0f, 1f);
                }
            }

            return opacity;
        }

        /// <summary>获取投影方向矩阵。</summary>
        public Matrix4x4 GetProjectionMatrix()
        {
            Vector3 forward = Vector3.Normalize(Forward);
            Vector3 up = Vector3.Normalize(Up);
            Vector3 right = Vector3.Cross(forward, up);
            up = Vector3.Cross(right, forward);

            return Matrix4x4.CreateWorld(Position, forward, up);
        }

        /// <summary>设置朝向 (投影方向)。</summary>
        public void SetDirection(Vector3 forward)
        {
            Forward = Vector3.Normalize(forward);
            Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
            if (Right.LengthSquared() < 0.001f)
                Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitX));
            Up = Vector3.Normalize(Vector3.Cross(Right, Forward));
        }

        /// <summary>设置朝向 (从法线)。</summary>
        public void SetNormal(Vector3 normal)
        {
            SetDirection(-normal);
        }

        /// <summary>重置年龄。</summary>
        public void ResetAge() => _age = 0f;
    }

    #endregion

    #region DecalActor

    /// <summary>
    /// 贴花 Actor。自动创建 DecalComponent。
    /// 类似 UE DecalActor。
    /// </summary>
    public class DecalActor
    {
        // ── 标识 ──
        public string ActorName { get; set; } = "DecalActor";
        public Guid Id { get; } = Guid.NewGuid();
        public string ActorTag { get; set; }

        // ── 变换 ──
        public Vector3 Position
        {
            get => Decal.Position;
            set => Decal.Position = value;
        }
        public Quaternion Rotation
        {
            get => Decal.Rotation;
            set => Decal.Rotation = value;
        }

        // ── 贴花组件 ──
        public DecalComponent Decal { get; } = new DecalComponent();

        /// <summary>创建贴花 Actor 并设置材质。</summary>
        public DecalActor() { }

        public DecalActor(string materialAsset, Vector3 position, Vector3 normal)
        {
            Decal.MaterialAsset = materialAsset;
            Decal.Position = position;
            Decal.SetNormal(normal);
        }

        /// <summary>初始化 (设置投影)。</summary>
        public void Initialize()
        {
            if (Decal.Forward == -Vector3.UnitZ)
                Decal.SetDirection(-Vector3.UnitY); // 默认向下投影
        }

        /// <summary>更新。</summary>
        public void Update(float dt)
        {
            Decal.Update(dt);
        }

        /// <summary>是否仍然有效。</summary>
        public bool IsValid => Decal.IsEnabled && Decal.IsVisible;
    }

    #endregion

    #region DecalManager

    /// <summary>贴花池化管理器。</summary>
    public class DecalManager
    {
        // ── 贴花列表 ──
        private readonly List<DecalComponent> _activeDecals = new List<DecalComponent>();
        private readonly Queue<DecalComponent> _pool = new Queue<DecalComponent>();

        // ── 配置 ──
        public int MaxActiveDecals { get; set; } = 256;
        public float MaxDrawDistance { get; set; } = 500f;
        public float CullDistance { get; set; } = 300f;
        public bool EnableCulling { get; set; } = true;

        // ── 统计 ──
        public int ActiveCount => _activeDecals.Count;
        public int PoolCount => _pool.Count;
        public int CulledCount { get; private set; }

        /// <summary>生成贴花 (从池获取或新建)。</summary>
        public DecalComponent SpawnDecal(Vector3 position, Vector3 normal, Vector3 size,
            DecalMaterial material = null, float lifetime = -1f)
        {
            DecalComponent decal;
            if (_pool.Count > 0)
            {
                decal = _pool.Dequeue();
                decal.IsEnabled = true;
                decal.IsVisible = true;
                decal.ResetAge();
            }
            else
            {
                decal = new DecalComponent();
            }

            decal.Position = position;
            decal.SetNormal(normal);
            decal.DecalSize = size;
            if (material != null)
                decal.Material = material;
            decal.Lifetime = lifetime;

            _activeDecals.Add(decal);

            // 超出最大数量则移除最旧的
            while (_activeDecals.Count > MaxActiveDecals)
            {
                var oldest = _activeDecals[0];
                oldest.IsEnabled = false;
                _pool.Enqueue(oldest);
                _activeDecals.RemoveAt(0);
            }

            return decal;
        }

        /// <summary>移除贴花。</summary>
        public void RemoveDecal(DecalComponent decal)
        {
            if (_activeDecals.Remove(decal))
            {
                decal.IsEnabled = false;
                _pool.Enqueue(decal);
            }
        }

        /// <summary>更新所有贴花。</summary>
        public void Update(float dt, Vector3 cameraPosition)
        {
            CulledCount = 0;

            for (int i = _activeDecals.Count - 1; i >= 0; i--)
            {
                var decal = _activeDecals[i];
                decal.Update(dt);

                if (!decal.IsEnabled)
                {
                    _activeDecals.RemoveAt(i);
                    _pool.Enqueue(decal);
                    continue;
                }

                // 距离剔除
                if (EnableCulling)
                {
                    float dist = Vector3.Distance(decal.Position, cameraPosition);
                    if (dist > CullDistance)
                    {
                        decal.IsVisible = false;
                        CulledCount++;
                    }
                    else if (dist > MaxDrawDistance)
                    {
                        decal.IsVisible = false;
                        CulledCount++;
                    }
                    else
                    {
                        decal.IsVisible = true;
                    }
                }
            }
        }

        /// <summary>获取可见贴花列表 (用于渲染)。</summary>
        public List<DecalComponent> GetVisibleDecals()
        {
            var visible = new List<DecalComponent>();
            foreach (var decal in _activeDecals)
            {
                if (decal.IsEnabled && decal.IsVisible)
                    visible.Add(decal);
            }
            return visible;
        }

        /// <summary>按优先级排序可见贴花。</summary>
        public void SortVisibleDecals()
        {
            _activeDecals.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>清除所有贴花。</summary>
        public void ClearAll()
        {
            foreach (var decal in _activeDecals)
                _pool.Enqueue(decal);
            _activeDecals.Clear();
        }

        /// <summary>预分配池。</summary>
        public void PreallocatePool(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _pool.Enqueue(new DecalComponent { IsEnabled = false, IsVisible = false });
            }
        }
    }

    #endregion
}
