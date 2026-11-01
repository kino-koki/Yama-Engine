using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics.Real
{
    /// <summary>
    /// 碰撞体基类
    /// </summary>
    public abstract class Collider
    {
        public ColliderType Type { get; protected set; }
        public Rigidbody? Body { get; private set; }
        public Vector3 Center { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;
        public bool IsTrigger { get; set; }
        public PhysicsMaterial? Material { get; set; }
        
        protected Collider(ColliderType type)
        {
            Type = type;
        }

        public abstract AABB GetBoundingBox();
        public abstract RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance);
        public abstract MassProperties CalculateMassProperties(float density);

        public void AttachToBody(Rigidbody body)
        {
            Body = body;
        }

        public void DetachFromBody()
        {
            Body = null;
        }

        /// <summary>
        /// 获取世界空间变换
        /// </summary>
        public Matrix4x4 GetWorldTransform()
        {
            Vector3 position = Body?.Position ?? Center;
            Quaternion rotation = Body?.Orientation ?? Rotation;
            
            return Matrix4x4.CreateScale(Scale) * 
                   Matrix4x4.CreateFromQuaternion(rotation) * 
                   Matrix4x4.CreateTranslation(position);
        }
    }

    /// <summary>
    /// 轴对齐包围盒
    /// </summary>
    public struct AABB
    {
        public Vector3 Min;
        public Vector3 Max;
        
        public AABB(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }
        
        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Extents => (Max - Min) * 0.5f;
        public Vector3 Size => Max - Min;
        
        public bool Contains(Vector3 point)
        {
            return point.X >= Min.X && point.X <= Max.X &&
                   point.Y >= Min.Y && point.Y <= Max.Y &&
                   point.Z >= Min.Z && point.Z <= Max.Z;
        }
        
        public bool Intersects(AABB other)
        {
            return Min.X <= other.Max.X && Max.X >= other.Min.X &&
                   Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
                   Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        }
        
        public static AABB Union(AABB a, AABB b)
        {
            return new AABB(
                Vector3.Min(a.Min, b.Min),
                Vector3.Max(a.Max, b.Max)
            );
        }
    }

    /// <summary>
    /// 射线检测结果
    /// </summary>
    public struct RaycastHit
    {
        public Collider Collider;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public Rigidbody? Rigidbody;
        
        public RaycastHit(Collider collider, Vector3 point, Vector3 normal, float distance, Rigidbody? rigidbody = null)
        {
            Collider = collider;
            Point = point;
            Normal = normal;
            Distance = distance;
            Rigidbody = rigidbody;
        }
    }

    /// <summary>
    /// 碰撞描述
    /// </summary>
    public sealed class ColliderDescription
    {
        public ColliderType Type { get; set; }
        public Vector3 Center { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;
        public bool IsTrigger { get; set; }
        public PhysicsMaterial? Material { get; set; }
        
        // 特定形状参数
        public Vector3 Size { get; set; } = Vector3.One; // Box
        public float Radius { get; set; } = 0.5f; // Sphere
        public float Height { get; set; } = 1.0f; // Capsule
        public Vector3[]? Vertices { get; set; } // Mesh
        public int[]? Indices { get; set; } // Mesh
    }

    /// <summary>
    /// 碰撞类型
    /// </summary>
    public enum ColliderType
    {
        Box,
        Sphere,
        Capsule,
        Mesh
    }

    /// <summary>
    /// 质量属性
    /// </summary>
    public struct MassProperties
    {
        public float Mass;
        public Matrix4x4 InertiaTensor;
        public Vector3 CenterOfMass;
    }

    /// <summary>
    /// 盒碰撞体
    /// </summary>
    public sealed class BoxCollider : Collider
    {
        public Vector3 Size { get; set; } = Vector3.One;
        
        public BoxCollider(ColliderDescription desc) : base(ColliderType.Box)
        {
            Center = desc.Center;
            Rotation = desc.Rotation;
            Scale = desc.Scale;
            Size = desc.Size;
            IsTrigger = desc.IsTrigger;
            Material = desc.Material;
        }

        public override AABB GetBoundingBox()
        {
            var transform = GetWorldTransform();
            var halfSize = Size * 0.5f;
            
            // 变换8个角点
            var corners = new[]
            {
                Vector3.Transform(new Vector3(-halfSize.X, -halfSize.Y, -halfSize.Z), transform),
                Vector3.Transform(new Vector3(halfSize.X, -halfSize.Y, -halfSize.Z), transform),
                Vector3.Transform(new Vector3(-halfSize.X, halfSize.Y, -halfSize.Z), transform),
                Vector3.Transform(new Vector3(halfSize.X, halfSize.Y, -halfSize.Z), transform),
                Vector3.Transform(new Vector3(-halfSize.X, -halfSize.Y, halfSize.Z), transform),
                Vector3.Transform(new Vector3(halfSize.X, -halfSize.Y, halfSize.Z), transform),
                Vector3.Transform(new Vector3(-halfSize.X, halfSize.Y, halfSize.Z), transform),
                Vector3.Transform(new Vector3(halfSize.X, halfSize.Y, halfSize.Z), transform)
            };
            
            Vector3 min = corners[0];
            Vector3 max = corners[0];
            foreach (var corner in corners)
            {
                min = Vector3.Min(min, corner);
                max = Vector3.Max(max, corner);
            }
            
            return new AABB(min, max);
        }

        public override RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
        {
            var transform = GetWorldTransform();
            var inverseTransform = Matrix4x4.Invert(transform, out var inv) ? inv : Matrix4x4.Identity;
            
            // 将射线变换到局部空间
            var localOrigin = Vector3.Transform(origin, inverseTransform);
            var localDirection = Vector3.TransformNormal(direction, inverseTransform);
            
            // AABB 射线检测
            var halfSize = Size * 0.5f;
            var tMin = float.MinValue;
            var tMax = float.MaxValue;
            
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(localDirection.GetComponent(i)) < 1e-6f)
                {
                    if (Math.Abs(localOrigin.GetComponent(i) - 0) > halfSize.GetComponent(i))
                        return null;
                }
                else
                {
                    float invDir = 1.0f / localDirection.GetComponent(i);
                    float t1 = (-halfSize.GetComponent(i) - localOrigin.GetComponent(i)) * invDir;
                    float t2 = (halfSize.GetComponent(i) - localOrigin.GetComponent(i)) * invDir;
                    
                    if (t1 > t2)
                    {
                        float temp = t1; t1 = t2; t2 = temp;
                    }
                    
                    tMin = MathF.Max(tMin, t1);
                    tMax = MathF.Min(tMax, t2);
                    
                    if (tMax < tMin || tMax < 0)
                        return null;
                }
            }
            
            if (tMin < 0) tMin = 0;
            if (tMin > maxDistance) return null;
            
            var hitPoint = localOrigin + localDirection * tMin;
            var worldHitPoint = Vector3.Transform(hitPoint, transform);
            var worldNormal = Vector3.TransformNormal(CalculateNormal(hitPoint, halfSize), transform);
            
            return new RaycastHit(this, worldHitPoint, Vector3.Normalize(worldNormal), tMin, Body);
        }

        private Vector3 CalculateNormal(Vector3 point, Vector3 halfSize)
        {
            // 找到最近的面
            float minDist = float.MaxValue;
            Vector3 normal = Vector3.UnitY;
            
            var faces = new[]
            {
                (Vector3.UnitX, Math.Abs(point.X - halfSize.X)),
                (-Vector3.UnitX, Math.Abs(point.X + halfSize.X)),
                (Vector3.UnitY, Math.Abs(point.Y - halfSize.Y)),
                (-Vector3.UnitY, Math.Abs(point.Y + halfSize.Y)),
                (Vector3.UnitZ, Math.Abs(point.Z - halfSize.Z)),
                (-Vector3.UnitZ, Math.Abs(point.Z + halfSize.Z))
            };
            
            foreach (var (n, dist) in faces)
            {
                if (dist < minDist)
                {
                    minDist = dist;
                    normal = n;
                }
            }
            
            return normal;
        }

        public override MassProperties CalculateMassProperties(float density)
        {
            float mass = density * Size.X * Size.Y * Size.Z;
            
            // 长方体惯量张量
            float ixx = (1.0f / 12.0f) * mass * (Size.Y * Size.Y + Size.Z * Size.Z);
            float iyy = (1.0f / 12.0f) * mass * (Size.X * Size.X + Size.Z * Size.Z);
            float izz = (1.0f / 12.0f) * mass * (Size.X * Size.X + Size.Y * Size.Y);
            
            var inertia = new Matrix4x4(
                ixx, 0, 0, 0,
                0, iyy, 0, 0,
                0, 0, izz, 0,
                0, 0, 0, 1
            );
            
            return new MassProperties
            {
                Mass = mass,
                InertiaTensor = inertia,
                CenterOfMass = Vector3.Zero
            };
        }
    }

    /// <summary>
    /// 球体碰撞体
    /// </summary>
    public sealed class SphereCollider : Collider
    {
        public float Radius { get; set; } = 0.5f;
        
        public SphereCollider(ColliderDescription desc) : base(ColliderType.Sphere)
        {
            Center = desc.Center;
            Radius = desc.Radius;
            IsTrigger = desc.IsTrigger;
            Material = desc.Material;
        }

        public override AABB GetBoundingBox()
        {
            var position = Body?.Position ?? Center;
            var scale = Body?.Scale ?? Vector3.One;
            float maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
            float scaledRadius = Radius * maxScale;
            
            return new AABB(
                position - Vector3.One * scaledRadius,
                position + Vector3.One * scaledRadius
            );
        }

        public override RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
        {
            var position = Body?.Position ?? Center;
            var scale = Body?.Scale ?? Vector3.One;
            float maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
            float scaledRadius = Radius * maxScale;
            
            var oc = origin - position;
            float a = Vector3.Dot(direction, direction);
            float b = 2.0f * Vector3.Dot(oc, direction);
            float c = Vector3.Dot(oc, oc) - scaledRadius * scaledRadius;
            
            float discriminant = b * b - 4.0f * a * c;
            if (discriminant < 0)
                return null;
                
            float sqrtDiscriminant = MathF.Sqrt(discriminant);
            float t1 = (-b - sqrtDiscriminant) / (2.0f * a);
            float t2 = (-b + sqrtDiscriminant) / (2.0f * a);
            
            float t = MathF.Min(t1, t2);
            if (t < 0) t = MathF.Max(t1, t2);
            if (t < 0 || t > maxDistance)
                return null;
                
            var hitPoint = origin + direction * t;
            var normal = Vector3.Normalize(hitPoint - position);
            
            return new RaycastHit(this, hitPoint, normal, t, Body);
        }

        public override MassProperties CalculateMassProperties(float density)
        {
            float mass = (4.0f / 3.0f) * MathF.PI * Radius * Radius * Radius * density;
            
            // 球体惯量张量
            float i = (2.0f / 5.0f) * mass * Radius * Radius;
            var inertia = new Matrix4x4(
                i, 0, 0, 0,
                0, i, 0, 0,
                0, 0, i, 0,
                0, 0, 0, 1
            );
            
            return new MassProperties
            {
                Mass = mass,
                InertiaTensor = inertia,
                CenterOfMass = Vector3.Zero
            };
        }
    }

    /// <summary>
    /// 胶囊碰撞体
    /// </summary>
    public sealed class CapsuleCollider : Collider
    {
        public float Radius { get; set; } = 0.5f;
        public float Height { get; set; } = 1.0f;
        
        public CapsuleCollider(ColliderDescription desc) : base(ColliderType.Capsule)
        {
            Center = desc.Center;
            Radius = desc.Radius;
            Height = desc.Height;
            IsTrigger = desc.IsTrigger;
            Material = desc.Material;
        }

        public override AABB GetBoundingBox()
        {
            var position = Body?.Position ?? Center;
            var scale = Body?.Scale ?? Vector3.One;
            float scaledRadius = Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
            float scaledHeight = Height * scale.Y;
            
            float halfHeight = scaledHeight * 0.5f;
            var top = position + Vector3.UnitY * halfHeight;
            var bottom = position - Vector3.UnitY * halfHeight;
            
            return new AABB(
                new Vector3(position.X - scaledRadius, bottom.Y - scaledRadius, position.Z - scaledRadius),
                new Vector3(position.X + scaledRadius, top.Y + scaledRadius, position.Z + scaledRadius)
            );
        }

        public override RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
        {
            // 简化实现：检测胶囊的两个半球和中间圆柱
            var position = Body?.Position ?? Center;
            var scale = Body?.Scale ?? Vector3.One;
            float scaledRadius = Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
            float scaledHeight = Height * scale.Y;
            
            float halfHeight = scaledHeight * 0.5f;
            var top = position + Vector3.UnitY * halfHeight;
            var bottom = position - Vector3.UnitY * halfHeight;
            
            // 检测两个球体
            RaycastHit? topHit = RaySphere(origin, direction, maxDistance, top, scaledRadius);
            RaycastHit? bottomHit = RaySphere(origin, direction, maxDistance, bottom, scaledRadius);
            
            RaycastHit? closest = null;
            if (topHit.HasValue && (!closest.HasValue || topHit.Value.Distance < closest.Value.Distance))
                closest = topHit;
            if (bottomHit.HasValue && (!closest.HasValue || bottomHit.Value.Distance < closest.Value.Distance))
                closest = bottomHit;
            
            return closest;
        }
        
        private RaycastHit? RaySphere(Vector3 origin, Vector3 direction, float maxDistance, Vector3 sphereCenter, float radius)
        {
            var oc = origin - sphereCenter;
            float a = Vector3.Dot(direction, direction);
            float b = 2.0f * Vector3.Dot(oc, direction);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            
            float discriminant = b * b - 4.0f * a * c;
            if (discriminant < 0)
                return null;
                
            float sqrtDiscriminant = MathF.Sqrt(discriminant);
            float t1 = (-b - sqrtDiscriminant) / (2.0f * a);
            float t2 = (-b + sqrtDiscriminant) / (2.0f * a);
            
            float t = MathF.Min(t1, t2);
            if (t < 0) t = MathF.Max(t1, t2);
            if (t < 0 || t > maxDistance)
                return null;
                
            var hitPoint = origin + direction * t;
            var normal = Vector3.Normalize(hitPoint - sphereCenter);
            
            return new RaycastHit(this, hitPoint, normal, t, Body);
        }

        public override MassProperties CalculateMassProperties(float density)
        {
            // 胶囊 = 两个半球 + 圆柱
            float cylinderVolume = MathF.PI * Radius * Radius * (Height - 2 * Radius);
            float sphereVolume = (4.0f / 3.0f) * MathF.PI * Radius * Radius * Radius;
            float totalVolume = cylinderVolume + sphereVolume;
            float mass = density * totalVolume;
            
            // 近似惯量张量
            float i = (1.0f / 12.0f) * mass * (3 * Radius * Radius + Height * Height);
            var inertia = new Matrix4x4(
                i, 0, 0, 0,
                0, i, 0, 0,
                0, 0, i, 0,
                0, 0, 0, 1
            );
            
            return new MassProperties
            {
                Mass = mass,
                InertiaTensor = inertia,
                CenterOfMass = Vector3.Zero
            };
        }
    }

    /// <summary>
    /// 网格碰撞体（简化实现）
    /// </summary>
    public sealed class MeshCollider : Collider
    {
        public Vector3[] Vertices { get; set; } = Array.Empty<Vector3>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        
        public MeshCollider(ColliderDescription desc) : base(ColliderType.Mesh)
        {
            Center = desc.Center;
            Rotation = desc.Rotation;
            Scale = desc.Scale;
            Vertices = desc.Vertices ?? Array.Empty<Vector3>();
            Indices = desc.Indices ?? Array.Empty<int>();
            IsTrigger = desc.IsTrigger;
            Material = desc.Material;
        }

        public override AABB GetBoundingBox()
        {
            if (Vertices.Length == 0)
                return new AABB(Center, Center);
                
            var transform = GetWorldTransform();
            var transformedVertices = new Vector3[Vertices.Length];
            for (int i = 0; i < Vertices.Length; i++)
            {
                transformedVertices[i] = Vector3.Transform(Vertices[i], transform);
            }
            
            Vector3 min = transformedVertices[0];
            Vector3 max = transformedVertices[0];
            foreach (var vertex in transformedVertices)
            {
                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }
            
            return new AABB(min, max);
        }

        public override RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
        {
            // 简化实现：只检测包围盒
            var aabb = GetBoundingBox();
            
            float tMin = float.MinValue;
            float tMax = float.MaxValue;
            var extents = aabb.Extents;
            
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(direction.GetComponent(i)) < 1e-6f)
                {
                    if (Math.Abs(origin.GetComponent(i) - aabb.Center.GetComponent(i)) > extents.GetComponent(i))
                        return null;
                }
                else
                {
                    float invDir = 1.0f / direction.GetComponent(i);
                    float t1 = (aabb.Min.GetComponent(i) - origin.GetComponent(i)) * invDir;
                    float t2 = (aabb.Max.GetComponent(i) - origin.GetComponent(i)) * invDir;
                    
                    if (t1 > t2)
                    {
                        float temp = t1; t1 = t2; t2 = temp;
                    }
                    
                    tMin = MathF.Max(tMin, t1);
                    tMax = MathF.Min(tMax, t2);
                    
                    if (tMax < tMin || tMax < 0)
                        return null;
                }
            }
            
            if (tMin < 0) tMin = 0;
            if (tMin > maxDistance) return null;
            
            var hitPoint = origin + direction * tMin;
            var normal = Vector3.UnitY; // 简化：总是返回向上法线
            
            return new RaycastHit(this, hitPoint, normal, tMin, Body);
        }

        public override MassProperties CalculateMassProperties(float density)
        {
            // 简化实现：使用包围盒近似
            var aabb = GetBoundingBox();
            float mass = density * aabb.Size.X * aabb.Size.Y * aabb.Size.Z;
            
            // 长方体惯量张量
            float ixx = (1.0f / 12.0f) * mass * (aabb.Size.Y * aabb.Size.Y + aabb.Size.Z * aabb.Size.Z);
            float iyy = (1.0f / 12.0f) * mass * (aabb.Size.X * aabb.Size.X + aabb.Size.Z * aabb.Size.Z);
            float izz = (1.0f / 12.0f) * mass * (aabb.Size.X * aabb.Size.X + aabb.Size.Y * aabb.Size.Y);
            
            var inertia = new Matrix4x4(
                ixx, 0, 0, 0,
                0, iyy, 0, 0,
                0, 0, izz, 0,
                0, 0, 0, 1
            );
            
            return new MassProperties
            {
                Mass = mass,
                InertiaTensor = inertia,
                CenterOfMass = aabb.Center
            };
        }
    }
}