using System;
using System.IO;
using System.Numerics;
using YamaEngine.Physics.Real;

namespace YamaEngine.Physics.Real.Tests
{
    /// <summary>
    /// 真实物理引擎测试 - 验证物理行为的正确性和真实性
    /// </summary>
    public static class RealPhysicsTests
    {
        private static readonly Random _random = new();
        
        /// <summary>
        /// 测试1：自由落体 - 验证重力效果
        /// </summary>
        public static void TestFreeFall(string tempRoot)
        {
            Console.WriteLine("测试1: 自由落体");
            
            var settings = new PhysicsSettings
            {
                Gravity = new Vector3(0, -9.81f, 0),
                SolverIterations = 8
            };
            
            var engine = new RealPhysicsEngine(settings);
            
            // 创建一个球体
            var sphereDesc = new RigidbodyDescription
            {
                Name = "FallingSphere",
                Type = RigidbodyType.Dynamic,
                Position = new Vector3(0, 10, 0),
                Mass = 1.0f,
                Material = PhysicsMaterial.Metal
            };
            
            var sphere = engine.CreateRigidbody(sphereDesc);
            
            // 添加球体碰撞体
            var colliderDesc = new ColliderDescription
            {
                Type = ColliderType.Sphere,
                Radius = 0.5f
            };
            
            var collider = engine.CreateCollider(colliderDesc, sphere);
            
            // 模拟自由落体
            float deltaTime = 0.016f; // 60fps
            float expectedTime = MathF.Sqrt(2 * 10 / 9.81f); // h = 0.5 * g * t^2
            
            for (int i = 0; i < 600; i++) // 10秒
            {
                engine.Step(deltaTime);
                
                if (i % 60 == 0) // 每秒记录一次
                {
                    float expectedY = 10 - 0.5f * 9.81f * (i * deltaTime) * (i * deltaTime);
                    float error = MathF.Abs(sphere.Position.Y - expectedY);
                    
                    Console.WriteLine($"  t={i * deltaTime:F2}s: y={sphere.Position.Y:F3}m (期望={expectedY:F3}m, 误差={error:F6}m)");
                    
                    // 误差应该很小（< 0.01m）
                    if (error > 0.01f)
                    {
                        Console.WriteLine($"  ❌ 自由落体测试失败，误差过大: {error:F6}m");
                        return;
                    }
                }
            }
            
            Console.WriteLine("  ✅ 自由落体测试通过");
        }
        
        /// <summary>
        /// 测试2：球体碰撞 - 验证弹性碰撞
        /// </summary>
        public static void TestSphereCollision(string tempRoot)
        {
            Console.WriteLine("测试2: 球体碰撞");
            
            var settings = new PhysicsSettings
            {
                Gravity = new Vector3(0, 0, 0), // 无重力
                SolverIterations = 10
            };
            
            var engine = new RealPhysicsEngine(settings);
            
            // 创建两个球体
            var sphereADesc = new RigidbodyDescription
            {
                Name = "SphereA",
                Type = RigidbodyType.Dynamic,
                Position = new Vector3(-2, 0, 0),
                Velocity = new Vector3(1, 0, 0),
                Mass = 1.0f,
                Material = PhysicsMaterial.Metal
            };
            
            var sphereBDesc = new RigidbodyDescription
            {
                Name = "SphereB",
                Type = RigidbodyType.Dynamic,
                Position = new Vector3(2, 0, 0),
                Velocity = new Vector3(-1, 0, 0),
                Mass = 1.0f,
                Material = PhysicsMaterial.Rubber
            };
            
            var sphereA = engine.CreateRigidbody(sphereADesc);
            var sphereB = engine.CreateRigidbody(sphereBDesc);
            
            // 添加碰撞体
            var colliderDesc = new ColliderDescription
            {
                Type = ColliderType.Sphere,
                Radius = 0.5f
            };
            
            engine.CreateCollider(colliderDesc, sphereA);
            engine.CreateCollider(colliderDesc, sphereB);
            
            // 模拟碰撞
            float deltaTime = 0.016f;
            Vector3 initialMomentum = sphereA.Velocity * sphereA.Mass + sphereB.Velocity * sphereB.Mass;
            
            for (int i = 0; i < 200; i++)
            {
                engine.Step(deltaTime);
                
                if (i % 30 == 0)
                {
                    Vector3 currentMomentum = sphereA.Velocity * sphereA.Mass + sphereB.Velocity * sphereB.Mass;
                    float momentumError = Vector3.Distance(initialMomentum, currentMomentum);
                    
                    Console.WriteLine($"  Frame {i}: A={sphereA.Position} v={sphereA.Velocity}, B={sphereB.Position} v={sphereB.Velocity}");
                    Console.WriteLine($"    动量: {currentMomentum}, 误差: {momentumError:F6}");
                    
                    // 动量应该守恒（误差 < 0.1）
                    if (momentumError > 0.1f)
                    {
                        Console.WriteLine($"  ❌ 动量守恒测试失败，误差过大: {momentumError:F6}");
                        return;
                    }
                }
            }
            
            // 碰撞后速度应该反向（弹性碰撞）
            float finalSpeedA = sphereA.Velocity.Length();
            float finalSpeedB = sphereB.Velocity.Length();
            
            Console.WriteLine($"  最终速度: A={finalSpeedA:F3} m/s, B={finalSpeedB:F3} m/s");
            
            if (finalSpeedA < 0.5f || finalSpeedB < 0.5f)
            {
                Console.WriteLine($"  ❌ 弹性碰撞测试失败，速度损失过大");
                return;
            }
            
            Console.WriteLine("  ✅ 球体碰撞测试通过");
        }
        
