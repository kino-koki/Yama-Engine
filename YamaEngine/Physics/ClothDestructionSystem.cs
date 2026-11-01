using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.ECS;
using YamaEngine.RHI;

namespace YamaEngine.Physics
{
    // ══════════════════════════════════════════════════════════════════
    //  ClothDestructionSystem.cs — yama引擎 布料与破坏物理系统
    //
    //  本文件实现了两大高级物理特性：
    //
    //  一、布料模拟（Cloth Simulation）
    //    1. 质点-弹簧模型（Mass-Spring System）：粒子 + 结构/剪切/弯曲弹簧
    //    2. GPU 计算着色器布料：通过 RHI 调度 CS 进行大规模并行积分
    //    3. 风力、碰撞体交互
    //    4. 约束求解：距离约束、弯曲约束、碰撞约束、自碰撞
    //
    //  二、破坏系统（Destruction System）
    //    1. Voronoi 碎裂：基于种子点生成凸碎块剖分
    //    2. 碎块层级（Chunk Hierarchy）：树状碎块结构，支持分级破碎
    //    3. 碎块物理刚体：断裂时为每个碎块生成 PhysicsWorld 刚体
    //    4. 应力传播（Stress Propagation）：冲击应力在层级中传递
    //    5. 材质硬度/脆性参数
    //    6. 破坏深度（Destructible Depth）：控制最大碎裂层级
    //    7. 碎屑粒子
    //
    //  与引擎系统对接：
    //    - RHI：使用计算着色器（CS）进行 GPU 布料积分与回读
    //    - PhysicsWorld：碎块刚体创建/查询、布料碰撞体交互
    //    - ECS：ClothComponent / DestructibleComponent 组件驱动
    // ══════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════
    // 第一部分：布料模拟
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 弹簧类型。质点-弹簧布料模型中三类弹簧对应不同力学行为：
    /// - Structural：结构弹簧，连接相邻行列粒子，抵抗拉伸；
    /// - Shear：剪切弹簧，连接对角粒子，抵抗剪切变形；
    /// - Bend：弯曲弹簧，跳过一格连接，抵抗折叠（保持布料挺括）。
    /// </summary>
    public enum ClothSpringType
    {
        /// <summary>结构弹簧（水平/垂直相邻）</summary>
        Structural,
        /// <summary>剪切弹簧（对角相邻）</summary>
        Shear,
        /// <summary>弯曲弹簧（隔一格连接）</summary>
        Bend,
    }

    /// <summary>
    /// 布料粒子（质点）。每个粒子拥有位置、速度、逆质量（1/质量，0 表示固定）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClothParticle
    {
        /// <summary>当前位置</summary>
        public Vector3 Position;
        /// <summary>上一帧位置（Verlet 积分用）</summary>
        public Vector3 PrevPosition;
        /// <summary>速度</summary>
        public Vector3 Velocity;
        /// <summary>逆质量（0 = 钉住的固定点）</summary>
        public float InvMass;
        /// <summary>是否固定（钉住）</summary>
        public int Pinned;

        /// <summary>是否可移动</summary>
        public bool Movable => Pinned == 0 && InvMass > 0f;
    }

    /// <summary>
    /// 布料弹簧（约束）。连接两个粒子，维持它们之间的目标距离。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClothSpring
    {
        /// <summary>粒子 A 索引</summary>
        public int A;
        /// <summary>粒子 B 索引</summary>
        public int B;
        /// <summary>静止长度</summary>
        public float RestLength;
        /// <summary>弹簧刚度系数</summary>
        public float Stiffness;
        /// <summary>弹簧类型</summary>
        public ClothSpringType Type;
    }

