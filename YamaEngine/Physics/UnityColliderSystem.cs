using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityColliderSystem.cs
    //
    //  碰撞体系统 — 类似 Unity Collider。
    //
    //  功能：
    //    - 碰撞体类型（Box / Sphere / Capsule / Mesh / Compound / Wheel / Terrain）
    //    - ColliderComponent：中心 / 尺寸 / 半径 / 高度 / 方向
    //    - 复合碰撞体（多个子碰撞体组成一个 Body）
    //    - IsTrigger（触发碰撞体，不产生物理响应）
    //    - 碰撞体材质（弹力 / 摩擦力 / 硬度 / 混合模式）
    //    - 碰撞体跟随 Transform 缩放
    //    - 碰撞体到 Jolt 形状的自动转换
    // ============================================================================================

    /// <summary>
    ///   碰撞体类型。
    /// </summary>
    public enum ColliderType
    {
        /// <summary>盒体碰撞体。</summary>
        Box,
        /// <summary>球体碰撞体。</summary>
        Sphere,
        /// <summary>胶囊体碰撞体（沿 Y 轴）。</summary>
        Capsule,
        /// <summary>三角网格碰撞体（仅静态）。</summary>
        Mesh,
        /// <summary>复合碰撞体（包含多个子碰撞体）。</summary>
        Compound,
        /// <summary>车轮碰撞体（用于载具）。</summary>
        Wheel,
        /// <summary>地形碰撞体（高度图）。</summary>
        Terrain,
    }

    /// <summary>
    ///   碰撞体组件。描述一个碰撞体的形状、材质和触发属性。
    ///   类似 Unity 的 Collider 组件。
    /// </summary>
    public struct ColliderComponent
    {
        /// <summary>碰撞体类型。</summary>
        public ColliderType Type;

        /// <summary>碰撞体中心（相对于刚体局部空间）。</summary>
        public Vector3 Center;

        /// <summary>盒体碰撞体的半尺寸（Type=Box 时有效）。</summary>
        public Vector3 HalfExtents;

        /// <summary>球体/胶囊体的半径。</summary>
        public float Radius;

        /// <summary>胶囊体/车轮高度。</summary>
        public float Height;

        /// <summary>胶囊体方向（默认为 Y 轴）。</summary>
        public Vector3 Direction;

        /// <summary>是否为触发器（传感器，不产生物理响应）。</summary>
        public bool IsTrigger;

        /// <summary>碰撞体材质。</summary>
        public PhysicsMaterial? Material;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>碰撞层。</summary>
        public PhysicsLayer Layer;

        /// <summary>碰撞层级掩码。</summary>
        public uint LayerMask;

        /// <summary>是否跟随 Transform 缩放。</summary>
        public bool ScaleWithTransform;

        /// <summary>子碰撞体列表（Type=Compound 时有效）。</summary>
        public List<ColliderComponent>? Children;

        /// <summary>网格顶点（Type=Mesh 时有效）。</summary>
        public Vector3[]? MeshVertices;

        /// <summary>网格索引（Type=Mesh 时有效）。</summary>
        public uint[]? MeshIndices;

        /// <summary>创建默认盒体碰撞体。</summary>
        public static ColliderComponent CreateBox(Vector3 halfExtents, Vector3? center = null)
        {
            return new ColliderComponent
            {
                Type = ColliderType.Box,
                Center = center ?? Vector3.Zero,
                HalfExtents = halfExtents,
                Enabled = true,
                Layer = PhysicsLayer.Default,
                LayerMask = PhysicsLayerMask.Everything,
                Direction = Vector3.UnitY,
            };
        }

        /// <summary>创建默认球体碰撞体。</summary>
        public static ColliderComponent CreateSphere(float radius, Vector3? center = null)
        {
            return new ColliderComponent
            {
                Type = ColliderType.Sphere,
                Center = center ?? Vector3.Zero,
                Radius = radius,
                Enabled = true,
                Layer = PhysicsLayer.Default,
                LayerMask = PhysicsLayerMask.Everything,
                Direction = Vector3.UnitY,
            };
        }

        /// <summary>创建默认胶囊体碰撞体。</summary>
        public static ColliderComponent CreateCapsule(float radius, float height, Vector3? center = null)
        {
            return new ColliderComponent
            {
                Type = ColliderType.Capsule,
                Center = center ?? Vector3.Zero,
                Radius = radius,
                Height = height,
                Enabled = true,
                Layer = PhysicsLayer.Default,
                LayerMask = PhysicsLayerMask.Everything,
                Direction = Vector3.UnitY,
            };
        }
    }

    /// <summary>
    ///   碰撞体材质参数。包含弹力/摩擦力/硬度及混合模式。
    ///   可关联到 ColliderComponent 或 PhysicsMaterial 实例。
    /// </summary>
    public struct ColliderMaterial
    {
        /// <summary>弹力 (0..1)。</summary>
        public float Bounciness;

        /// <summary>动摩擦系数 (0..1)。</summary>
        public float Friction;

        /// <summary>硬度（用于破坏系统）。</summary>
        public float Hardness;

        /// <summary>弹力混合模式。</summary>
        public PhysicsMaterialCombineMode BounceCombine;

        /// <summary>摩擦混合模式。</summary>
        public PhysicsMaterialCombineMode FrictionCombine;

        /// <summary>默认碰撞体材质。</summary>
        public static ColliderMaterial Default => new()
        {
            Bounciness = 0.2f,
            Friction = 0.6f,
            Hardness = 1f,
            BounceCombine = PhysicsMaterialCombineMode.Average,
            FrictionCombine = PhysicsMaterialCombineMode.Average,
        };
    }

    /// <summary>
    ///   碰撞体系统。管理碰撞体的创建、转换、和形状缓存。
    ///   负责将 ColliderComponent 转换为 Jolt ShapeHandle。
    /// </summary>
    public sealed class ColliderSystem
    {
        private readonly PhysicsWorld _world;
        private readonly Dictionary<ulong, ShapeHandle> _shapeCache = new();

        /// <summary>形状缓存大小。</summary>
        public int CachedShapeCount => _shapeCache.Count;

        internal ColliderSystem(PhysicsWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>
        ///   将 ColliderComponent 转换为 Jolt ShapeHandle。
        ///   结果缓存在内部字典中，相同的碰撞体配置复用同一形状。
        /// </summary>
        public ShapeHandle ToShape(in ColliderComponent collider)
        {
            // 缓存键
            ulong cacheKey = ComputeCacheKey(collider);
            if (_shapeCache.TryGetValue(cacheKey, out var cached))
                return cached;

            ShapeHandle shape = collider.Type switch
            {
                ColliderType.Box => CreateBoxShape(collider),
                ColliderType.Sphere => CreateSphereShape(collider),
                ColliderType.Capsule => CreateCapsuleShape(collider),
                ColliderType.Mesh => CreateMeshShape(collider),
                ColliderType.Wheel => CreateWheelShape(collider),
                _ => throw new NotSupportedException($"Collider type {collider.Type} not supported."),
            };

            _shapeCache[cacheKey] = shape;
            return shape;
        }

        /// <summary>
        ///   将复合碰撞体的多个子组件转换为单个复合 Jolt Shape。
        /// </summary>
        public ShapeHandle ToCompoundShape(IReadOnlyList<ColliderComponent> colliders)
        {
            int count = colliders.Count;
            var shapes = new ShapeHandle[count];
            var positions = new Vector3[count];
            var rotations = new Quaternion[count];

            for (int i = 0; i < count; i++)
            {
                var c = colliders[i];
                shapes[i] = ToShape(c);
                positions[i] = c.Center;
                rotations[i] = Quaternion.Identity;
            }

            return _world.CreateCompoundShape(shapes, positions, rotations);
        }

        /// <summary>
        ///   从 ColliderComponent 创建刚体（便捷方法）。
        /// </summary>
        public uint CreateBody(int entityId, Vector3 position, Quaternion rotation,
            float mass, in ColliderComponent collider)
        {
            var shape = collider.Type == ColliderType.Compound && collider.Children != null
                ? ToCompoundShape(collider.Children)
                : ToShape(collider);

            ushort layer = (ushort)(1 << (int)collider.Layer);
            ushort mask = (ushort)(collider.LayerMask & 0xFFFF);

            return _world.CreateBody(new BodyDescriptor
            {
                EntityId = entityId,
                Position = position + collider.Center,
                Rotation = rotation,
                Mass = mass,
                ObjectLayer = layer,
                CollisionMask = mask != 0 ? mask : PhysicsLayers.All,
                IsSensor = collider.IsTrigger,
                PreCreatedShape = shape,
                Restitution = collider.Material?.Restitution ?? 0.2f,
                Friction = collider.Material?.Friction ?? 0.6f,
            });
        }

        /// <summary>
        ///   清空形状缓存。当碰撞体参数批量修改后调用。
        /// </summary>
        public void ClearCache()
        {
            foreach (var kvp in _shapeCache)
            {
                if (!kvp.Value.IsNull)
                    JoltNative.SJ_Shape_Destroy(kvp.Value);
            }
            _shapeCache.Clear();
        }

        // ========================================================================================
        //  形状创建
        // ========================================================================================

        private ShapeHandle CreateBoxShape(in ColliderComponent c)
        {
            return _world.CreateBoxShape(c.HalfExtents);
        }

        private ShapeHandle CreateSphereShape(in ColliderComponent c)
        {
            return _world.CreateSphereShape(c.Radius);
        }

        private ShapeHandle CreateCapsuleShape(in ColliderComponent c)
        {
            return _world.CreateCapsuleShape(c.Radius, c.Height);
        }

        private ShapeHandle CreateMeshShape(in ColliderComponent c)
        {
            if (c.MeshVertices == null || c.MeshIndices == null)
                throw new InvalidOperationException("Mesh collider requires vertices and indices.");
            return _world.CreateMeshShape(c.MeshVertices.AsSpan(), c.MeshIndices.AsSpan());
        }

        private ShapeHandle CreateWheelShape(in ColliderComponent c)
        {
            // 车轮：使用圆柱形状，沿 Z 轴方向（车轮旋转轴）
            // 圆柱体简化替代
            return _world.CreateCylinderShape(c.Radius, c.Height);
        }

        /// <summary>
        ///   生成碰撞体配色的哈希缓存键。
        ///   在形状复用中很重要——相同的碰撞体参数共享同一原生形状。
        /// </summary>
        private static ulong ComputeCacheKey(in ColliderComponent c)
        {
            HashCode hc = new();
            hc.Add((int)c.Type);
            hc.Add(c.Center);
            hc.Add(c.HalfExtents);
            hc.Add(c.Radius);
            hc.Add(c.Height);
            hc.Add(c.Direction);
            hc.Add(c.IsTrigger);

            // 对于 Mesh 类型，加入顶点数和索引数作为缓存键的一部分
            if (c.Type == ColliderType.Mesh && c.MeshVertices != null && c.MeshIndices != null)
            {
                hc.Add(c.MeshVertices.Length);
                hc.Add(c.MeshIndices.Length);
            }

            return (ulong)(uint)hc.ToHashCode();
        }
    }
}