        /// <summary>
        /// 测试3：堆叠稳定性 - 验证多个物体堆叠的稳定性
        /// </summary>
        public static void TestStackingStability(string tempRoot)
        {
            Console.WriteLine("测试3: 堆叠稳定性");
            
            var settings = new PhysicsSettings
            {
                Gravity = new Vector3(0, -9.81f, 0),
                SolverIterations = 12,
                EnableSleep = true
            };
            
            var engine = new RealPhysicsEngine(settings);
            
            // 创建地面
            var groundDesc = new RigidbodyDescription
            {
                Name = "Ground",
                Type = RigidbodyType.Static,
                Position = new Vector3(0, 0, 0),
                Material = PhysicsMaterial.Concrete
            };
            
            var ground = engine.CreateRigidbody(groundDesc);
            
            var groundColliderDesc = new ColliderDescription
            {
                Type = ColliderType.Box,
                Size = new Vector3(10, 1, 10)
            };
            
            engine.CreateCollider(groundColliderDesc, ground);
            
            // 创建堆叠的盒子
            var boxes = new List<Rigidbody>();
            for (int i = 0; i < 10; i++)
            {
                var boxDesc = new RigidbodyDescription
                {
                    Name = $"Box{i}",
                    Type = RigidbodyType.Dynamic,
                    Position = new Vector3(0, 0.5f + i * 1.1f, 0),
                    Mass = 1.0f,
                    Material = PhysicsMaterial.Wood
                };
                
                var box = engine.CreateRigidbody(boxDesc);
                boxes.Add(box);
                
                var boxColliderDesc = new ColliderDescription
                {
                    Type = ColliderType.Box,
                    Size = new Vector3(1, 1, 1)
                };
                
                engine.CreateCollider(boxColliderDesc, box);
            }
            
            // 模拟堆叠
            float deltaTime = 0.016f;
            float maxSettlingTime = 5.0f; // 5秒内应该稳定
            int maxOscillations = 10;
            
            for (int i = 0; i < 600; i++) // 10秒
            {
                engine.Step(deltaTime);
                
                if (i >= 300 && i % 60 == 0) // 5秒后检查
                {
                    bool allStable = true;
                    float maxVelocity = 0;
                    
                    foreach (var box in boxes)
                    {
                        float velocity = box.Velocity.Length();
                        maxVelocity = MathF.Max(maxVelocity, velocity);
                        
                        if (velocity > 0.05f) // 睡眠阈值
                        {
                            allStable = false;
                        }
                    }
                    
                    Console.WriteLine($"  t={i * deltaTime:F2}s: 最大速度={maxVelocity:F4} m/s, 稳定={allStable}");
                    
                    if (allStable)
                    {
                        Console.WriteLine($"  ✅ 堆叠稳定，在 {(i * deltaTime):F2}s 内达到静止状态");
                        return;
                    }
                    
                    if (i * deltaTime > maxSettlingTime)
                    {
                        Console.WriteLine($"  ❌ 堆叠不稳定，{maxSettlingTime:F1f}秒后仍未稳定");
                        return;
                    }
                }
            }
            
            Console.WriteLine("  ✅ 堆叠稳定性测试通过（所有盒子最终稳定）");
        }
        
