using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  ShapeDatabase.cs
    //
    //  碰撞形状数据库。集中管理所有形状的创建/缓存/查询。
    //  功能:
    //    1) 注册和获取形状(用ID)
    //    2) 形状缓存(相同参数的形状不重复创建)
    //    3) 工厂方法(各种预设形状)
    //    4) 形状查询(体积/惯性/AABB/支持点)
    //    5) 形状变换(缩放/旋转/平移)
    //
    //  注意: 形状是不可变的(Immutable)，创建后不能改。这样能安全共享。
    //  TODO: 复杂凸包的体积计算还是AABB近似，应该用散度定理精确算。
    // ============================================================================================

    /// <summary>形状标识符。</summary>
    public readonly struct ShapeId : IEquatable<ShapeId>
    {
        public readonly int Value;
        public bool IsValid => Value > 0;
        public static ShapeId Invalid => new(0);

        public ShapeId(int value) { Value = value; }

        public bool Equals(ShapeId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is ShapeId id && Equals(id);
        public override int GetHashCode() => Value;
        public static bool operator ==(ShapeId a, ShapeId b) => a.Value == b.Value;
        public static bool operator !=(ShapeId a, ShapeId b) => a.Value != b.Value;
        public override string ToString() => $"Shape#{Value}";
    }

    /// <summary>
    ///   形状数据库。管理所有碰撞形状。
    /// </summary>
    public sealed class ShapeDatabase
    {
        private readonly Dictionary<ShapeId, ICollisionShape> _shapes = new();
        private readonly Dictionary<string, ShapeId> _namedShapes = new();
        private readonly Dictionary<(CollisionShapeType, string), ShapeId> _cache = new();
        private int _nextId = 1;
        private float _totalMemoryUsage;

        public int Count => _shapes.Count;
        public int NamedCount => _namedShapes.Count;
        public int CacheCount => _cache.Count;
        public float TotalMemoryUsageMB => _totalMemoryUsage / (1024f * 1024f);

        // ========================================================================================
        //  注册
        // ========================================================================================

        /// <summary>注册一个形状。</summary>
        public ShapeId Register(ICollisionShape shape, string? name = null)
        {
            if (shape == null) return ShapeId.Invalid;

            // 缓存检查
            string cacheKey = GenerateCacheKey(shape);
            if (_cache.TryGetValue((shape.Type, cacheKey), out var cachedId))
                return cachedId;

            var id = new ShapeId(_nextId++);
            _shapes[id] = shape;
            if (name != null)
                _namedShapes[name] = id;
            _cache[(shape.Type, cacheKey)] = id;
            _totalMemoryUsage += EstimateMemoryUsage(shape);
            return id;
        }

        /// <summary>用名称获取形状。</summary>
        public ShapeId GetByName(string name)
        {
            return _namedShapes.TryGetValue(name, out var id) ? id : ShapeId.Invalid;
        }

        /// <summary>用ID获取形状。</summary>
        public ICollisionShape? Get(ShapeId id)
        {
            return id.IsValid && _shapes.TryGetValue(id, out var shape) ? shape : null;
        }

        /// <summary>用ID获取形状(强类型)。</summary>
        public T? Get<T>(ShapeId id) where T : class, ICollisionShape
        {
            return Get(id) as T;
        }

        /// <summary>用ID获取形状(强类型，int版)。</summary>
        public T? Get<T>(int id) where T : class, ICollisionShape
        {
            return Get(new ShapeId(id)) as T;
        }

        /// <summary>用ID获取形状（窄相位用，返回Shape基类）。</summary>
        public Shape? GetShape(int id)
        {
            return Get(new ShapeId(id)) as Shape;
        }

        /// <summary>用ID获取形状（窄相位用，返回Shape基类）。</summary>
        public Shape? GetShape(ShapeId id)
        {
            return Get(id) as Shape;
        }

        /// <summary>移除形状。</summary>
        public bool Unregister(ShapeId id)
        {
            if (!_shapes.TryGetValue(id, out var shape)) return false;
            _shapes.Remove(id);
            _totalMemoryUsage -= EstimateMemoryUsage(shape);
            // 移除名称映射
            foreach (var kvp in _namedShapes)
            {
                if (kvp.Value == id)
                {
                    _namedShapes.Remove(kvp.Key);
                    break;
                }
            }
            // 移除缓存
            string cacheKey = GenerateCacheKey(shape);
            _cache.Remove((shape.Type, cacheKey));
            return true;
        }

        /// <summary>清空所有形状。</summary>
        public void Clear()
        {
            _shapes.Clear();
            _namedShapes.Clear();
            _cache.Clear();
            _nextId = 1;
            _totalMemoryUsage = 0;
        }

        // ========================================================================================
        //  工厂方法
        // ========================================================================================

        /// <summary>创建球。</summary>
        public ShapeId CreateSphere(float radius, string? name = null)
        {
            return Register(new SphereShape(radius), name);
        }

        /// <summary>创建盒子。</summary>
        public ShapeId CreateBox(Vector3 halfExtents, string? name = null)
        {
            return Register(new BoxShape(halfExtents), name);
        }

        /// <summary>创建盒子。</summary>
        public ShapeId CreateBox(float halfX, float halfY, float halfZ, string? name = null)
        {
            return Register(new BoxShape(halfX, halfY, halfZ), name);
        }

        /// <summary>创建胶囊。</summary>
        public ShapeId CreateCapsule(float radius, float height, string? name = null)
        {
            return Register(new CapsuleShape(radius, height), name);
        }

        /// <summary>创建平面。</summary>
        public ShapeId CreatePlane(Vector3 normal, string? name = null)
        {
            return Register(new PlaneShape(normal), name);
        }

        /// <summary>创建凸包。</summary>
        public ShapeId CreateConvexHull(Vector3[] vertices, string? name = null)
        {
            return Register(new ConvexHullShape(vertices), name);
        }

        /// <summary>创建网格。</summary>
        public ShapeId CreateMesh(Vector3[] vertices, int[] indices, string? name = null)
        {
            return Register(new MeshShape(vertices, indices), name);
        }

        /// <summary>创建高度场。</summary>
        public ShapeId CreateHeightfield(float[,] heights, float cellSize, Vector3 origin, string? name = null)
        {
            return Register(new HeightfieldShape(heights, cellSize, origin), name);
        }

        /// <summary>创建复合形状。</summary>
        public ShapeId CreateCompound(Action<CompoundBuilder> configure, string? name = null)
        {
            var builder = new CompoundBuilder();
            configure(builder);
            return Register(builder.Build(), name);
        }

        // ========================================================================================
        //  预设形状
        // ========================================================================================

        /// <summary>预设: 人类角色胶囊。</summary>
        public ShapeId CreateHumanoidCapsule(float height = 1.8f, string? name = null)
        {
            return CreateCapsule(0.35f, height - 0.7f, name);
        }

        /// <summary>预设: 球形弹丸。</summary>
        public ShapeId CreateProjectile(float radius = 0.05f, string? name = null)
        {
            return CreateSphere(radius, name);
        }

        /// <summary>预设: 车辆底盘。</summary>
        public ShapeId CreateVehicleChassis(float length = 4f, float width = 2f, float height = 1.5f,
            string? name = null)
        {
            return CreateBox(length * 0.5f, height * 0.5f, width * 0.5f, name);
        }

        /// <summary>预设: 标准立方体(边长1)。</summary>
        public ShapeId CreateUnitCube(string? name = null)
        {
            return CreateBox(0.5f, 0.5f, 0.5f, name);
        }

        /// <summary>预设: 地面平面。</summary>
        public ShapeId CreateGroundPlane(string? name = null)
        {
            return CreatePlane(Vector3.UnitY, name);
        }

        /// <summary>预设: 楼梯形状(复合)。</summary>
        public ShapeId CreateStairs(int stepCount, float stepHeight, float stepWidth, float stepDepth,
            string? name = null)
        {
            return CreateCompound(builder =>
            {
                for (int i = 0; i < stepCount; i++)
                {
                    var stepBox = new BoxShape(new Vector3(stepDepth * 0.5f, stepHeight * 0.5f, stepWidth * 0.5f));
                    builder.AddChild(stepBox,
                        new Vector3(i * stepDepth, i * stepHeight + stepHeight * 0.5f, 0));
                }
            }, name);
        }

        /// <summary>预设: 圆柱体(用多边形近似)。</summary>
        public ShapeId CreateCylinder(float radius, float height, int segments = 16, string? name = null)
        {
            var vertices = new Vector3[segments * 2];
            float halfH = height * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * MathF.PI * 2f;
                float x = MathF.Cos(angle) * radius;
                float z = MathF.Sin(angle) * radius;
                vertices[i] = new Vector3(x, -halfH, z);
                vertices[i + segments] = new Vector3(x, halfH, z);
            }
            return CreateConvexHull(vertices, name);
        }

        /// <summary>预设: 圆锥(用凸包近似)。</summary>
        public ShapeId CreateCone(float radius, float height, int segments = 16, string? name = null)
        {
            var vertices = new Vector3[segments + 2];
            float halfH = height * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * MathF.PI * 2f;
                vertices[i] = new Vector3(MathF.Cos(angle) * radius, -halfH, MathF.Sin(angle) * radius);
            }
            vertices[segments] = new Vector3(0, halfH, 0);  // 顶点
            vertices[segments + 1] = new Vector3(0, -halfH, 0);  // 底中心
            return CreateConvexHull(vertices, name);
        }

        // ========================================================================================
        //  查询
        // ========================================================================================

        /// <summary>获取形状的AABB。</summary>
        public bool GetAABB(ShapeId id, Vector3 position, Quaternion rotation,
            out Vector3 min, out Vector3 max)
        {
            min = Vector3.Zero; max = Vector3.Zero;
            var shape = Get(id);
            if (shape == null) return false;
            shape.GetAABB(position, rotation, out min, out max);
            return true;
        }

        /// <summary>获取形状的局部AABB（单位原点+单位旋转，未做世界变换）。</summary>
        public AABB GetLocalAABB(ShapeId id)
        {
            var shape = Get(id);
            if (shape == null) return new AABB(Vector3.Zero, Vector3.Zero);
            shape.GetAABB(Vector3.Zero, Quaternion.Identity, out var min, out var max);
            return new AABB(min, max);
        }

        /// <summary>获取形状的局部AABB（int id 重载）。</summary>
        public AABB GetLocalAABB(int id) => GetLocalAABB(new ShapeId(id));

        /// <summary>获取形状的体积。</summary>
        public float GetVolume(ShapeId id)
        {
            var shape = Get(id);
            return shape?.Volume ?? 0f;
        }

        /// <summary>获取形状的支持点。</summary>
        public Vector3 GetSupportPoint(ShapeId id, Vector3 direction, Vector3 position, Quaternion rotation)
        {
            var shape = Get(id);
            if (shape == null) return position;
            return shape.GetSupportPoint(direction, position, rotation);
        }

        /// <summary>射线检测。</summary>
        public bool Raycast(ShapeId id, Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal)
        {
            t = 0; normal = Vector3.Zero;
            var shape = Get(id);
            if (shape == null) return false;
            return shape.Raycast(origin, direction, maxDistance, position, rotation, out t, out normal);
        }

        /// <summary>形状射线检测，额外输出命中点(origin + dir * t)。</summary>
        public bool Raycast(ShapeId id, Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 position, Quaternion rotation, out float t, out Vector3 normal, out Vector3 point)
        {
            t = 0; normal = Vector3.Zero; point = Vector3.Zero;
            if (!Raycast(id, origin, direction, maxDistance, position, rotation, out t, out normal))
                return false;
            point = origin + direction * t;
            return true;
        }

        /// <summary>计算形状的惯性张量(相对局部中心)。</summary>
        public Matrix4x4 ComputeInertiaTensor(ShapeId id, float mass)
        {
            var shape = Get(id);
            if (shape == null) return Matrix4x4.Identity;
            return ComputeInertia(shape, mass);
        }

        /// <summary>静态方法: 计算任意形状的惯性张量。</summary>
        public static Matrix4x4 ComputeInertia(ICollisionShape shape, float mass)
        {
            if (mass <= 0) return Matrix4x4.Identity;

            return shape.Type switch
            {
                CollisionShapeType.Sphere => ComputeSphereInertia(((SphereShape)shape).Radius, mass),
                CollisionShapeType.Box => ComputeBoxInertia(((BoxShape)shape).HalfExtents, mass),
                CollisionShapeType.Capsule => ComputeCapsuleInertia(
                    ((CapsuleShape)shape).Radius, ((CapsuleShape)shape).HalfHeight, mass),
                CollisionShapeType.ConvexHull => ComputeConvexHullInertia((ConvexHullShape)shape, mass),
                CollisionShapeType.Mesh => ComputeMeshInertia((MeshShape)shape, mass),
                CollisionShapeType.Compound => ComputeCompoundInertia((CompoundShape)shape, mass),
                CollisionShapeType.Heightfield => Matrix4x4.Identity, // 静态
                _ => Matrix4x4.Identity,
            };
        }

        /// <summary>静态方法: 计算任意形状的体积。</summary>
        public static float ComputeVolume(ICollisionShape shape)
        {
            return shape?.Volume ?? 0f;
        }

        // ========================================================================================
        //  惯性张量计算
        // ========================================================================================

        /// <summary>球体惯性张量。</summary>
        public static Matrix4x4 ComputeSphereInertia(float radius, float mass)
        {
            float i = (2f / 5f) * mass * radius * radius;
            return new Matrix4x4(i, 0, 0, 0, 0, i, 0, 0, 0, 0, i, 0, 0, 0, 0, 1);
        }

        /// <summary>盒子惯性张量。</summary>
        public static Matrix4x4 ComputeBoxInertia(Vector3 halfExtents, float mass)
        {
            float w = halfExtents.X * 2, h = halfExtents.Y * 2, d = halfExtents.Z * 2;
            float ixx = mass * (h * h + d * d) / 12f;
            float iyy = mass * (w * w + d * d) / 12f;
            float izz = mass * (w * w + h * h) / 12f;
            return new Matrix4x4(ixx, 0, 0, 0, 0, iyy, 0, 0, 0, 0, izz, 0, 0, 0, 0, 1);
        }

        /// <summary>胶囊惯性张量。</summary>
        public static Matrix4x4 ComputeCapsuleInertia(float radius, float halfHeight, float mass)
        {
            // 圆柱部分 + 两个半球
            float cylMass = mass * 0.7f;  // 估算
            float sphMass = mass * 0.3f;

            float r2 = radius * radius;
            float h2 = (halfHeight * 2) * (halfHeight * 2);

            // 圆柱
            float cylIxx = cylMass * (3f * r2 + h2) / 12f;
            float cylIyy = 0.5f * cylMass * r2;
            float cylIzz = cylIxx;

            // 两个半球(简化为球)
            float sphI = 0.4f * sphMass * r2;

            float ixx = cylIxx + sphI;
            float iyy = cylIyy + sphI * 0.5f;
            float izz = cylIzz + sphI;

            return new Matrix4x4(ixx, 0, 0, 0, 0, iyy, 0, 0, 0, 0, izz, 0, 0, 0, 0, 1);
        }

        /// <summary>凸包惯性张量(用采样近似)。</summary>
        public static Matrix4x4 ComputeConvexHullInertia(ConvexHullShape hull, float mass)
        {
            // 用顶点采样近似(简化)
            int n = hull.Vertices.Length;
            if (n == 0) return Matrix4x4.Identity;

            float ixx = 0, iyy = 0, izz = 0;
            float ixy = 0, ixz = 0, iyz = 0;

            foreach (var v in hull.Vertices)
            {
                ixx += v.Y * v.Y + v.Z * v.Z;
                iyy += v.X * v.X + v.Z * v.Z;
                izz += v.X * v.X + v.Y * v.Y;
                ixy -= v.X * v.Y;
                ixz -= v.X * v.Z;
                iyz -= v.Y * v.Z;
            }

            float factor = mass / n;
            return new Matrix4x4(
                ixx * factor, ixy * factor, ixz * factor, 0,
                ixy * factor, iyy * factor, iyz * factor, 0,
                ixz * factor, iyz * factor, izz * factor, 0,
                0, 0, 0, 1);
        }

        /// <summary>网格惯性张量(用凸包近似)。</summary>
        public static Matrix4x4 ComputeMeshInertia(MeshShape mesh, float mass)
        {
            // 静态网格通常不需要惯性
            return Matrix4x4.Identity;
        }

        /// <summary>复合形状惯性张量。</summary>
        public static Matrix4x4 ComputeCompoundInertia(CompoundShape compound, float mass)
        {
            float totalVol = compound.Volume;
            if (totalVol <= 0) return Matrix4x4.Identity;

            Matrix4x4 total = Matrix4x4.Identity;
            foreach (var c in compound.Children)
            {
                float childMass = mass * (c.Shape.Volume / totalVol);
                var childInertia = ComputeInertia(c.Shape, childMass);

                // 平行轴定理: I_total = I_local + m * (d²I - d⊗d)
                Vector3 d = c.Offset + c.Shape.LocalCenter;
                float d2 = d.LengthSquared();
                Matrix4x4 shift = new(
                    d2 - d.X * d.X, -d.X * d.Y, -d.X * d.Z, 0,
                    -d.Y * d.X, d2 - d.Y * d.Y, -d.Y * d.Z, 0,
                    -d.Z * d.X, -d.Z * d.Y, d2 - d.Z * d.Z, 0,
                    0, 0, 0, 1);
                shift *= childMass;

                // 旋转子惯性张量
                Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(c.Rotation);
                Matrix4x4 rotTrans = Matrix4x4.Transpose(rotMat);
                childInertia = rotMat * childInertia * rotTrans;

                total += childInertia + shift;
            }
            return total;
        }

        // ========================================================================================
        //  形状变换
        // ========================================================================================

        /// <summary>缩放球。</summary>
        public ShapeId ScaleSphere(ShapeId source, float scale, string? name = null)
        {
            var sphere = Get<SphereShape>(source);
            if (sphere == null) return ShapeId.Invalid;
            return CreateSphere(sphere.Radius * scale, name);
        }

        /// <summary>缩放盒子。</summary>
        public ShapeId ScaleBox(ShapeId source, Vector3 scale, string? name = null)
        {
            var box = Get<BoxShape>(source);
            if (box == null) return ShapeId.Invalid;
            return CreateBox(box.HalfExtents * scale, name);
        }

        /// <summary>缩放凸包。</summary>
        public ShapeId ScaleConvexHull(ShapeId source, Vector3 scale, string? name = null)
        {
            var hull = Get<ConvexHullShape>(source);
            if (hull == null) return ShapeId.Invalid;
            var newVerts = new Vector3[hull.Vertices.Length];
            for (int i = 0; i < hull.Vertices.Length; i++)
                newVerts[i] = hull.Vertices[i] * scale;
            return CreateConvexHull(newVerts, name);
        }

        // ========================================================================================
        //  凸包生成
        // ========================================================================================

        /// <summary>从点集生成凸包(QuickHull简化版)。</summary>
        public ShapeId CreateConvexHullFromPoints(Vector3[] points, string? name = null)
        {
            // 简化: 去重 + 取极值点
            var unique = new List<Vector3>(points.Length);
            var seen = new HashSet<Vector3>();
            foreach (var p in points)
            {
                if (seen.Add(p))
                    unique.Add(p);
            }

            if (unique.Count < 4)
            {
                // 点太少，退化
                return CreateSphere(0.1f, name);
            }

            // 真正的QuickHull算法太长，这里用简化版: 取AABB的8个角点 + 极值点
            var result = new List<Vector3>(16);
            Vector3 min = unique[0], max = unique[0];
            foreach (var p in unique)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            // 8个角点
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    for (int z = 0; z < 2; z++)
                        result.Add(new Vector3(
                            x == 0 ? min.X : max.X,
                            y == 0 ? min.Y : max.Y,
                            z == 0 ? min.Z : max.Z));

            // 加上极值点
            foreach (var p in unique)
            {
                bool isExtreme = false;
                foreach (var r in result)
                {
                    if (Vector3.DistanceSquared(p, r) < 0.001f) { isExtreme = false; break; }
                }
                // 简化: 都加上
                if (!result.Contains(p))
                    result.Add(p);
            }

            return CreateConvexHull(result.ToArray(), name);
        }

        // ========================================================================================
        //  统计和调试
        // ========================================================================================

        public void GetStats(out int totalCount, out int namedCount, out int cacheCount,
            out float memoryMB, out Dictionary<CollisionShapeType, int> typeCounts)
        {
            totalCount = _shapes.Count;
            namedCount = _namedShapes.Count;
            cacheCount = _cache.Count;
            memoryMB = _totalMemoryUsage / (1024f * 1024f);
            typeCounts = new Dictionary<CollisionShapeType, int>();
            foreach (var s in _shapes.Values)
            {
                if (!typeCounts.ContainsKey(s.Type))
                    typeCounts[s.Type] = 0;
                typeCounts[s.Type]++;
            }
        }

        /// <summary>枚举所有形状。</summary>
        public IEnumerable<(ShapeId id, ICollisionShape shape, string? name)> EnumerateShapes()
        {
            foreach (var kvp in _shapes)
            {
                string? name = null;
                foreach (var nkvp in _namedShapes)
                {
                    if (nkvp.Value == kvp.Key)
                    {
                        name = nkvp.Key;
                        break;
                    }
                }
                yield return (kvp.Key, kvp.Value, name);
            }
        }

        /// <summary>导出形状信息(用于序列化)。</summary>
        public List<ShapeExportData> ExportAll()
        {
            var result = new List<ShapeExportData>(_shapes.Count);
            foreach (var kvp in _shapes)
            {
                string? name = null;
                foreach (var nkvp in _namedShapes)
                {
                    if (nkvp.Value == kvp.Key) { name = nkvp.Key; break; }
                }
                result.Add(new ShapeExportData
                {
                    Id = kvp.Key.Value,
                    Name = name,
                    Type = kvp.Value.Type,
                    Volume = kvp.Value.Volume,
                });
            }
            return result;
        }

        // ========================================================================================
        //  工具
        // ========================================================================================

        private static string GenerateCacheKey(ICollisionShape shape)
        {
            return shape.Type switch
            {
                CollisionShapeType.Sphere => $"s{((SphereShape)shape).Radius}",
                CollisionShapeType.Box => $"b{((BoxShape)shape).HalfExtents}",
                CollisionShapeType.Capsule => $"c{((CapsuleShape)shape).Radius}_{((CapsuleShape)shape).HalfHeight}",
                CollisionShapeType.Plane => $"p{((PlaneShape)shape).Normal}",
                CollisionShapeType.ConvexHull => $"h{((ConvexHullShape)shape).Vertices.Length}_{((ConvexHullShape)shape).Vertices.GetHashCode()}",
                CollisionShapeType.Mesh => $"m{((MeshShape)shape).Vertices.Length}_{((MeshShape)shape).Indices.Length}",
                CollisionShapeType.Heightfield => $"hf{((HeightfieldShape)shape).Heights.GetLength(0)}_{((HeightfieldShape)shape).Heights.GetLength(1)}",
                CollisionShapeType.Compound => $"co{((CompoundShape)shape).Children.Count}_{Guid.NewGuid()}",
                _ => Guid.NewGuid().ToString(),
            };
        }

        private static float EstimateMemoryUsage(ICollisionShape shape)
        {
            return shape.Type switch
            {
                CollisionShapeType.Sphere => 64,
                CollisionShapeType.Box => 80,
                CollisionShapeType.Capsule => 80,
                CollisionShapeType.Plane => 64,
                CollisionShapeType.ConvexHull => ((ConvexHullShape)shape).Vertices.Length * 16 + 64,
                CollisionShapeType.Mesh => ((MeshShape)shape).Vertices.Length * 16 +
                                           ((MeshShape)shape).Indices.Length * 4 + 64,
                CollisionShapeType.Heightfield =>
                    ((HeightfieldShape)shape).Heights.GetLength(0) *
                    ((HeightfieldShape)shape).Heights.GetLength(1) * 4 + 64,
                CollisionShapeType.Compound => ((CompoundShape)shape).Children.Count * 64 + 64,
                _ => 64,
            };
        }
    }

    /// <summary>复合形状构建器。</summary>
    public sealed class CompoundBuilder
    {
        private readonly List<CompoundShape.ChildShape> _children = new();

        public CompoundBuilder AddChild(ICollisionShape shape, Vector3 offset, Quaternion? rotation = null)
        {
            _children.Add(new CompoundShape.ChildShape
            {
                Shape = shape,
                Offset = offset,
                Rotation = rotation ?? Quaternion.Identity,
            });
            return this;
        }

        public CompoundBuilder AddSphere(float radius, Vector3 offset, Quaternion? rotation = null)
        {
            return AddChild(new SphereShape(radius), offset, rotation);
        }

        public CompoundBuilder AddBox(Vector3 halfExtents, Vector3 offset, Quaternion? rotation = null)
        {
            return AddChild(new BoxShape(halfExtents), offset, rotation);
        }

        public CompoundBuilder AddCapsule(float radius, float height, Vector3 offset, Quaternion? rotation = null)
        {
            return AddChild(new CapsuleShape(radius, height), offset, rotation);
        }

        public CompoundShape Build()
        {
            var compound = new CompoundShape();
            foreach (var c in _children)
                compound.AddChild(c.Shape, c.Offset, c.Rotation);
            return compound;
        }
    }

    /// <summary>形状导出数据。</summary>
    [Serializable]
    public struct ShapeExportData
    {
        public int Id;
        public string? Name;
        public CollisionShapeType Type;
        public float Volume;
    }

    // ============================================================================================
    //  形状查询辅助
    // ============================================================================================

    /// <summary>形状查询辅助类。</summary>
    public static class ShapeQuery
    {
        /// <summary>检查两个AABB是否重叠。</summary>
        public static bool AABBOverlap(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
        {
            return minA.X <= maxB.X && maxA.X >= minB.X &&
                   minA.Y <= maxB.Y && maxA.Y >= minB.Y &&
                   minA.Z <= maxB.Z && maxA.Z >= minB.Z;
        }

        /// <summary>计算两个AABB的交集。</summary>
        public static bool AABBIntersect(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB,
            out Vector3 outMin, out Vector3 outMax)
        {
            outMin = Vector3.Max(minA, minB);
            outMax = Vector3.Min(maxA, maxB);
            return outMin.X <= outMax.X && outMin.Y <= outMax.Y && outMin.Z <= outMax.Z;
        }

        /// <summary>合并两个AABB。</summary>
        public static void AABBUnion(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB,
            out Vector3 outMin, out Vector3 outMax)
        {
            outMin = Vector3.Min(minA, minB);
            outMax = Vector3.Max(maxA, maxB);
        }

        /// <summary>计算AABB的表面积。</summary>
        public static float AABBSurfaceArea(Vector3 min, Vector3 max)
        {
            Vector3 d = max - min;
            return 2f * (d.X * d.Y + d.Y * d.Z + d.X * d.Z);
        }

        /// <summary>计算AABB的体积。</summary>
        public static float AABBVolume(Vector3 min, Vector3 max)
        {
            Vector3 d = max - min;
            return d.X * d.Y * d.Z;
        }

        /// <summary>计算AABB中心。</summary>
        public static Vector3 AABBCenter(Vector3 min, Vector3 max)
        {
            return (min + max) * 0.5f;
        }

        /// <summary>点是否在AABB内。</summary>
        public static bool PointInAABB(Vector3 point, Vector3 min, Vector3 max)
        {
            return point.X >= min.X && point.X <= max.X &&
                   point.Y >= min.Y && point.Y <= max.Y &&
                   point.Z >= min.Z && point.Z <= max.Z;
        }

        /// <summary>点到AABB最近点。</summary>
        public static Vector3 ClosestPointOnAABB(Vector3 point, Vector3 min, Vector3 max)
        {
            return new Vector3(
                Math.Clamp(point.X, min.X, max.X),
                Math.Clamp(point.Y, min.Y, max.Y),
                Math.Clamp(point.Z, min.Z, max.Z));
        }

        /// <summary>点到AABB距离的平方。</summary>
        public static float DistanceSquaredToAABB(Vector3 point, Vector3 min, Vector3 max)
        {
            Vector3 closest = ClosestPointOnAABB(point, min, max);
            return Vector3.DistanceSquared(point, closest);
        }

        /// <summary>射线-AABB相交检测(slab method)。</summary>
        public static bool RayAABB(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max,
            out float tmin, out float tmax)
        {
            tmin = 0f;
            tmax = float.MaxValue;

            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? origin.X : i == 1 ? origin.Y : origin.Z;
                float d = i == 0 ? direction.X : i == 1 ? direction.Y : direction.Z;
                float mn = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
                float mx = i == 0 ? max.X : i == 1 ? max.Y : max.Z;

                if (MathF.Abs(d) < 1e-8f)
                {
                    if (o < mn || o > mx) return false;
                    continue;
                }
                float t1 = (mn - o) / d;
                float t2 = (mx - o) / d;
                if (t1 > t2) (t1, t2) = (t2, t1);
                if (t1 > tmin) tmin = t1;
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) return false;
            }
            return true;
        }

        /// <summary>球-AABB相交检测。</summary>
        public static bool SphereAABB(Vector3 center, float radius, Vector3 min, Vector3 max)
        {
            return DistanceSquaredToAABB(center, min, max) <= radius * radius;
        }

        /// <summary>线段-AABB相交检测。</summary>
        public static bool SegmentAABB(Vector3 p0, Vector3 p1, Vector3 min, Vector3 max)
        {
            Vector3 d = p1 - p0;
            float length = d.Length();
            if (length < 1e-6f)
                return PointInAABB(p0, min, max);
            Vector3 dir = d / length;
            return RayAABB(p0, dir, min, max, out float tmin, out float tmax) &&
                   tmin <= length;
        }
    }

    // ============================================================================================
    //  形状容器(给RigidBody用)
    // ============================================================================================

    /// <summary>
    ///   形状实例。一个形状+一个变换(相对刚体的偏移)。
    /// </summary>
    public sealed class ShapeInstance
    {
        public ShapeId ShapeId;
        public Vector3 LocalOffset;
        public Quaternion LocalRotation = Quaternion.Identity;
        public float Density = 1f;
        public int CollisionGroup = 1;
        public int CollisionMask = ~0;
        public bool IsTrigger;

        public ShapeInstance(ShapeId shapeId, Vector3? offset = null, Quaternion? rotation = null)
        {
            ShapeId = shapeId;
            LocalOffset = offset ?? Vector3.Zero;
            LocalRotation = rotation ?? Quaternion.Identity;
        }

        /// <summary>获取世界空间AABB。</summary>
        public void GetWorldAABB(ShapeDatabase db, Vector3 bodyPos, Quaternion bodyRot,
            out Vector3 min, out Vector3 max)
        {
            var shape = db.Get(ShapeId);
            if (shape == null)
            {
                min = max = bodyPos;
                return;
            }
            Vector3 worldPos = bodyPos + Vector3.Transform(LocalOffset, bodyRot);
            Quaternion worldRot = LocalRotation * bodyRot;
            shape.GetAABB(worldPos, worldRot, out min, out max);
        }
    }

    /// <summary>
    ///   形状容器。一个刚体可以有多个形状实例。
    /// </summary>
    public sealed class ShapeContainer
    {
        private readonly List<ShapeInstance> _instances = new();
        private readonly ShapeDatabase _db;
        private Vector3 _cachedCenterOfMass;
        private float _cachedMass;
        private Matrix4x4 _cachedInertia;
        private bool _dirty = true;

        public IReadOnlyList<ShapeInstance> Instances => _instances;
        public int Count => _instances.Count;
        public ShapeDatabase Database => _db;

        public ShapeContainer(ShapeDatabase db)
        {
            _db = db;
        }

        public void Add(ShapeInstance instance)
        {
            _instances.Add(instance);
            _dirty = true;
        }

        public void Add(ShapeId shapeId, Vector3? offset = null, Quaternion? rotation = null,
            float density = 1f)
        {
            _instances.Add(new ShapeInstance(shapeId, offset, rotation) { Density = density });
            _dirty = true;
        }

        public bool Remove(ShapeInstance instance)
        {
            bool removed = _instances.Remove(instance);
            if (removed) _dirty = true;
            return removed;
        }

        public void Clear()
        {
            _instances.Clear();
            _dirty = true;
        }

        /// <summary>计算总质量。</summary>
        public float GetTotalMass()
        {
            UpdateCacheIfNeeded();
            return _cachedMass;
        }

        /// <summary>计算质心(相对刚体局部系)。</summary>
        public Vector3 GetCenterOfMass()
        {
            UpdateCacheIfNeeded();
            return _cachedCenterOfMass;
        }

        /// <summary>计算惯性张量。</summary>
        public Matrix4x4 GetInertiaTensor()
        {
            UpdateCacheIfNeeded();
            return _cachedInertia;
        }

        private void UpdateCacheIfNeeded()
        {
            if (!_dirty) return;
            _dirty = false;

            _cachedMass = 0;
            _cachedCenterOfMass = Vector3.Zero;
            _cachedInertia = Matrix4x4.Identity;

            foreach (var inst in _instances)
            {
                var shape = _db.Get(inst.ShapeId);
                if (shape == null) continue;
                float vol = shape.Volume;
                float mass = vol * inst.Density;
                _cachedMass += mass;
                _cachedCenterOfMass += (inst.LocalOffset + shape.LocalCenter) * mass;
            }

            if (_cachedMass > 0)
                _cachedCenterOfMass /= _cachedMass;

            // 惯性张量(平行轴定理)
            foreach (var inst in _instances)
            {
                var shape = _db.Get(inst.ShapeId);
                if (shape == null) continue;
                float vol = shape.Volume;
                float mass = vol * inst.Density;
                var localInertia = ShapeDatabase.ComputeInertia(shape, mass);

                Vector3 d = inst.LocalOffset + shape.LocalCenter - _cachedCenterOfMass;
                float d2 = d.LengthSquared();
                Matrix4x4 shift = new(
                    d2 - d.X * d.X, -d.X * d.Y, -d.X * d.Z, 0,
                    -d.Y * d.X, d2 - d.Y * d.Y, -d.Y * d.Z, 0,
                    -d.Z * d.X, -d.Z * d.Y, d2 - d.Z * d.Z, 0,
                    0, 0, 0, 1);
                shift *= mass;

                Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(inst.LocalRotation);
                localInertia = rotMat * localInertia * Matrix4x4.Transpose(rotMat);

                _cachedInertia += localInertia + shift;
            }
        }

        /// <summary>获取整体AABB。</summary>
        public void GetAABB(Vector3 bodyPos, Quaternion bodyRot, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var inst in _instances)
            {
                inst.GetWorldAABB(_db, bodyPos, bodyRot, out var cmin, out var cmax);
                min = Vector3.Min(min, cmin);
                max = Vector3.Max(max, cmax);
            }
            if (min.X > max.X)
            {
                min = max = bodyPos;
            }
        }
    }
}
