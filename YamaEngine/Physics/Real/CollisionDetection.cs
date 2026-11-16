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

                var overlapping = _tree?.Query(aabbA) ?? new List<Collider>();

                foreach (var colliderB in overlapping)
                {
                    // 排除自身；用对象引用比较（比 hashcode 更可靠）。
                    if (ReferenceEquals(colliderA, colliderB))
                        continue;

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

            // 委托给与运行时一致的分离轴定理（SAT）实现。
            // 旧的"角点-在-盒内"测试在面接触时无法生成接触点（角点 clamp 后 penetration=0），
            // 且对角穿透时产生斜向伪法线，导致盒子无法在地面静止。
            var tA = boxA.GetWorldTransform();
            var tB = boxB.GetWorldTransform();
            var rotA = Quaternion.CreateFromRotationMatrix(tA);
            var rotB = Quaternion.CreateFromRotationMatrix(tB);
            var posA = tA.Translation;
            var posB = tB.Translation;

            // SAT 求最小穿透轴（法线从 A 指向 B）。
            Span<Vector3> axesA = stackalloc Vector3[3];
            Span<Vector3> axesB = stackalloc Vector3[3];
            axesA[0] = Vector3.Transform(Vector3.UnitX, rotA);
            axesA[1] = Vector3.Transform(Vector3.UnitY, rotA);
            axesA[2] = Vector3.Transform(Vector3.UnitZ, rotA);
            axesB[0] = Vector3.Transform(Vector3.UnitX, rotB);
            axesB[1] = Vector3.Transform(Vector3.UnitY, rotB);
            axesB[2] = Vector3.Transform(Vector3.UnitZ, rotB);

            Vector3 halfA = boxA.Size * 0.5f;
            Vector3 halfB = boxB.Size * 0.5f;
            Vector3 t = posB - posA;

            Span<float> R = stackalloc float[9];
            Span<float> AbsR = stackalloc float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    R[i * 3 + j] = Vector3.Dot(axesA[i], axesB[j]);
                    AbsR[i * 3 + j] = MathF.Abs(R[i * 3 + j]) + 1e-5f;
                }

            float minPen = float.MaxValue;
            Vector3 minAxis = Vector3.Zero;

            for (int i = 0; i < 3; i++)
            {
                float ra = halfA[i];
                float rb = halfB[0] * AbsR[i * 3 + 0] + halfB[1] * AbsR[i * 3 + 1] + halfB[2] * AbsR[i * 3 + 2];
                float pen = ra + rb - MathF.Abs(Vector3.Dot(t, axesA[i]));
                if (pen < 0) return contacts; // 分离
                if (pen < minPen) { minPen = pen; minAxis = axesA[i]; }
            }

            for (int j = 0; j < 3; j++)
            {
                float ra = halfA[0] * AbsR[j] + halfA[1] * AbsR[3 + j] + halfA[2] * AbsR[6 + j];
                float rb = halfB[j];
                float pen = ra + rb - MathF.Abs(Vector3.Dot(t, axesB[j]));
                if (pen < 0) return contacts; // 分离
                if (pen < minPen) { minPen = pen; minAxis = axesB[j]; }
            }

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Vector3 axis = Vector3.Cross(axesA[i], axesB[j]);
                    float lenSq = axis.LengthSquared();
                    if (lenSq < 1e-5f) continue;
                    axis /= MathF.Sqrt(lenSq);

                    float ra = 0, rb = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        ra += halfA[k] * MathF.Abs(Vector3.Dot(axesA[k], axis));
                        rb += halfB[k] * MathF.Abs(Vector3.Dot(axesB[k], axis));
                    }
                    float pen = ra + rb - MathF.Abs(Vector3.Dot(t, axis));
                    if (pen < 0) return contacts; // 分离
                    if (pen < minPen) { minPen = pen; minAxis = axis; }
                }
            }

            // 保证法线从 A 指向 B。
            if (Vector3.Dot(t, minAxis) < 0) minAxis = -minAxis;
            if (minAxis.LengthSquared() < 1e-10f) minAxis = Vector3.UnitY;
            minAxis = Vector3.Normalize(minAxis);

            // 单点面接触：接触点取 B 中心在 A 表面的投影（面接触的合理近似）。
            // 注：面-面 4 点 manifold 曾尝试 9 次以修复 TestKineticFrictionDeceleration，
            // 每次都在下落冲击/静置边际深度下退化（盒漂到 Y=1.27~1.36、倾斜被抛离），
            // 均回滚到本单点基线（13/14）。当前稳定在 13/14，勿再尝试简化 manifold——
            // 正确修复需 PhysX/Box2D b2CollidePolygons 级完整 EPA + incident-face clipping，
            // 是一次大重构，且必须同时处理 bias/N 防弹飞，增量尝试都会破坏现有 13 项测试。
            Vector3 contactPoint = posA + minAxis * Vector3.Dot(minAxis, posB - posA);
            contacts.Add(new Contact
            {
                Point = contactPoint,
                Normal = minAxis,
                PenetrationDepth = MathF.Max(0f, minPen),
                BodyA = boxA.Body,
                BodyB = boxB.Body,
                ColliderA = boxA,
                ColliderB = boxB,
                PointA = contactPoint,
                PointB = contactPoint
            });

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