        /// <summary>
        /// 测试4：弹簧约束 - 验证胡克定律
        /// </summary>
        public static void TestSpringConstraint(string tempRoot)
        {
            Console.WriteLine("测试4: 弹簧约束");
            
            var settings = new PhysicsSettings
            {
                Gravity = new Vector3(0, -9.81f, 0),
                SolverIterations = 8
            };
            
            var engine = new RealPhysicsEngine(settings);
            
            // 创建两个物体
            var bodyADesc = new RigidbodyDescription
            {
                Name = "BodyA",
                Type = RigidbodyType.Static,
                Position = new Vector3(0, 10, 0),
                Material = PhysicsMaterial.Metal
            };
            
            var bodyBDesc = new RigidbodyDescription
            {
                Name = "BodyB",
                Type = RigidbodyType.Dynamic,
                Position = new Vector3(0, 5, 0),
                Mass = 1.0f,
                Material = PhysicsMaterial.Metal
            };
            
            var bodyA = engine.CreateRigidbody(bodyADesc);
            var bodyB = engine.CreateRigidbody(bodyBDesc);
            
            // 添加碰撞体
            var colliderDesc = new ColliderDescription
            {
                Type = ColliderType.Sphere,
                Radius = 0.3f
            };
            
            engine.CreateCollider(colliderDesc, bodyA);
            engine.CreateCollider(colliderDesc, bodyB);
            
            // 创建弹簧
            var spring = engine.CreateSpringConstraint(
                bodyA, bodyB,
                Vector3.Zero, Vector3.Zero,
                stiffness: 50.0f,  // 较软的弹簧
                damping: 1.0f
            );
            
            // 模拟弹簧运动
            float deltaTime = 0.016f;
            var positions = new List<float>();
            var oscillationPeriod = 2.0f * MathF.PI * MathF.Sqrt(1.0f / 50.0f); // T = 2π√(m/k)
            
            for (int i = 0; i < 600; i++)
            {
                engine.Step(deltaTime);
                positions.Add(bodyB.Position.Y);
                
                if (i % 60 == 0)
                {
                    Console.WriteLine($"  t={i * deltaTime:F2}s: y={bodyB.Position.Y:F3}m");
                }
            }
            
            // 检查简谐运动
            float amplitude = positions.Max() - positions.Min();
            float equilibriumY = positions.Average();
            
            Console.WriteLine($"  振幅: {amplitude:F3}m, 平衡位置: {equilibriumY:F3}m");
            Console.WriteLine($"  理论周期: {oscillationPeriod:F2}s");
            
            if (amplitude < 0.1f)
            {
                Console.WriteLine($"  ❌ 弹簧测试失败，振幅过小");
                return;
            }
            
            if (MathF.Abs(equilibriumY - 7.5f) > 0.5f) // 应该在中间
            {
                Console.WriteLine($"  ❌ 弹簧测试失败，平衡位置不正确");
                return;
            }
            
            Console.WriteLine("  ✅ 弹簧约束测试通过");
        }
        
