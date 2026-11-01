using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.Physics.Real
{
    /// <summary>
    /// Vector3 数学扩展。
    /// </summary>
    internal static class Vector3MathExtensions
    {
        /// <summary>将指定分量替换为新值。</summary>
        public static Vector3 WithComponent(this Vector3 v, int index, float value)
        {
            switch (index)
            {
                case 0: return new Vector3(value, v.Y, v.Z);
                case 1: return new Vector3(v.X, value, v.Z);
                case 2: return new Vector3(v.X, v.Y, value);
                default: return v;
            }
        }
    }

    /// <summary>
    /// 宽相位碰撞检测 - 使用 AABB 树进行快速剔除
    /// </summary>
    public sealed class BroadPhase
    {
        private readonly List<Collider> _colliders = new();
        private AabbTree? _tree;
        
        public void UpdateColliderBounds()
        {
            // 更新所有碰撞体的包围盒
            foreach (var collider in _colliders)
            {
                // 碰撞体会在 GetBoundingBox 中自动更新变换
            }
        }
        
        public void SetColliders(List<Collider> colliders)
        {
            _colliders.Clear();
            _colliders.AddRange(colliders);
            RebuildTree();
        }
        
        private void RebuildTree()
        {
            if (_colliders.Count == 0)
            {
                _tree = null;
                return;
            }
            
            // 构建 AABB 树
            _tree = new AabbTree(_colliders);
        }
        
        public List<(Collider, Collider)> FindPotentialCollisions(List<Collider> colliders)
        {
            if (colliders.Count < 2)
                return new List<(Collider, Collider)>();
                
            var pairs = new List<(Collider, Collider)>();
            
            // 更新并重建树
            SetColliders(colliders);
            
            // 使用 AABB 树查询潜在碰撞对
            for (int i = 0; i < colliders.Count; i++)
            {
                var colliderA = colliders[i];
                var aabbA = colliderA.GetBoundingBox();
                
                // 查询与当前碰撞体 AABB 相交的所有碰撞体
                var overlapping = _tree?.Query(aabbA) ?? new List<Collider>();
                
                foreach (var colliderB in overlapping)
                {
                    if (colliderA.GetHashCode() >= colliderB.GetHashCode())
                        continue; // 避免重复检测
                        
                    // 检查碰撞层
                    if (!ShouldCollide(colliderA.Body, colliderB.Body))
                        continue;
                        
                    pairs.Add((colliderA, colliderB));
                }
            }
            
            return pairs;
        }
        
        private bool ShouldCollide(Rigidbody? bodyA, Rigidbody? bodyB)
        {
            if (bodyA == null || bodyB == null)
                return true;
                
            if (!bodyA.EnableCollision || !bodyB.EnableCollision)
                return false;
                
            bool aCollidesWithB = (bodyA.CollisionMask & bodyB.CollisionLayer) != 0;
            bool bCollidesWithA = (bodyB.CollisionMask & bodyA.CollisionLayer) != 0;
            
            return aCollidesWithB && bCollidesWithA;
        }
        
        /// <summary>
        /// AABB 树 - 用于快速宽相位碰撞检测
        /// </summary>
        private sealed class AabbTree
        {
            private Node? _root;
            private readonly Dictionary<Collider, Node> _nodeMap = new();
            
            public AabbTree(List<Collider> colliders)
            {
                if (colliders.Count == 0)
                    return;
                    
                // 创建叶子节点
                var nodes = colliders.Select(c => new Node
                {
                    Collider = c,
                    AABB = c.GetBoundingBox(),
                    Height = 0,
                    IsLeaf = true
                }).ToList();
                
                // 构建树
                _root = BuildTopDown(nodes);
                
                // 建立映射
                foreach (var node in nodes)
                {
                    _nodeMap[node.Collider!] = node;
                }
            }
            
            private Node? BuildTopDown(List<Node> nodes)
            {
                if (nodes.Count == 0)
                    return null;
                    
                if (nodes.Count == 1)
                    return nodes[0];
                    
                // 找到最长的轴
                var allBounds = nodes.Select(n => n.AABB).ToArray();
                Vector3 min = allBounds[0].Min;
                Vector3 max = allBounds[0].Max;
                
                foreach (var aabb in allBounds)
                {
                    min = Vector3.Min(min, aabb.Min);
                    max = Vector3.Max(max, aabb.Max);
                }
                
                Vector3 size = max - min;
                int splitAxis = 0;
                if (size.Y > size.X) splitAxis = 1;
                if (size.Z > size.Y) splitAxis = 2;
                
                // 按中心点排序
                nodes.Sort((a, b) => 
                {
                    float centerA = a.AABB.Center.GetComponent(splitAxis);
                    float centerB = b.AABB.Center.GetComponent(splitAxis);
                    return centerA.CompareTo(centerB);
                });
                
                // 分割
                int mid = nodes.Count / 2;
                var leftNodes = nodes.Take(mid).ToList();
                var rightNodes = nodes.Skip(mid).ToList();
                
                var leftChild = BuildTopDown(leftNodes);
                var rightChild = BuildTopDown(rightNodes);
                
                if (leftChild == null) return rightChild;
                if (rightChild == null) return leftChild;
                
                // 创建内部节点
                var combinedAABB = AABB.Union(leftChild.AABB, rightChild.AABB);
                return new Node
                {
                    AABB = combinedAABB,
                    Left = leftChild,
                    Right = rightChild,
                    Height = Math.Max(leftChild.Height, rightChild.Height) + 1,
                    IsLeaf = false
                };
            }
            
            public List<Collider> Query(AABB query)
            {
                var results = new List<Collider>();
                QueryRecursive(_root, query, results);
                return results;
            }
            
            private void QueryRecursive(Node? node, AABB query, List<Collider> results)
            {
                if (node == null || !node.AABB.Intersects(query))
                    return;
                    
                if (node.IsLeaf && node.Collider != null)
                {
                    results.Add(node.Collider);
                }
                else
                {
                    QueryRecursive(node.Left, query, results);
                    QueryRecursive(node.Right, query, results);
                }
            }
            
            private sealed class Node
            {
                public AABB AABB;
                public Collider? Collider;
                public Node? Left;
                public Node? Right;
                public int Height;
                public bool IsLeaf;
            }
        }
    }

    /// <summary>
    /// 窄相位碰撞检测 - 精确的碰撞检测
    /// </summary>
    public sealed class NarrowPhase
    {
        public List<Contact> GenerateContacts(Collider colliderA, Collider colliderB)
        {
            var contacts = new List<Contact>();
            
            // 根据碰撞体类型调用相应的检测算法
            if (colliderA is BoxCollider boxA && colliderB is BoxCollider boxB)
            {
                contacts.AddRange(BoxBox(boxA, boxB));
            }
            else if (colliderA is SphereCollider sphereA && colliderB is SphereCollider sphereB)
            {
                contacts.AddRange(SphereSphere(sphereA, sphereB));
            }
            else if (colliderA is SphereCollider sphA && colliderB is BoxCollider bxB)
            {
                contacts.AddRange(SphereBox(sphA, bxB));
            }
            else if (colliderA is BoxCollider bxA && colliderB is SphereCollider sphB)
            {
                contacts.AddRange(SphereBox(sphB, bxA)); // 交换参数
            }
            else
            {
                // 简化实现：对其他组合使用 AABB 近似
                contacts.AddRange(AabbApproximation(colliderA, colliderB));
            }
            
            return contacts;
        }
        
        private List<Contact> BoxBox(BoxCollider boxA, BoxCollider boxB)
        {
            var contacts = new List<Contact>();
            
            // 计算世界空间变换
            var transformA = boxA.GetWorldTransform();
            var transformB = boxB.GetWorldTransform();
            var inverseTransformB = Matrix4x4.Invert(transformB, out var invB) ? invB : Matrix4x4.Identity;
            
            // 将 boxA 的8个角点变换到 boxB 的局部空间
            var halfSizeA = boxA.Size * 0.5f;
            var cornersA = new[]
            {
                new Vector3(-halfSizeA.X, -halfSizeA.Y, -halfSizeA.Z),
                new Vector3(halfSizeA.X, -halfSizeA.Y, -halfSizeA.Z),
                new Vector3(-halfSizeA.X, halfSizeA.Y, -halfSizeA.Z),
                new Vector3(halfSizeA.X, halfSizeA.Y, -halfSizeA.Z),
                new Vector3(-halfSizeA.X, -halfSizeA.Y, halfSizeA.Z),
                new Vector3(halfSizeA.X, -halfSizeA.Y, halfSizeA.Z),
                new Vector3(-halfSizeA.X, halfSizeA.Y, halfSizeA.Z),
                new Vector3(halfSizeA.X, halfSizeA.Y, halfSizeA.Z)
            };
            
            var halfSizeB = boxB.Size * 0.5f;
            
            // 检查每个角点是否在 boxB 内
            foreach (var corner in cornersA)
            {
                var worldCorner = Vector3.Transform(corner, transformA);
                var localCornerB = Vector3.Transform(worldCorner, inverseTransformB);
                
                // 计算最近的表面点和穿透深度
                Vector3 clampedPoint = Vector3.Clamp(localCornerB, -halfSizeB, halfSizeB);
                Vector3 penetration = localCornerB - clampedPoint;
                float penetrationDepth = penetration.Length();
                
                if (penetrationDepth > 1e-6f)
                {
                    Vector3 normalB = Vector3.Normalize(penetration);
                    Vector3 normalA = Vector3.TransformNormal(-normalB, transformB);
                    
                    Vector3 worldPoint = worldCorner - normalA * penetrationDepth;
                    
                    var contact = new Contact
                    {
                        Point = worldPoint,
                        Normal = Vector3.Normalize(normalA),
                        PenetrationDepth = penetrationDepth,
                        BodyA = boxA.Body,
                        BodyB = boxB.Body,
                        ColliderA = boxA,
                        ColliderB = boxB,
                        PointA = corner,
                        PointB = clampedPoint
                    };
                    
                    contacts.Add(contact);
                }
            }
            
            // 限制最多4个接触点
            if (contacts.Count > 4)
            {
                // 选择穿透最深的4个点
                contacts = contacts.OrderByDescending(c => c.PenetrationDepth).Take(4).ToList();
            }
            
            return contacts;
        }
        
        private List<Contact> SphereSphere(SphereCollider sphereA, SphereCollider sphereB)
        {
            var contacts = new List<Contact>();
            
            var posA = sphereA.Body?.Position ?? sphereA.Center;
            var posB = sphereB.Body?.Position ?? sphereB.Center;
            var scaleA = sphereA.Body?.Scale ?? Vector3.One;
            var scaleB = sphereB.Body?.Scale ?? Vector3.One;
            
            float radiusA = sphereA.Radius * MathF.Max(scaleA.X, MathF.Max(scaleA.Y, scaleA.Z));
            float radiusB = sphereB.Radius * MathF.Max(scaleB.X, MathF.Max(scaleB.Y, scaleB.Z));
            
            Vector3 diff = posB - posA;
            float distance = diff.Length();
            float combinedRadius = radiusA + radiusB;
            
            if (distance < combinedRadius && distance > 1e-6f)
            {
                Vector3 normal = diff / distance;
                float penetrationDepth = combinedRadius - distance;
                
                var contact = new Contact
                {
                    Point = posA + normal * radiusA,
                    Normal = normal,
                    PenetrationDepth = penetrationDepth,
                    BodyA = sphereA.Body,
                    BodyB = sphereB.Body,
                    ColliderA = sphereA,
                    ColliderB = sphereB,
                    PointA = normal * radiusA,
                    PointB = -normal * radiusB
                };
                
                contacts.Add(contact);
            }
            
            return contacts;
        }
        
        private List<Contact> SphereBox(SphereCollider sphere, BoxCollider box)
        {
            var contacts = new List<Contact>();
            
            var transformBox = box.GetWorldTransform();
            var inverseTransformBox = Matrix4x4.Invert(transformBox, out var invBox) ? invBox : Matrix4x4.Identity;
            
            var spherePos = sphere.Body?.Position ?? sphere.Center;
            var scaleSphere = sphere.Body?.Scale ?? Vector3.One;
            float sphereRadius = sphere.Radius * MathF.Max(scaleSphere.X, MathF.Max(scaleSphere.Y, scaleSphere.Z));
            
            // 将球心变换到盒子局部空间
            var localSpherePos = Vector3.Transform(spherePos, inverseTransformBox);
            
            // 找到盒子中离球心最近的点
            var halfSizeBox = box.Size * 0.5f;
            var closestPointBox = Vector3.Clamp(localSpherePos, -halfSizeBox, halfSizeBox);
            
            // 计算距离
            var diff = localSpherePos - closestPointBox;
            float distance = diff.Length();
            
            if (distance < sphereRadius)
            {
                Vector3 normal;
                Vector3 contactPoint;
                float penetrationDepth;
                
                if (distance < 1e-6f)
                {
                    // 球心在盒子内部
                    normal = Vector3.UnitY;
                    contactPoint = spherePos;
                    penetrationDepth = sphereRadius;
                }
                else
                {
                    normal = diff / distance;
                    contactPoint = closestPointBox;
                    penetrationDepth = sphereRadius - distance;
                }
                
                // 将法线和接触点变换回世界空间
                var worldNormal = Vector3.TransformNormal(normal, transformBox);
                var worldContactPoint = Vector3.Transform(contactPoint, transformBox);
                
                var contact = new Contact
                {
                    Point = worldContactPoint,
                    Normal = Vector3.Normalize(worldNormal),
                    PenetrationDepth = penetrationDepth,
                    BodyA = sphere.Body,
                    BodyB = box.Body,
                    ColliderA = sphere,
                    ColliderB = box,
                    PointA = -worldNormal * sphereRadius,
                    PointB = contactPoint - sphere.Body?.Position ?? Vector3.Zero
                };
                
                contacts.Add(contact);
            }
            
            return contacts;
        }
        
        private List<Contact> AabbApproximation(Collider colliderA, Collider colliderB)
        {
            var contacts = new List<Contact>();
            
            // 使用 AABB 近似计算
            var aabbA = colliderA.GetBoundingBox();
            var aabbB = colliderB.GetBoundingBox();
            
            if (!aabbA.Intersects(aabbB))
                return contacts;
            
            // 计算穿透深度
            var overlap = new Vector3(
                MathF.Min(aabbA.Max.X, aabbB.Max.X) - MathF.Max(aabbA.Min.X, aabbB.Min.X),
                MathF.Min(aabbA.Max.Y, aabbB.Max.Y) - MathF.Max(aabbA.Min.Y, aabbB.Min.Y),
                MathF.Min(aabbA.Max.Z, aabbB.Max.Z) - MathF.Max(aabbA.Min.Z, aabbB.Min.Z)
            );
            
            if (overlap.X <= 0 || overlap.Y <= 0 || overlap.Z <= 0)
                return contacts;
            
            // 找到最小穿透轴
            int minAxis = 0;
            float minPenetration = overlap.X;
            if (overlap.Y < minPenetration)
            {
                minAxis = 1;
                minPenetration = overlap.Y;
            }
            if (overlap.Z < minPenetration)
            {
                minAxis = 2;
                minPenetration = overlap.Z;
            }
            
            Vector3 normal = Vector3.Zero;
            normal = normal.WithComponent(minAxis, 1.0f);
            
            // 确保法线从 A 指向 B
            var centerA = aabbA.Center;
            var centerB = aabbB.Center;
            if (Vector3.Dot(normal, centerB - centerA) < 0)
                normal = -normal;
            
            var contact = new Contact
            {
                Point = (aabbA.Center + aabbB.Center) * 0.5f,
                Normal = normal,
                PenetrationDepth = minPenetration,
                BodyA = colliderA.Body,
                BodyB = colliderB.Body,
                ColliderA = colliderA,
                ColliderB = colliderB
            };
            
            contacts.Add(contact);
            return contacts;
        }
    }
}