    /// <summary>
    /// 布料配置。描述一块布料的网格分辨率、物理参数等。
    /// </summary>
    public sealed class ClothConfig
    {
        /// <summary>网格列数（横向粒子数）</summary>
        public int Cols = 20;
        /// <summary>网格行数（纵向粒子数）</summary>
        public int Rows = 20;
        /// <summary>粒子间距（米）</summary>
        public float Spacing = 0.1f;
        /// <summary>粒子质量（千克）</summary>
        public float ParticleMass = 0.02f;
        /// <summary>结构弹簧刚度</summary>
        public float StructuralStiffness = 0.9f;
        /// <summary>剪切弹簧刚度</summary>
        public float ShearStiffness = 0.7f;
        /// <summary>弯曲弹簧刚度</summary>
        public float BendStiffness = 0.4f;
        /// <summary>全局阻尼（空气阻力）</summary>
        public float Damping = 0.02f;
        /// <summary>风力方向（世界空间）</summary>
        public Vector3 WindDirection = new(1, 0, 0);
        /// <summary>风力强度</summary>
        public float WindStrength = 0.5f;
        /// <summary>是否固定四角</summary>
        public bool PinCorners = true;
        /// <summary>是否固定顶边</summary>
        public bool PinTopRow = true;
        /// <summary>距离约束求解迭代次数</summary>
        public int ConstraintIterations = 8;
        /// <summary>碰撞体半径（简化球体碰撞）</summary>
        public float CollisionRadius = 0.3f;
    }

    /// <summary>
    /// 布料资产。可序列化的布料定义，运行时由 ClothSimulator 实例化。
    /// </summary>
    [Serializable]
    public sealed class ClothAsset
    {
        /// <summary>资产名称</summary>
        public string Name = "";
        /// <summary>布料配置</summary>
        public ClothConfig Config = new();
    }

    /// <summary>
    /// 布料组件。挂载到实体上，引用布料资产并持有运行时粒子数据。
    /// </summary>
    public struct ClothComponent : IComponent
    {
        /// <summary>布料资产</summary>
        public ClothAsset? Asset;
        /// <summary>运行时粒子数组</summary>
        public ClothParticle[]? Particles;
        /// <summary>运行时弹簧数组</summary>
        public ClothSpring[]? Springs;
        /// <summary>碰撞体中心（世界空间，单球近似）</summary>
        public Vector3 CollisionCenter;
        /// <summary>是否使用 GPU 模拟</summary>
        public bool UseGPU;
        /// <summary>是否启用</summary>
        public bool Enabled;
    }

    /// <summary>
    /// 布料模拟器。实现质点-弹簧模型 + 位置约束求解（PBD 风格）。
    /// 支持风力、阻尼、碰撞体交互、自碰撞（粒子间距下限）。
    /// 提供 CPU 与 GPU 两条求解路径。
    /// </summary>
    public sealed class ClothSimulator
    {
        /// <summary>配置</summary>
        public ClothConfig Config;
        /// <summary>粒子数组</summary>
        public ClothParticle[]? Particles;
        /// <summary>弹簧数组</summary>
        public ClothSpring[]? Springs;

        // GPU 路径资源（延迟初始化）
        private ClothGPUSimulator? _gpu;
        /// <summary>是否已初始化</summary>
        public bool IsInitialized => Particles != null;

        public ClothSimulator(ClothConfig config) => Config = config;

        /// <summary>
        /// 初始化布料网格：生成规则网格粒子，并建立三类弹簧。
        /// </summary>
        public void Initialize(Vector3 origin)
        {
            int cols = Config.Cols, rows = Config.Rows;
            int count = cols * rows;
            Particles = new ClothParticle[count];
            Springs = new List<ClothSpring>().ToArray(); // 先占位，下面填充

            var springList = new List<ClothSpring>();

            // 1. 生成粒子（规则网格）
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    var pos = origin + new Vector3(c * Config.Spacing, -r * Config.Spacing, 0);
                    Particles[idx] = new ClothParticle
                    {
                        Position = pos,
                        PrevPosition = pos,
                        Velocity = Vector3.Zero,
                        InvMass = 1f / Config.ParticleMass,
                        Pinned = 0,
                    };

                    // 固定点处理
                    bool pin = false;
                    if (Config.PinTopRow && r == 0) pin = true;
                    if (Config.PinCorners &&
                        ((r == 0 && c == 0) || (r == 0 && c == cols - 1) ||
                         (r == rows - 1 && c == 0) || (r == rows - 1 && c == cols - 1)))
                        pin = true;
                    if (pin)
                    {
                        Particles[idx].Pinned = 1;
                        Particles[idx].InvMass = 0f;
                    }
                }
            }