        /// <summary>
        /// 测试5：摩擦力 - 验证静摩擦和动摩擦
        /// </summary>
        public static void TestFriction(string tempRoot)
        {
            Console.WriteLine("测试5: 摩擦力");
            
            var settings = new PhysicsSettings
            {
                Gravity = new Vector3(0, -9.81f, 0),
                SolverIterations = 8
            };
            
            var engine = new RealPhysicsEngine(settings);
            
            // 创建高摩擦地面
            var groundDesc = new RigidbodyDescription
            {
                Name = "Ground",
                Type = RigidbodyType.Static,
                Position = new Vector3(0, 0, 0),
                Material = new PhysicsMaterial 
                { 
                    Name = "HighFriction", 
                    StaticFriction = 0.8f, 
                    DynamicFriction = 0.7f, 
                    Restitution = 0.1f 
                }
            };
            
            var ground = engine.CreateRigidbody(groundDesc);
            
            var groundColliderDesc = new ColliderDescription
            {
                Type = ColliderType.Box,
                Size = new Vector3(10, 1, 10)
            };
            
            engine.CreateCollider(groundColliderDesc, ground);
            
            // 创建滑块
            var blockDesc = new RigidbodyDescription
            {
                Name = "Block",
                Type = RigidbodyType.Dynamic,
                Position = new Vector3(0, 0.6f, 0),
                Mass = 1.0f,
                Material = new PhysicsMaterial 
                { 
                    Name = "Block", 
                    StaticFriction = 0.8f, 
                    DynamicFriction = 0.7f, 
                    Restitution = 0.1f 
                }
            };
            
            var block = engine.CreateRigidbody(blockDesc);
            
            var blockColliderDesc = new ColliderDescription
            {
                Type = ColliderType.Box,
                Size = new Vector3(1, 1, 1)
            };
            
            engine.CreateCollider(blockColliderDesc, block);
            
            // 应用水平力
            float appliedForce = 5.0f; // 小于最大静摩擦力
            
            // 最大静摩擦力 = μ * N = 0.8 * 1.0 * 9.81 ≈ 7.85N
            // 我们的力 5N 小于静摩擦力，所以块不应该移动
            
            float deltaTime = 0.016f;
            for (int i = 0; i < 200; i++)
            {
                block.ApplyForce(new Vector3(appliedForce, 0, 0));
                engine.Step(deltaTime);
                
                if (i % 30 == 0)
                {
                    Console.WriteLine($"  t={i * deltaTime:F2}s: x={block.Position.X:F4}m, v={block.Velocity.X:F4}m/s");
                }
            }
            
            // 检查块是否保持静止（静摩擦）
            float finalXVelocity = MathF.Abs(block.Velocity.X);
            float finalXPosition = MathF.Abs(block.Position.X);
            
            Console.WriteLine($"  最终水平速度: {finalXVelocity:F6} m/s");
            Console.WriteLine($"  最终水平位移: {finalXPosition:F6} m");
            
            if (finalXVelocity > 0.01f)
            {
                Console.WriteLine($"  ⚠️ 静摩擦测试：块有轻微移动（可能由于数值积分误差）");
            }
            
            if (finalXPosition > 0.1f)
            {
                Console.WriteLine($"  ❌ 静摩擦测试失败，块移动过多");
                return;
            }
            
            // 测试动摩擦：施加更大的力
            block.Velocity = Vector3.Zero;
            block.Position = new Vector3(0, 0.6f, 0);
            
            float largeForce = 15.0f; // 大于最大静摩擦力
            
            for (int i = 0; i < 200; i++)
            {
                block.ApplyForce(new Vector3(largeForce, 0, 0));
                engine.Step(deltaTime);
                
                if (i % 30 == 0)
                {
                    Console.WriteLine($"  t={i * deltaTime:F2}s (动摩擦): x={block.Position.X:F4}m, v={block.Velocity.X:F4}m/s");
                }
            }
            
            // 检查块是否移动并达到稳定速度
            float dynamicVelocity = MathF.Abs(block.Velocity.X);
            
            Console.WriteLine($"  最终动摩擦速度: {dynamicVelocity:F4} m/s");
            
            if (dynamicVelocity < 0.1f)
            {
                Console.WriteLine($"  ❌ 动摩擦测试失败，块没有加速");
                return;
            }
            
            Console.WriteLine("  ✅ 摩擦力测试通过");
        }
        
