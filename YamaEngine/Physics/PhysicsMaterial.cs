using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  PhysicsMaterial.cs  —  Yama Engine 物理材质系统
    //
    //  提供完整的物理材质参数体系，包括：
    //    - 摩擦系数（静摩擦/动摩擦/滚动摩擦）
    //    - 弹性/恢复系数（Restitution）
    //    - 密度（Density，用于从体积自动计算质量）
    //    - 材质组合规则（Average / Min / Max / Multiply）
    //    - 预设材质库（金属/木材/橡胶/冰/混凝土/玻璃/肉体/地面）
    //    - 与 PhysicsWorld / BodyDescriptor 的对接
    //
    //  所有数学使用 System.Numerics。固定时间步长由 PhysicsWorld 管理。
    // ============================================================================================

    /// <summary>
    ///   材质参数组合模式。当两个不同材质的物体发生碰撞时，决定如何从两者的
    ///   摩擦/弹性值计算出最终使用的接触参数。
    /// </summary>
    public enum PhysicsMaterialCombineMode
    {
        /// <summary>取两者平均值。适用于大多数自然材质组合。</summary>
        Average,
        /// <summary>取两者最小值。使表面偏"滑"（如冰+金属→滑）。</summary>
        Min,
        /// <summary>取两者最大值。使表面偏"涩"（如橡胶+金属→涩）。</summary>
        Multiply,
        /// <summary>取两者乘积。极端值组合（如 0.9×0.1=0.09）。</summary>
        Max,
    }

    /// <summary>
    ///   物理材质。描述刚体表面的摩擦、弹性与密度属性，以及碰撞时的组合规则。
    ///   每个刚体可关联一个 PhysicsMaterial，碰撞求解器根据双方的材质参数
    ///   和组合模式计算接触点的等效摩擦/恢复系数。
    /// </summary>
    public sealed class PhysicsMaterial : IEquatable<PhysicsMaterial>, ICloneable
    {
        // ── 摩擦参数 ──

        /// <summary>动摩擦系数 (0=无摩擦/冰面, 1=高摩擦/橡胶)。默认 0.6。</summary>
        public float Friction { get; set; } = 0.6f;

        /// <summary>静摩擦系数 (0=无摩擦, 1=极高静摩擦)。默认等于动摩擦。</summary>
        public float StaticFriction { get; set; } = 0.6f;

        /// <summary>滚动摩擦系数（影响滚动刚体的减速）。默认 0.001。</summary>
        public float RollingFriction { get; set; } = 0.001f;

        /// <summary>摩擦组合模式。默认 Average。</summary>
        public PhysicsMaterialCombineMode FrictionCombine { get; set; } = PhysicsMaterialCombineMode.Average;

        // ── 弹性参数 ──

        /// <summary>恢复系数/弹性 (0=完全非弹性碰撞/泥, 1=完全弹性碰撞/完美弹球)。默认 0.2。</summary>
        public float Restitution { get; set; } = 0.2f;

        /// <summary>弹性组合模式。默认 Average。</summary>
        public PhysicsMaterialCombineMode RestitutionCombine { get; set; } = PhysicsMaterialCombineMode.Average;

        // ── 密度参数 ──

        /// <summary>
        ///   密度 (kg/m³)。用于从碰撞体体积自动计算质量。
        ///   常见参考值：水=1000, 铝=2700, 铁=7800, 木材≈600, 橡胶≈1100。
        ///   默认 1000（近似水密度）。
        /// </summary>
        public float Density { get; set; } = 1000f;

        // ── 高级参数 ──

        /// <summary>线性阻尼额外系数（叠加到刚体自身的 LinearDamping 上）。默认 0。</summary>
        public float ExtraLinearDamping { get; set; } = 0f;

        /// <summary>角阻尼额外系数（叠加到刚体自身的 AngularDamping 上）。默认 0。</summary>
        public float ExtraAngularDamping { get; set; } = 0f;

        /// <summary>材质名称（用于调试与序列化）。</summary>
        public string Name { get; set; } = "Default";

        // ──────────────────────────────────────────────────────────────
        // 构造函数
        // ──────────────────────────────────────────────────────────────

        /// <summary>创建一个默认物理材质（摩擦0.6/弹性0.2/密度1000）。</summary>
        public PhysicsMaterial() { }

        /// <summary>创建指定摩擦与弹性的物理材质（密度默认1000）。</summary>
        public PhysicsMaterial(float friction, float restitution, float density = 1000f)
        {
            Friction = Math.Clamp(friction, 0f, 1f);
            StaticFriction = Friction;
            Restitution = Math.Clamp(restitution, 0f, 1f);
            Density = MathF.Max(0.0001f, density);
        }

        /// <summary>创建完整参数的物理材质。</summary>
        public PhysicsMaterial(string name, float friction, float restitution, float density,
            float staticFriction = -1f, float rollingFriction = 0.001f,
            PhysicsMaterialCombineMode frictionCombine = PhysicsMaterialCombineMode.Average,
            PhysicsMaterialCombineMode restitutionCombine = PhysicsMaterialCombineMode.Average)
        {
            Name = name;
            Friction = Math.Clamp(friction, 0f, 1f);
            StaticFriction = Math.Clamp(staticFriction < 0 ? friction : staticFriction, 0f, 1f);
            RollingFriction = MathF.Max(0f, rollingFriction);
            Restitution = Math.Clamp(restitution, 0f, 1f);
            Density = MathF.Max(0.0001f, density);
            FrictionCombine = frictionCombine;
            RestitutionCombine = restitutionCombine;
        }

        // ──────────────────────────────────────────────────────────────
        // 组合计算（碰撞接触参数求解）
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        ///   计算两个材质的等效摩擦系数。
        ///   根据两者的 FrictionCombine 模式决定使用哪种规则：
        ///   优先取"更滑"一方的模式（Min 优先于 Average 优先于 Max 优先于 Multiply），
        ///   体现物理直觉：两个不同材质接触时，表面特性由"更滑"的一方主导。
        /// </summary>
        public static float CombineFriction(PhysicsMaterial a, PhysicsMaterial b)
        {
            if (a == null || b == null) return 0.5f;
            PhysicsMaterialCombineMode mode = PickFrictionCombineMode(a.FrictionCombine, b.FrictionCombine);
            return CombineValues(a.Friction, b.Friction, mode);
        }

        /// <summary>计算两个材质的等效静摩擦系数。</summary>
        public static float CombineStaticFriction(PhysicsMaterial a, PhysicsMaterial b)
        {
            if (a == null || b == null) return 0.5f;
            PhysicsMaterialCombineMode mode = PickFrictionCombineMode(a.FrictionCombine, b.FrictionCombine);
            return CombineValues(a.StaticFriction, b.StaticFriction, mode);
        }

        /// <summary>计算两个材质的等效恢复系数（弹性）。</summary>
        public static float CombineRestitution(PhysicsMaterial a, PhysicsMaterial b)
        {
            if (a == null || b == null) return 0f;
            PhysicsMaterialCombineMode mode = PickRestitutionCombineMode(a.RestitutionCombine, b.RestitutionCombine);
            return CombineValues(a.Restitution, b.Restitution, mode);
        }

        /// <summary>计算两个材质的等效滚动摩擦系数（取平均）。</summary>
        public static float CombineRollingFriction(PhysicsMaterial a, PhysicsMaterial b)
        {
            if (a == null || b == null) return 0f;
            return (a.RollingFriction + b.RollingFriction) * 0.5f;
        }

        /// <summary>
        ///   计算两个材质碰撞后的综合接触参数。
        /// </summary>
        public static ContactMaterialParams ComputeContact(PhysicsMaterial a, PhysicsMaterial b)
        {
            return new ContactMaterialParams
            {
                Friction = CombineFriction(a, b),
                StaticFriction = CombineStaticFriction(a, b),
                Restitution = CombineRestitution(a, b),
                RollingFriction = CombineRollingFriction(a, b),
                CombinedRestitutionCombine = PickRestitutionCombineMode(
                    a?.RestitutionCombine ?? PhysicsMaterialCombineMode.Average,
                    b?.RestitutionCombine ?? PhysicsMaterialCombineMode.Average),
                CombinedFrictionCombine = PickFrictionCombineMode(
                    a?.FrictionCombine ?? PhysicsMaterialCombineMode.Average,
                    b?.FrictionCombine ?? PhysicsMaterialCombineMode.Average),
            };
        }

        /// <summary>
        ///   选择摩擦组合模式：取"更滑"一方的模式。
        ///   优先级：Min(最滑) > Average > Multiply > Max(最涩)。
        /// </summary>
        private static PhysicsMaterialCombineMode PickFrictionCombineMode(
            PhysicsMaterialCombineMode a, PhysicsMaterialCombineMode b)
        {
            // 取数值更小的（枚举序号：Average=0, Min=1, Max=2, Multiply=3）
            // 但我们想让 Min 优先（更滑），所以定义优先级映射
            int prioA = FrictionCombinePriority(a);
            int prioB = FrictionCombinePriority(b);
            return prioA <= prioB ? a : b;
        }

        /// <summary>
        ///   选择弹性组合模式：取"更弹"一方的模式。
        ///   优先级：Max(最弹) > Average > Multiply > Min(最不弹)。
        /// </summary>
        private static PhysicsMaterialCombineMode PickRestitutionCombineMode(
            PhysicsMaterialCombineMode a, PhysicsMaterialCombineMode b)
        {
            int prioA = RestitutionCombinePriority(a);
            int prioB = RestitutionCombinePriority(b);
            return prioA <= prioB ? a : b;
        }

        // 摩擦组合优先级：数值越小越优先（越滑）
        private static int FrictionCombinePriority(PhysicsMaterialCombineMode m) => m switch
        {
            PhysicsMaterialCombineMode.Min => 0,       // 最滑，优先
            PhysicsMaterialCombineMode.Average => 1,
            PhysicsMaterialCombineMode.Multiply => 2,
            PhysicsMaterialCombineMode.Max => 3,        // 最涩，最后
            _ => 1,
        };

        // 弹性组合优先级：数值越小越优先（越弹）
        private static int RestitutionCombinePriority(PhysicsMaterialCombineMode m) => m switch
        {
            PhysicsMaterialCombineMode.Max => 0,        // 最弹，优先
            PhysicsMaterialCombineMode.Average => 1,
            PhysicsMaterialCombineMode.Multiply => 2,
            PhysicsMaterialCombineMode.Min => 3,        // 最不弹，最后
            _ => 1,
        };

        /// <summary>按组合模式计算两个标量的最终值。</summary>
        private static float CombineValues(float a, float b, PhysicsMaterialCombineMode mode)
        {
            return mode switch
            {
                PhysicsMaterialCombineMode.Average => (a + b) * 0.5f,
                PhysicsMaterialCombineMode.Min => MathF.Min(a, b),
                PhysicsMaterialCombineMode.Max => MathF.Max(a, b),
                PhysicsMaterialCombineMode.Multiply => a * b,
                _ => (a + b) * 0.5f,
            };
        }

        // ──────────────────────────────────────────────────────────────
        // 质量计算（从密度和体积推导质量）
        // ──────────────────────────────────────────────────────────────

        /// <summary>根据密度和体积计算质量 (kg)。</summary>
        public float CalculateMass(float volumeCubicMeters)
        {
            return MathF.Max(0f, Density * volumeCubicMeters);
        }

        /// <summary>根据密度和球体半径计算质量。</summary>
        public float CalculateMassSphere(float radius)
        {
            float volume = (4f / 3f) * MathF.PI * radius * radius * radius;
            return CalculateMass(volume);
        }

        /// <summary>根据密度和盒体半尺寸计算质量。</summary>
        public float CalculateMassBox(Vector3 halfExtents)
        {
            float volume = 8f * halfExtents.X * halfExtents.Y * halfExtents.Z;
            return CalculateMass(volume);
        }

        /// <summary>根据密度和胶囊体参数计算质量。</summary>
        public float CalculateMassCapsule(float radius, float height)
        {
            // 胶囊 = 圆柱 + 两个半球
            float cylVol = MathF.PI * radius * radius * height;
            float sphVol = (4f / 3f) * MathF.PI * radius * radius * radius;
            return CalculateMass(cylVol + sphVol);
        }

        // ──────────────────────────────────────────────────────────────
        // 与 PhysicsWorld / BodyDescriptor 对接
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        ///   将材质参数应用到已创建的刚体上。
        ///   调用 PhysicsWorld.SetFriction / SetRestitution 更新刚体属性。
        /// </summary>
        /// <param name="world">物理世界实例。</param>
        /// <param name="bodyId">目标刚体 ID。</param>
        public void ApplyToBody(PhysicsWorld world, uint bodyId)
        {
            if (world == null || bodyId == 0) return;
            world.SetFriction(bodyId, Friction);
            world.SetRestitution(bodyId, Restitution);
        }

        /// <summary>
        ///   将材质参数填充到 BodyDescriptor 中（在创建刚体之前调用）。
        /// </summary>
        /// <param name="descriptor">要填充的刚体描述符。</param>
        public void ApplyToDescriptor(BodyDescriptor descriptor)
        {
            if (descriptor == null) return;
            descriptor.Friction = Friction;
            descriptor.Restitution = Restitution;
            descriptor.LinearDamping += ExtraLinearDamping;
            descriptor.AngularDamping += ExtraAngularDamping;
        }

        /// <summary>
        ///   根据形状自动计算质量并设置到 BodyDescriptor。
        ///   根据 BodyDescriptor 的形状类型和 Density 推导质量。
        /// </summary>
        public void ApplyMassToDescriptor(BodyDescriptor descriptor)
        {
            if (descriptor == null) return;
            float mass = descriptor.ShapeKind switch
            {
                ShapeType.Box => CalculateMassBox(descriptor.BoxHalfExtents),
                ShapeType.Sphere => CalculateMassSphere(descriptor.SphereRadius),
                ShapeType.Capsule => CalculateMassCapsule(descriptor.CapsuleRadius, descriptor.CapsuleHeight),
                _ => 1f, // Mesh/Convex 等形状无法简单计算体积，使用默认质量
            };
            descriptor.Mass = mass > 0f ? mass : 1f;
        }

        // ──────────────────────────────────────────────────────────────
        // IEquatable / ICloneable
        // ──────────────────────────────────────────────────────────────

        public bool Equals(PhysicsMaterial? other)
        {
            if (other is null) return false;
            return Friction == other.Friction
                && StaticFriction == other.StaticFriction
                && Restitution == other.Restitution
                && Density == other.Density
                && RollingFriction == other.RollingFriction
                && FrictionCombine == other.FrictionCombine
                && RestitutionCombine == other.RestitutionCombine;
        }

        public override bool Equals(object? obj) => Equals(obj as PhysicsMaterial);

        public override int GetHashCode()
        {
            return HashCode.Combine(Friction, Restitution, Density, StaticFriction,
                                    RollingFriction, FrictionCombine, RestitutionCombine);
        }

        public PhysicsMaterial Clone()
        {
            return new PhysicsMaterial
            {
                Name = Name,
                Friction = Friction,
                StaticFriction = StaticFriction,
                RollingFriction = RollingFriction,
                Restitution = Restitution,
                Density = Density,
                FrictionCombine = FrictionCombine,
                RestitutionCombine = RestitutionCombine,
                ExtraLinearDamping = ExtraLinearDamping,
                ExtraAngularDamping = ExtraAngularDamping,
            };
        }

        object ICloneable.Clone() => Clone();

        public override string ToString()
        {
            return $"PhysicsMaterial[{Name}] Friction={Friction:F3} Restitution={Restitution:F3} Density={Density:F1}";
        }

        public static bool operator ==(PhysicsMaterial? left, PhysicsMaterial? right)
            => left is null ? right is null : left.Equals(right);

        public static bool operator !=(PhysicsMaterial? left, PhysicsMaterial? right)
            => !(left == right);
    }

    /// <summary>
    ///   两个材质碰撞后的综合接触参数。由 PhysicsMaterial.ComputeContact 计算得出，
    ///   供碰撞求解器在接触点使用。
    /// </summary>
    public struct ContactMaterialParams
    {
        /// <summary>等效动摩擦系数。</summary>
        public float Friction;
        /// <summary>等效静摩擦系数。</summary>
        public float StaticFriction;
        /// <summary>等效恢复系数（弹性）。</summary>
        public float Restitution;
        /// <summary>等效滚动摩擦系数。</summary>
        public float RollingFriction;
        /// <summary>最终采用的摩擦组合模式。</summary>
        public PhysicsMaterialCombineMode CombinedFrictionCombine;
        /// <summary>最终采用的弹性组合模式。</summary>
        public PhysicsMaterialCombineMode CombinedRestitutionCombine;

        public override string ToString()
            => $"Contact(F={Friction:F3} R={Restitution:F3} SF={StaticFriction:F3} RF={RollingFriction:F4})";
    }

    // ============================================================================================
    //  预设材质库
    // ============================================================================================

    /// <summary>
    ///   预设物理材质库。提供游戏开发中常用的标准材质，可直接使用或作为自定义材质的基础。
    ///   所有预设均为不可变单例（Clone 后可修改）。
    /// </summary>
    public static class PhysicsMaterialPresets
    {
        /// <summary>默认材质：摩擦0.6/弹性0.2/密度1000。</summary>
        public static readonly PhysicsMaterial Default = new("Default", 0.6f, 0.2f, 1000f);

        /// <summary>金属：摩擦0.3/弹性0.3/密度7800（铁密度）。</summary>
        public static readonly PhysicsMaterial Metal = new("Metal", 0.3f, 0.3f, 7800f,
            rollingFriction: 0.002f);

        /// <summary>抛光金属：摩擦0.15/弹性0.4/密度7800。</summary>
        public static readonly PhysicsMaterial PolishedMetal = new("PolishedMetal", 0.15f, 0.4f, 7800f,
            rollingFriction: 0.0005f);

        /// <summary>木材：摩擦0.5/弹性0.25/密度600。</summary>
        public static readonly PhysicsMaterial Wood = new("Wood", 0.5f, 0.25f, 600f,
            rollingFriction: 0.01f);

        /// <summary>橡胶：摩擦0.9/弹性0.8/密度1100。高摩擦高弹性。</summary>
        public static readonly PhysicsMaterial Rubber = new("Rubber", 0.9f, 0.8f, 1100f,
            rollingFriction: 0.05f);

        /// <summary>冰：摩擦0.02/弹性0.1/密度920。极低摩擦。</summary>
        public static readonly PhysicsMaterial Ice = new("Ice", 0.02f, 0.1f, 920f,
            rollingFriction: 0.0001f,
            frictionCombine: PhysicsMaterialCombineMode.Min);

        /// <summary>混凝土：摩擦0.7/弹性0.15/密度2400。</summary>
        public static readonly PhysicsMaterial Concrete = new("Concrete", 0.7f, 0.15f, 2400f,
            rollingFriction: 0.015f);

        /// <summary>玻璃：摩擦0.4/弹性0.5/密度2500。</summary>
        public static readonly PhysicsMaterial Glass = new("Glass", 0.4f, 0.5f, 2500f,
            rollingFriction: 0.003f);

        /// <summary>肉体/皮革：摩擦0.8/弹性0.05/密度1050。近水密度，几乎无弹性。</summary>
        public static readonly PhysicsMaterial Flesh = new("Flesh", 0.8f, 0.05f, 1050f,
            rollingFriction: 0.02f);

        /// <summary>草地：摩擦0.6/弹性0.2/密度1200。</summary>
        public static readonly PhysicsMaterial Grass = new("Grass", 0.6f, 0.2f, 1200f,
            rollingFriction: 0.02f);

        /// <summary>沙地：摩擦0.65/弹性0.05/密度1600。低弹性高摩擦。</summary>
        public static readonly PhysicsMaterial Sand = new("Sand", 0.65f, 0.05f, 1600f,
            rollingFriction: 0.08f);

        /// <summary>泥地：摩擦0.5/弹性0.02/密度1700。极低弹性。</summary>
        public static readonly PhysicsMaterial Mud = new("Mud", 0.5f, 0.02f, 1700f,
            rollingFriction: 0.1f);

        /// <summary>塑料：摩擦0.5/弹性0.35/密度950。</summary>
        public static readonly PhysicsMaterial Plastic = new("Plastic", 0.5f, 0.35f, 950f,
            rollingFriction: 0.005f);

        /// <summary>海绵：摩擦0.7/弹性0.1/密度50。极低密度。</summary>
        public static readonly PhysicsMaterial Sponge = new("Sponge", 0.7f, 0.1f, 50f,
            rollingFriction: 0.03f);

        /// <summary>无摩擦材质（用于零摩擦轨道/导轨）：摩擦0/弹性0/密度1000。</summary>
        public static readonly PhysicsMaterial Frictionless = new("Frictionless", 0f, 0f, 1000f,
            rollingFriction: 0f,
            frictionCombine: PhysicsMaterialCombineMode.Min,
            restitutionCombine: PhysicsMaterialCombineMode.Min);

        /// <summary>完美弹性材质（用于弹球/弹力球）：摩擦0.3/弹性1.0/密度1200。</summary>
        public static readonly PhysicsMaterial Bouncy = new("Bouncy", 0.3f, 1.0f, 1200f,
            restitutionCombine: PhysicsMaterialCombineMode.Max);

        /// <summary>
        ///   按名称查找预设材质。找不到返回 null。
        /// </summary>
        public static PhysicsMaterial? FindByName(string name)
        {
            return name?.ToLowerInvariant() switch
            {
                "default" => Default,
                "metal" => Metal,
                "polishedmetal" => PolishedMetal,
                "wood" => Wood,
                "rubber" => Rubber,
                "ice" => Ice,
                "concrete" => Concrete,
                "glass" => Glass,
                "flesh" => Flesh,
                "grass" => Grass,
                "sand" => Sand,
                "mud" => Mud,
                "plastic" => Plastic,
                "sponge" => Sponge,
                "frictionless" => Frictionless,
                "bouncy" => Bouncy,
                _ => null,
            };
        }

        /// <summary>获取全部预设材质的只读列表。</summary>
        public static IReadOnlyList<PhysicsMaterial> All => new[]
        {
            Default, Metal, PolishedMetal, Wood, Rubber, Ice, Concrete, Glass,
            Flesh, Grass, Sand, Mud, Plastic, Sponge, Frictionless, Bouncy,
        };
    }

    // ============================================================================================
    //  材质注册表
    // ============================================================================================

    /// <summary>
    ///   物理材质注册表。管理命名材质的注册、查找与碰撞材质对缓存。
    ///   使用双字典加速材质对（A,B）的接触参数查询，避免每次碰撞重复计算。
    /// </summary>
    public class PhysicsMaterialTable
    {
        private readonly Dictionary<string, PhysicsMaterial> _materials = new();
        private readonly Dictionary<ulong, ContactMaterialParams> _contactCache = new();

        /// <summary>注册一个命名材质。同名材质会被覆盖。</summary>
        public void Register(string name, PhysicsMaterial material)
        {
            if (string.IsNullOrEmpty(name) || material == null) return;
            material.Name = name;
            _materials[name] = material;
            _contactCache.Clear(); // 材质变更，清空缓存
        }

        /// <summary>按名称查找材质。找不到返回 null。</summary>
        public PhysicsMaterial? Find(string name)
            => _materials.TryGetValue(name, out var m) ? m : null;

        /// <summary>移除一个材质。</summary>
        public bool Remove(string name)
        {
            bool removed = _materials.Remove(name);
            if (removed) _contactCache.Clear();
            return removed;
        }

        /// <summary>已注册材质数量。</summary>
        public int Count => _materials.Count;

        /// <summary>获取全部已注册材质名。</summary>
        public IEnumerable<string> MaterialNames => _materials.Keys;

        /// <summary>
        ///   计算两个命名材质的接触参数（带缓存）。
        ///   使用材质名哈希组合作为缓存键，避免重复计算。
        /// </summary>
        public ContactMaterialParams GetContactParams(string nameA, string nameB)
        {
            var a = Find(nameA) ?? PhysicsMaterialPresets.Default;
            var b = Find(nameB) ?? PhysicsMaterialPresets.Default;
            return GetContactParams(a, b);
        }

        /// <summary>
        ///   计算两个材质的接触参数（带缓存）。
        /// </summary>
        public ContactMaterialParams GetContactParams(PhysicsMaterial a, PhysicsMaterial b)
        {
            if (a == null) a = PhysicsMaterialPresets.Default;
            if (b == null) b = PhysicsMaterialPresets.Default;

            // 缓存键：两个名称哈希的组合（无序对，A+B 和 B+A 应产生相同键）
            ulong hashA = (ulong)(a.Name?.GetHashCode() ?? 0);
            ulong hashB = (ulong)(b.Name?.GetHashCode() ?? 0);
            ulong key = hashA < hashB
                ? (hashA << 32) | (hashB & 0xFFFFFFFF)
                : (hashB << 32) | (hashA & 0xFFFFFFFF);

            if (_contactCache.TryGetValue(key, out var cached))
                return cached;

            var contact = PhysicsMaterial.ComputeContact(a, b);
            _contactCache[key] = contact;
            return contact;
        }

        /// <summary>注册全部预设材质到注册表。</summary>
        public void RegisterAllPresets()
        {
            foreach (var preset in PhysicsMaterialPresets.All)
                Register(preset.Name, preset.Clone());
        }

        /// <summary>清空全部材质与缓存。</summary>
        public void Clear()
        {
            _materials.Clear();
            _contactCache.Clear();
        }
    }
}