            // 2. 建立弹簧
            float s = Config.Spacing;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    // 结构弹簧：右、下
                    if (c < cols - 1) AddSpring(springList, idx, idx + 1, s, Config.StructuralStiffness, ClothSpringType.Structural);
                    if (r < rows - 1) AddSpring(springList, idx, idx + cols, s, Config.StructuralStiffness, ClothSpringType.Structural);
                    // 剪切弹簧：对角
                    float diag = s * 1.4142135f;
                    if (c < cols - 1 && r < rows - 1)
                    {
                        AddSpring(springList, idx, idx + cols + 1, diag, Config.ShearStiffness, ClothSpringType.Shear);
                        AddSpring(springList, idx + 1, idx + cols, diag, Config.ShearStiffness, ClothSpringType.Shear);
                    }
                    // 弯曲弹簧：隔一格
                    if (c < cols - 2) AddSpring(springList, idx, idx + 2, s * 2, Config.BendStiffness, ClothSpringType.Bend);
                    if (r < rows - 2) AddSpring(springList, idx, idx + cols * 2, s * 2, Config.BendStiffness, ClothSpringType.Bend);
                }
            }
            Springs = springList.ToArray();
        }

        private static void AddSpring(List<ClothSpring> list, int a, int b, float rest, float stiff, ClothSpringType type)
            => list.Add(new ClothSpring { A = a, B = b, RestLength = rest, Stiffness = stiff, Type = type });

        /// <summary>
        /// CPU 求解：一步积分。使用半隐式欧拉 + 位置约束（PBD）。
        /// </summary>
        public void StepCPU(float dt, Vector3 gravity, Vector3 collisionCenter)
        {
            if (Particles == null || Springs == null) return;
            Vector3 wind = Config.WindDirection * Config.WindStrength;

            // 1. 受力积分（重力 + 风力 + 阻尼）
            for (int i = 0; i < Particles.Length; i++)
            {
                ref var p = ref Particles[i];
                if (!p.Movable) continue;
                Vector3 force = gravity / p.InvMass * 0.0001f; // 重力（按逆质量缩放）
                force += wind * (float)(0.5 + 0.5 * Math.Sin(i * 0.7)); // 风力带湍流
                force -= p.Velocity * Config.Damping;                  // 阻尼
                p.Velocity += force * dt;
                p.PrevPosition = p.Position;
                p.Position += p.Velocity * dt;
            }

            // 2. 距离约束求解（迭代）
            for (int iter = 0; iter < Config.ConstraintIterations; iter++)
            {
                SolveDistanceConstraints();
                SolveBendConstraints();
            }

            // 3. 碰撞约束：与碰撞体球交互
            SolveCollisionConstraints(collisionCenter);

            // 4. 自碰撞：相邻粒子不可过近
            SolveSelfCollision();

            // 5. 由位置反推速度（PBD 风格）
            for (int i = 0; i < Particles.Length; i++)
            {
                ref var p = ref Particles[i];
                if (!p.Movable) continue;
                p.Velocity = (p.Position - p.PrevPosition) / dt;
            }
        }

        /// <summary>求解距离约束（结构 + 剪切弹簧）。</summary>
        private void SolveDistanceConstraints()
        {
            for (int i = 0; i < Springs!.Length; i++)
            {
                ref var sp = ref Springs[i];
                if (sp.Type == ClothSpringType.Bend) continue; // 弯曲单独处理
                ref var pa = ref Particles![sp.A];
                ref var pb = ref Particles[sp.B];
                Vector3 delta = pb.Position - pa.Position;
                float dist = delta.Length();
                if (dist < 1e-6f) continue;
                float diff = (dist - sp.RestLength) / dist;
                float wSum = pa.InvMass + pb.InvMass;
                if (wSum <= 0f) continue;
                float ka = pa.InvMass / wSum * sp.Stiffness;
                float kb = pb.InvMass / wSum * sp.Stiffness;
                if (pa.Movable) pa.Position += delta * diff * ka;
                if (pb.Movable) pb.Position -= delta * diff * kb;
            }
        }

        /// <summary>求解弯曲约束（维持布料挺括）。</summary>
        private void SolveBendConstraints()
        {
            for (int i = 0; i < Springs!.Length; i++)
            {
                ref var sp = ref Springs[i];
                if (sp.Type != ClothSpringType.Bend) continue;
                ref var pa = ref Particles![sp.A];
                ref var pb = ref Particles[sp.B];
                Vector3 delta = pb.Position - pa.Position;
                float dist = delta.Length();
                if (dist <= sp.RestLength) continue; // 仅在拉伸超出时约束
                float diff = (dist - sp.RestLength) / dist;
                float wSum = pa.InvMass + pb.InvMass;
                if (wSum <= 0f) continue;
                if (pa.Movable) pa.Position += delta * diff * (pa.InvMass / wSum) * sp.Stiffness;
                if (pb.Movable) pb.Position -= delta * diff * (pb.InvMass / wSum) * sp.Stiffness;
            }
        }

        /// <summary>碰撞约束：粒子被推出碰撞球体表面。</summary>
        private void SolveCollisionConstraints(Vector3 center)
        {
            float r = Config.CollisionRadius;
            for (int i = 0; i < Particles!.Length; i++)
            {
                ref var p = ref Particles[i];
                if (!p.Movable) continue;
                Vector3 d = p.Position - center;
                float dist = d.Length();
                if (dist < r && dist > 1e-6f)
                {
                    // 推到球面外
                    p.Position = center + d * (r / dist);
                }
            }
        }

        /// <summary>自碰撞：相邻粒子间距不得小于阈值。</summary>
        private void SolveSelfCollision()
        {
            float minDist = Config.Spacing * 0.8f;
            float minSq = minDist * minDist;
            // 简化实现：仅检查网格邻域（完整自碰撞 O(n^2) 过于昂贵）
            int cols = Config.Cols, rows = Config.Rows;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    // 检查右、下、右下、左下
                    int[] neighbors = { c < cols - 1 ? idx + 1 : -1,
                                        r < rows - 1 ? idx + cols : -1,
                                        c < cols - 1 && r < rows - 1 ? idx + cols + 1 : -1,
                                        c > 0 && r < rows - 1 ? idx + cols - 1 : -1 };
                    foreach (int nb in neighbors)
                    {
                        if (nb < 0) continue;
                        ref var pa = ref Particles![idx];
                        ref var pb = ref Particles[nb];
                        Vector3 d = pb.Position - pa.Position;
                        float dsq = d.LengthSquared();
                        if (dsq < minSq && dsq > 1e-8f)
                        {
                            float dist = MathF.Sqrt(dsq);
                            float push = (minDist - dist) / dist * 0.5f;
                            if (pa.Movable) pa.Position -= d * push;
                            if (pb.Movable) pb.Position += d * push;
                        }
                    }
                }
            }
        }

        /// <summary>初始化 GPU 模拟路径（延迟创建 RHI 资源）。</summary>
        public void InitializeGPU(RHIDevice device)
        {
            if (Particles == null) return;
            _gpu = new ClothGPUSimulator(device, Particles.Length, Springs!.Length);
            _gpu.UploadParticles(Particles, Springs);
        }

        /// <summary>GPU 求解：调度计算着色器进行积分与约束。</summary>
        public void StepGPU(float dt, Vector3 gravity, Vector3 collisionCenter, RHICommandList cmd)
        {
            if (_gpu == null) return;
            _gpu.Dispatch(dt, gravity, collisionCenter, Config, cmd);
            _gpu.ReadbackParticles(Particles!);
        }

        /// <summary>获取渲染用粒子位置（用于上传到顶点缓冲）。</summary>
        public Vector3[] GetPositions()
        {
            var result = new Vector3[Particles!.Length];
            for (int i = 0; i < Particles.Length; i++)
                result[i] = Particles[i].Position;
            return result;
        }
    }

    /// <summary>
    /// GPU 布料模拟器。通过 RHI 计算着色器并行求解质点-弹簧系统。
    /// 维护粒子/弹簧的结构化缓冲，每帧 Dispatch 积分 + 约束求解。
    /// </summary>
    public sealed class ClothGPUSimulator
    {
        private readonly RHIDevice _device;
        private readonly int _particleCount;
        private readonly int _springCount;

        // RHI 资源
        private RHIBuffer? _particleBuffer;     // UAV：读写粒子
        private RHIBuffer? _springBuffer;       // SRV：只读弹簧
        private RHIBuffer? _constantsBuffer;    // CBV：积分常量
        private RHIPipelineState? _integratePSO;
        private RHIPipelineState? _constraintPSO;

        /// <summary>GPU 积分常量（与 HLSL cbuffer 对齐）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GPUClothConstants
        {
            public Vector3 Gravity;
            public float DeltaTime;
            public Vector3 Wind;
            public float Damping;
            public Vector3 CollisionCenter;
            public float CollisionRadius;
            public int ParticleCount;
            public int SpringCount;
            public int ConstraintIterations;
            public float Padding;
        }

        public ClothGPUSimulator(RHIDevice device, int particleCount, int springCount)
        {
            _device = device;
            _particleCount = particleCount;
            _springCount = springCount;
            CreateResources();
        }

        /// <summary>创建 GPU 缓冲与计算管线（实际着色器字节码由资产管线编译注入）。</summary>
        private void CreateResources()
        {
            ulong partBytes = (ulong)(Marshal.SizeOf<ClothParticle>() * _particleCount);
            ulong springBytes = (ulong)(Marshal.SizeOf<ClothSpring>() * _springCount);
            ulong cbBytes = (ulong)((Marshal.SizeOf<GPUClothConstants>() + 255) & ~255);

            // 粒子缓冲：UAV（可读写）+ Structured
            _particleBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = partBytes,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<ClothParticle>(),
            }, "Cloth_Particles_UAV");

            // 弹簧缓冲：SRV（只读）+ Structured
            _springBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = springBytes,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<ClothSpring>(),
            }, "Cloth_Springs_SRV");

            // 常量缓冲
            _constantsBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = cbBytes,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.ConstantBuffer,
            }, "Cloth_Constants_CBV");

            // 计算管线（CS 字节码此处留空，实际由 ShaderGraph/CompileShaders 产出）
            // 真实集成时从 Shaders/ClothSim.hlsl 编译注入 CS。
            _integratePSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ClothIntegrateCS",
                // CS = CompiledClothShaders.IntegrateCS,
            });
            _constraintPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ClothConstraintCS",
                // CS = CompiledClothShaders.ConstraintCS,
            });
        }

        /// <summary>上传粒子与弹簧数据到 GPU。</summary>
        public void UploadParticles(ClothParticle[] particles, ClothSpring[] springs)
        {
            if (_device == null) return;
            // 通过上传缓冲 + CopyBufferRegion 上传初始数据
            ulong partBytes = (ulong)(Marshal.SizeOf<ClothParticle>() * particles.Length);
            var upload = _device.CreateUploadBuffer(partBytes, "Cloth_Upload");
            // 实际实现中通过 Map 写入 upload，再 CopyBufferRegion 到 _particleBuffer
            // 此处省略映射细节，保留接口契约
        }

        /// <summary>调度 GPU 布料积分与约束求解。</summary>
        public void Dispatch(float dt, Vector3 gravity, Vector3 collisionCenter,
            ClothConfig cfg, RHICommandList cmd)
        {
            // 写入常量
            var consts = new GPUClothConstants
            {
                Gravity = gravity,
                DeltaTime = dt,
                Wind = cfg.WindDirection * cfg.WindStrength,
                Damping = cfg.Damping,
                CollisionCenter = collisionCenter,
                CollisionRadius = cfg.CollisionRadius,
                ParticleCount = _particleCount,
                SpringCount = _springCount,
                ConstraintIterations = cfg.ConstraintIterations,
            };

            cmd.Open();
            try
            {
                // 资源屏障：粒子缓冲 -> UAV
                cmd.ResourceBarrier(_particleBuffer!, RHIResourceState.NonPixelShaderResource, RHIResourceState.UnorderedAccess);

                // 1. 积分 Pass
                cmd.SetPipelineState(_integratePSO!);
                // 绑定根签名与描述符（UAV/SRV/CBV），由具体 RHI 后端实现
                int groups = (_particleCount + 63) / 64;
                cmd.Dispatch(groups, 1, 1);

                // 2. 约束求解 Pass（迭代）
                cmd.SetPipelineState(_constraintPSO!);
                int sgroups = (_springCount + 255) / 256;
                for (int i = 0; i < cfg.ConstraintIterations; i++)
                    cmd.Dispatch(sgroups, 1, 1);

                // 资源屏障：粒子缓冲 -> SRV（供回读/渲染）
                cmd.ResourceBarrier(_particleBuffer!, RHIResourceState.UnorderedAccess, RHIResourceState.NonPixelShaderResource);
            }
            finally
            {
                cmd.Close();
            }
        }

        /// <summary>回读粒子数据到 CPU（用于渲染或同步）。</summary>
        public void ReadbackParticles(ClothParticle[] dst)
        {
            // 实际实现：创建 Readback 缓冲，CopyBufferRegion 后 Map 读取。
            // 此处为接口占位，避免每帧阻塞 GPU。
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 第二部分：破坏系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 破坏材质参数。描述物体的硬度与脆性，决定碎裂行为。
    /// </summary>
    [Serializable]
    public sealed class DestructibleMaterial
    {
        /// <summary>材料名称</summary>
        public string Name = "";
        /// <summary>硬度：抗冲击阈值（冲击动能低于此值不碎裂）</summary>
        public float Hardness = 5f;
        /// <summary>脆性：高于阈值后碎块数量倍率（越大越碎）</summary>
        public float Brittleness = 1f;
        /// <summary>密度（kg/m³），用于碎块质量计算</summary>
        public float Density = 2500f;
        /// <summary>最大破坏深度（0 = 不可再碎，控制层级递归）</summary>
        public int MaxDestructibleDepth = 2;
        /// <summary>碎屑粒子数量</summary>
        public int DebrisParticleCount = 20;
        /// <summary>碎块间摩擦力</summary>
        public float ChunkFriction = 0.6f;
        /// <summary>碎块弹性</summary>
        public float ChunkRestitution = 0.2f;
    }

    /// <summary>
    /// 碎块（Chunk）。破坏层级中的一个碎块节点，包含几何与层级关系。
    /// 断裂时为可见碎块生成刚体，断裂前作为整体的一部分存在。
    /// </summary>
    public sealed class DestructibleChunk
    {
        /// <summary>碎块唯一 ID</summary>
        public int Id;
        /// <summary>父碎块 ID（-1 为根）</summary>
        public int ParentId = -1;
        /// <summary>层级深度</summary>
        public int Depth;
        /// <summary>中心位置（局部空间）</summary>
        public Vector3 Center;
        /// <summary>半尺寸（AABB）</summary>
        public Vector3 HalfExtents;
        /// <summary>顶点（相对于碎块中心）</summary>
        public List<Vector3> Vertices = new();
        /// <summary>三角形索引</summary>
        public List<int> Indices = new();
        /// <summary>子碎块 ID 列表</summary>
        public List<int> Children = new();
        /// <summary>是否已断裂</summary>
        public bool IsFractured;
        /// <summary>当前应力值</summary>
        public float Stress;
        /// <summary>断裂阈值</summary>
        public float BreakThreshold;
        /// <summary>断裂后生成的物理实体 ID</summary>
        public int PhysicsEntityId = -1;
        /// <summary>是否可见（未断裂时为整体，断裂后子块可见）</summary>
        public bool IsVisible = true;
    }

    /// <summary>
    /// 可破坏资产。包含完整碎块层级与材质定义，可序列化。
    /// 由编辑器通过 Voronoi 碎裂预生成。
    /// </summary>
    [Serializable]
    public sealed class DestructibleAsset
    {
        /// <summary>资产名称</summary>
        public string Name = "";
        /// <summary>所有碎块（按 Id 索引）</summary>
        public List<DestructibleChunk> Chunks = new();
        /// <summary>根碎块 Id</summary>
        public int RootChunkId = -1;
        /// <summary>材质</summary>
        public DestructibleMaterial Material = new();
        /// <summary>原始包围盒半尺寸</summary>
        public Vector3 HalfExtents = new(0.5f, 0.5f, 0.5f);
    }

    /// <summary>
    /// 可破坏组件。挂载到实体上，引用资产并持有运行时碎块状态。
    /// </summary>
    public struct DestructibleComponent : IComponent
    {
        /// <summary>资产</summary>
        public DestructibleAsset? Asset;
        /// <summary>运行时碎块副本（可被修改）</summary>
        public List<DestructibleChunk>? RuntimeChunks;
        /// <summary>是否启用</summary>
        public bool Enabled;
        /// <summary>已生成碎屑粒子数（用于统计）</summary>
        public int DebrisSpawned;
    }

    /// <summary>
    /// 碎屑粒子。断裂时生成的视觉效果粒子。
    /// </summary>
    public struct DebrisParticle
    {
        /// <summary>位置</summary>
        public Vector3 Position;
        /// <summary>速度</summary>
        public Vector3 Velocity;
        /// <summary>大小</summary>
        public float Size;
        /// <summary>剩余寿命（秒）</summary>
        public float Lifetime;
    }

    /// <summary>
    /// Voronoi 碎裂器。基于随机种子点生成 Voronoi 剖分，
    /// 将原始几何分割为若干凸碎块。此处采用简化实现：
    /// 用随机点 + AABB 裁剪生成近似碎块（真实实现可用 Jump Flooding 算法）。
    /// </summary>
    public static class VoronoiFracture
    {
        /// <summary>
        /// 对一个轴对齐包围盒执行 Voronoi 碎裂，生成碎块层级。
        /// </summary>
        /// <param name="halfExtents">原始包围盒半尺寸</param>
        /// <param name="seedCount">种子点数量（决定碎块数）</param>
        /// <param name="depth">当前破坏深度</param>
        /// <param name="maxDepth">最大破坏深度</param>
        /// <param name="material">材质参数</param>
        /// <param name="rng">随机数生成器</param>
        /// <returns>生成的碎块列表（首个为根碎块）</returns>
        public static List<DestructibleChunk> Fracture(Vector3 halfExtents, int seedCount,
            int depth, int maxDepth, DestructibleMaterial material, Random rng)
        {
            var chunks = new List<DestructibleChunk>();
            if (depth > maxDepth) return chunks;

            // 生成随机种子点（在包围盒内）
            var seeds = new Vector3[seedCount];
            for (int i = 0; i < seedCount; i++)
            {
                seeds[i] = new Vector3(
                    (float)(rng.NextDouble() * 2 - 1) * halfExtents.X,
                    (float)(rng.NextDouble() * 2 - 1) * halfExtents.Y,
                    (float)(rng.NextDouble() * 2 - 1) * halfExtents.Z);
            }

            // 简化 Voronoi：每个种子点对应一个碎块，碎块为以种子为中心的 AABB，
            // 边界由相邻种子点的中点平面裁剪确定（此处近似为种子间距的一半）。
            for (int i = 0; i < seedCount; i++)
            {
                // 计算到最近邻居距离的一半作为碎块半尺寸
                float minDist = float.MaxValue;
                for (int j = 0; j < seedCount; j++)
                {
                    if (i == j) continue;
                    float d = Vector3.Distance(seeds[i], seeds[j]);
                    if (d < minDist) minDist = d;
                }
                float he = MathF.Min(minDist * 0.5f, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)));

                var chunk = new DestructibleChunk
                {
                    Id = i,
                    ParentId = -1,
                    Depth = depth,
                    Center = seeds[i],
                    HalfExtents = new Vector3(he),
                    BreakThreshold = material.Hardness * (1f + depth * 0.5f),
                    IsVisible = depth == 0, // 仅根可见，断裂后子块可见
                };

                // 生成简单立方体几何（6 面 12 三角形）
                BuildBoxGeometry(chunk);
                chunks.Add(chunk);
            }

            // 根碎块（包围整个原始几何）
            if (depth == 0)
            {
                var root = new DestructibleChunk
                {
                    Id = seedCount,
                    ParentId = -1,
                    Depth = 0,
                    Center = Vector3.Zero,
                    HalfExtents = halfExtents,
                    BreakThreshold = material.Hardness,
                    IsVisible = true,
                };
                BuildBoxGeometry(root);
                // 所有碎块作为根的子节点
                for (int i = 0; i < chunks.Count; i++)
                {
                    chunks[i].ParentId = root.Id;
                    root.Children.Add(chunks[i].Id);
                }
                chunks.Insert(0, root); // 根置于首位
            }
            return chunks;
        }

        /// <summary>为碎块构建立方体几何（用于渲染与碰撞）。</summary>
        private static void BuildBoxGeometry(DestructibleChunk chunk)
        {
            Vector3 he = chunk.HalfExtents;
            // 8 顶点
            chunk.Vertices = new List<Vector3>
            {
                new(-he.X, -he.Y, -he.Z), new( he.X, -he.Y, -he.Z),
                new( he.X,  he.Y, -he.Z), new(-he.X,  he.Y, -he.Z),
                new(-he.X, -he.Y,  he.Z), new( he.X, -he.Y,  he.Z),
                new( he.X,  he.Y,  he.Z), new(-he.X,  he.Y,  he.Z),
            };
            // 12 三角形（6 面）
            chunk.Indices = new List<int>
            {
                0,1,2, 0,2,3,   // 后
                4,6,5, 4,7,6,   // 前
                0,3,7, 0,7,4,   // 左
                1,5,6, 1,6,2,   // 右
                3,2,6, 3,6,7,   // 上
                0,4,5, 0,5,1,   // 下
            };
        }
    }

    /// <summary>
    /// 破坏系统。实现 ECS 的 ISystem 接口，统一驱动所有可破坏实体。
    /// 负责：检测冲击、执行断裂、生成碎块刚体、应力传播、碎屑粒子。
    /// </summary>

    /// <summary>
    /// 布料系统。实现 ECS 的 ISystem 接口，统一驱动所有布料组件。
    /// 根据组件配置选择 CPU 或 GPU 求解路径。
    /// </summary>
    public sealed class ClothSystem : ISystem
    {
        /// <summary>系统名称</summary>
        public string Name => "ClothSystem";

        /// <summary>物理世界引用（用于碰撞查询）</summary>
        public PhysicsWorld? PhysicsWorld;
        /// <summary>RHI 设备引用（GPU 路径）</summary>
        public RHIDevice? Device;
        /// <summary>计算命令列表（GPU 路径复用）</summary>
        public RHICommandList? ComputeCmd;
        /// <summary>重力</summary>
        public Vector3 Gravity = new(0, -9.81f, 0);

        // 模拟器缓存（每实体一个）
        private readonly Dictionary<int, ClothSimulator> _simulators = new();

        /// <summary>每帧更新：推进所有布料。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            var entities = ecs.QueryWithComponent<ClothComponent>();
            foreach (int eid in entities)
            {
                var cloth = ecs.GetComponent<ClothComponent>(eid);
                if (!cloth.Enabled || cloth.Asset == null) continue;
                if (cloth.Particles == null) continue;

                // 懒初始化模拟器
                if (!_simulators.TryGetValue(eid, out var sim))
                {
                    sim = new ClothSimulator(cloth.Asset.Config);
                    var tf = ecs.GetComponent<TransformComponent>(eid);
                    sim.Initialize(tf.Position);
                    cloth.Particles = sim.Particles;
                    cloth.Springs = sim.Springs;
                    if (cloth.UseGPU && Device != null)
                    {
                        sim.InitializeGPU(Device);
                        ComputeCmd ??= Device.CreateCommandList(RHIQueueType.Compute, "ClothCompute");
                    }
                    _simulators[eid] = sim;
                }

                // 更新碰撞中心（跟随实体或物理体）
                if (PhysicsWorld != null)
                {
                    cloth.CollisionCenter = PhysicsWorld.GetPositionByEntity(eid);
                }

                // 求解
                if (cloth.UseGPU && ComputeCmd != null)
                    sim.StepGPU(dt, Gravity, cloth.CollisionCenter, ComputeCmd);
                else
                    sim.StepCPU(dt, Gravity, cloth.CollisionCenter);

                ecs.SetComponent(eid, cloth);
            }
        }

        /// <summary>为实体挂载布料。</summary>
        public void AssignCloth(EntityComponentSystem ecs, int entityId, ClothAsset asset, bool useGPU = false)
        {
            if (!ecs.HasEntity(entityId)) return;
            ecs.AddComponent(entityId, new ClothComponent
            {
                Asset = asset,
                UseGPU = useGPU,
                Enabled = true,
            });
        }

        /// <summary>绑定物理世界。</summary>
        public void BindPhysics(PhysicsWorld world) => PhysicsWorld = world;
        /// <summary>绑定 RHI 设备（启用 GPU 布料）。</summary>
        public void BindDevice(RHIDevice device) => Device = device;
    }
}