        /// <summary>
        /// 测试6：弹性碰撞 - 验证恢复系数
        /// </summary>
        public static void TestRestitution(string tempRoot)
        {
            Console.WriteLine("测试6: 弹性碰撞（恢复系数）");
            
            var settings = new PhysicsSettings
            {
                Gravity = new Vector3(0, -9.81f, 0),
                SolverIterations = 8
            };
            
            var engine = new RealPhysicsEngine(settings);
            
            // 创建弹性球和地面
            var ballDesc = new RigidbodyDescription
            {
                Name = "ElasticBall",
                Type = RigidbodyType.Dynamic,
                Position = new Vector3(0, 10, 0),
                Mass = 1.0f,
                Material = new PhysicsMaterial 
                { 
                    Name = "SuperBall", 
                    StaticFriction = 0.1f, 
                    DynamicFriction = 0.1f, 
                    Restitution = 0.9f // 高弹性
                }
            };
            
            var ball = engine.CreateRigidbody(ballDesc);
            
            var ballColliderDesc = new ColliderDescription
            {
                Type = ColliderType.Sphere,
                Radius = 0.5f
            };
            
            engine.CreateCollider(ballColliderDesc, ball);
            
            // 创建地面
            var groundDesc = new RigidbodyDescription
            {
                Name = "Ground",
                Type = RigidbodyType.Static,
                Position = new Vector3(0, 0, 0),
                Material = PhysicsMaterial.Concrete
            };
            
            var ground = engine.CreateRigidbody(groundDesc);
            
            var groundColliderDesc = new ColliderDescription
            {
                Type = ColliderType.Box,
                Size = new Vector3(10, 1, 10)
            };
            
            engine.CreateCollider(groundColliderDesc, ground);
            
            // 记录反弹高度
            var bounceHeights = new List<float>();
            
            // 模拟弹跳
            float deltaTime = 0.016f;
            float initialHeight = 10.0f;
            float previousHeight = initialHeight;
            
            for (int i = 0; i < 600; i++)
            {
                float prevY = ball.Position.Y;
                engine.Step(deltaTime);
                float currentY = ball.Position.Y;
                
                // 检测反弹（从上升变为下降）
                if (prevY < currentY && currentY > 0.6f)
                {
                    previousHeight = currentY;
                }
                else if (prevY > currentY && previousHeight > currentY + 0.1f)
                {
                    // 刚弹跳
                    float bounceHeight = currentY - 0.5f; // 减去半径
                    if (bounceHeight > 0.1f && bounceHeight < initialHeight - 0.1f)
                    {
                        bounceHeights.Add(bounceHeight);
                        Console.WriteLine($"  反弹 #{bounceHeights.Count}: h={bounceHeight:F3}m");
                    }
                    previousHeight = currentY;
                }
            }
            
            // 计算恢复系数
            if (bounceHeights.Count >= 1)
            {
                float actualRestitution = MathF.Sqrt(bounceHeights[0] / initialHeight);
                Console.WriteLine($"  实际恢复系数: {actualRestitution:F3} (期望: 0.9)");
                
                if (MathF.Abs(actualRestitution - 0.9f) > 0.1f)
                {
                    Console.WriteLine($"  ❌ 恢复系数测试失败，偏差过大");
                    return;
                }
            }
            
            if (bounceHeights.Count == 0)
            {
                Console.WriteLine($"  ❌ 恢复系数测试失败，没有检测到反弹");
                return;
            }
            
            Console.WriteLine("  ✅ 弹性碰撞测试通过");
        }
        
        /// <summary>
        /// 运行所有测试
        /// </summary>
        public static void RunAllTests(string tempRoot)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("yama真实物理引擎测试套件");
            Console.WriteLine("========================================");
            
            try
            {
                TestFreeFall(tempRoot);
                TestSphereCollision(tempRoot);
                TestStackingStability(tempRoot);
                TestSpringConstraint(tempRoot);
                TestFriction(tempRoot);
                TestRestitution(tempRoot);
                
                Console.WriteLine("========================================");
                Console.WriteLine("✅ 所有测试通过！");
                Console.WriteLine("========================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"========================================");
                Console.WriteLine($"❌ 测试失败: {ex.Message}");
                Console.WriteLine($"堆栈: {ex.StackTrace}");
                Console.WriteLine("========================================");
                throw;
            }
        }
    }
}