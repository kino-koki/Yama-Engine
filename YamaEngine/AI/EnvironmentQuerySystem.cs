// ============================================================================
//  YamaEngine.AI.EnvironmentQuerySystem.cs
//  yama引擎 -- 环境查询系统 (Environment Query System, EQS)
//  对齐 UE Environment Query System
// ----------------------------------------------------------------------------
//  完整的环境查询系统，包含：
//    1. EQSContext          - 查询上下文 (查询者/世界/黑板/时间)
//    2. EQSGenerator        - 生成器基类
//       - EQSGenerator_AllActorsOfClass / ActorsInRadius / PointsInRadius
//       - EQSGenerator_Grid / EQSGenerator_AlongPath / EQSGenerator_CurrentLocation
//    3. EQSTest             - 测试器基类
//       - EQSTest_Distance / Trace / Dot / Angle / PathCost
//       - EQSTest_Blackboard / Noise / Height / Cover / Multiple
//    4. EQSQueryItem        - 查询项 (位置/得分/关联Actor)
//    5. EQSResult           - 查询结果 (排序后项目列表/最佳项)
//    6. EQSQuery            - 查询请求 (生成器/测试列表/评分)
//    7. EQSManager          - 管理器 (注册/执行/缓存)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、查询项 (EQSQueryItem)
    // ========================================================================

    /// <summary>
    /// EQS 查询项。描述一个候选点/Actor及其得分。
    /// </summary>
    public sealed class EQSQueryItem
    {
        /// <summary>位置。</summary>
        public Vector3 Position { get; set; }
        /// <summary>原始得分。</summary>
        public float RawScore { get; set; }
        /// <summary>加权后得分。</summary>
        public float WeightedScore { get; set; }
        /// <summary>关联的 Actor（可选）。</summary>
        public YamaEngine.GameFramework.Actor AssociatedActor { get; set; }
        /// <summary>上下文数据。</summary>
        public Dictionary<string, object> ContextData { get; } = new Dictionary<string, object>();
        /// <summary>标记是否有效。</summary>
        public bool IsValid { get; set; } = true;

        public EQSQueryItem() { }

        public EQSQueryItem(Vector3 position, YamaEngine.GameFramework.Actor actor = null)
        {
            Position = position;
            AssociatedActor = actor;
        }
    }

    // ========================================================================
    //  二、查询上下文 (EQSContext)
    // ========================================================================

    /// <summary>
    /// EQS 查询上下文。提供查询所需的运行时信息。
    /// </summary>
    public sealed class EQSContext
    {
        /// <summary>查询者（发出查询的AI实体）。</summary>
        public YamaEngine.GameFramework.Actor Querier { get; set; }
        /// <summary>查询者位置。</summary>
        public Vector3 QuerierPosition { get; set; }
        /// <summary>查询者前方向。</summary>
        public Vector3 QuerierForward { get; set; }
        /// <summary>黑板（可选）。</summary>
        public Blackboard Blackboard { get; set; }
        /// <summary>当前时间。</summary>
        public float CurrentTime { get; set; }
        /// <summary>DeltaTime。</summary>
        public float DeltaTime { get; set; }
        /// <summary>射线检测回调。</summary>
        public Func<Vector3, Vector3, bool> RaycastCheck { get; set; }
        /// <summary>路径查询回调。</summary>
        public Func<Vector3, Vector3, float> PathCostQuery { get; set; }
        /// <summary>世界中的所有Actor（可选）。</summary>
        public List<YamaEngine.GameFramework.Actor> WorldActors { get; set; } = new List<YamaEngine.GameFramework.Actor>();
        /// <summary>自定义用户数据。</summary>
        public object UserData { get; set; }

        public EQSContext() { }

        public void SetQuerier(YamaEngine.GameFramework.Actor querier)
        {
            Querier = querier;
            QuerierPosition = querier?.Position ?? Vector3.Zero;
            QuerierForward = Vector3.Transform(new Vector3(0, 0, 1),
                querier?.Rotation ?? Quaternion.Identity);
        }
    }

    // ========================================================================
    //  三、查询结果 (EQSResult)
    // ========================================================================

    /// <summary>
    /// EQS 查询结果。排序后的项目列表、最佳项。
    /// </summary>
    public sealed class EQSResult
    {
        /// <summary>所有查询项（按得分排序）。</summary>
        public List<EQSQueryItem> Items { get; private set; } = new List<EQSQueryItem>();
        /// <summary>最佳项（得分最高）。</summary>
        public EQSQueryItem BestItem { get; private set; }
        /// <summary>原始（未排序）数据。</summary>
        public List<EQSQueryItem> RawItems { get; private set; } = new List<EQSQueryItem>();
        /// <summary>查询是否成功。</summary>
        public bool IsSuccessful { get; private set; }

        public EQSResult() { }

        public void SetItems(List<EQSQueryItem> rawItems)
        {
            RawItems = new List<EQSQueryItem>(rawItems);
            Items = rawItems
                .Where(i => i.IsValid)
                .OrderByDescending(i => i.WeightedScore)
                .ToList();
            BestItem = Items.Count > 0 ? Items[0] : null;
            IsSuccessful = Items.Count > 0;
        }

        /// <summary>获取前N个最佳项。</summary>
        public List<EQSQueryItem> GetTopItems(int count)
        {
            return Items.Take(count).ToList();
        }

        /// <summary>获取随机一个高分项（从得分排名前N%中随机选择）。</summary>
        public EQSQueryItem GetRandomTopItem(float percentage = 0.3f)
        {
            if (Items.Count == 0) return null;
            int count = Math.Max(1, (int)(Items.Count * percentage));
            var rng = new Random();
            int idx = rng.Next(count);
            return Items[idx];
        }

        /// <summary>获取平均得分。</summary>
        public float GetAverageScore()
        {
            if (Items.Count == 0) return 0f;
            return Items.Average(i => i.WeightedScore);
        }
    }

    // ========================================================================
    //  四、生成器基类 (EQSGenerator)
    // ========================================================================

    /// <summary>
    /// EQS 生成器基类。生成候选查询项。
    /// </summary>
    public abstract class EQSGenerator
    {
        public string GeneratorName { get; protected set; } = "UnnamedGenerator";
        public bool IsEnabled { get; set; } = true;

        /// <summary>生成候选项目。</summary>
        public abstract List<EQSQueryItem> GenerateItems(EQSContext ctx);

        public virtual EQSGenerator Clone()
        {
            return (EQSGenerator)Activator.CreateInstance(GetType());
        }
    }

    /// <summary>生成所有某类Actor。</summary>
    public sealed class EQSGenerator_AllActorsOfClass : EQSGenerator
    {
        public Type ActorType { get; set; }
        public string ActorTag { get; set; } = "";

        public EQSGenerator_AllActorsOfClass()
        {
            GeneratorName = "AllActorsOfClass";
        }

        public override List<EQSQueryItem> GenerateItems(EQSContext ctx)
        {
            var items = new List<EQSQueryItem>();
            if (ctx.WorldActors == null) return items;

            foreach (var actor in ctx.WorldActors)
            {
                if (actor == null) continue;
                if (ActorType != null && !ActorType.IsInstanceOfType(actor)) continue;
                if (!string.IsNullOrEmpty(ActorTag) && !actor.ActorTags.Contains(ActorTag)) continue;
                items.Add(new EQSQueryItem(actor.Position, actor));
            }

            return items;
        }
    }

    /// <summary>生成半径内的Actor。</summary>
    public sealed class EQSGenerator_ActorsInRadius : EQSGenerator
    {
        public float Radius { get; set; } = 500f;
        public string ActorTag { get; set; } = "";

        public EQSGenerator_ActorsInRadius()
        {
            GeneratorName = "ActorsInRadius";
        }

        public override List<EQSQueryItem> GenerateItems(EQSContext ctx)
        {
            var items = new List<EQSQueryItem>();
            if (ctx.WorldActors == null) return items;

            foreach (var actor in ctx.WorldActors)
            {
                if (actor == null) continue;
                if (!string.IsNullOrEmpty(ActorTag) && !actor.ActorTags.Contains(ActorTag)) continue;

                float dist = Vector3.Distance(ctx.QuerierPosition, actor.Position);
                if (dist <= Radius)
                    items.Add(new EQSQueryItem(actor.Position, actor));
            }

            return items;
        }
    }

    /// <summary>生成半径内的随机点。</summary>
    public sealed class EQSGenerator_PointsInRadius : EQSGenerator
    {
        public float Radius { get; set; } = 500f;
        public int PointCount { get; set; } = 20;
        public float MinDistanceBetweenPoints { get; set; } = 5f;
        private readonly Random _rng = new Random();

        public EQSGenerator_PointsInRadius()
        {
            GeneratorName = "PointsInRadius";
        }

        public override List<EQSQueryItem> GenerateItems(EQSContext ctx)
        {
            var items = new List<EQSQueryItem>();
            float angleStep = MathF.PI * 2f / PointCount;
            float radiusVariance = Radius / PointCount;

            for (int i = 0; i < PointCount; i++)
            {
                float angle = angleStep * i + (float)(_rng.NextDouble() - 0.5) * angleStep * 0.5f;
                float r = Radius * (float)(0.3 + 0.7 * _rng.NextDouble());
                float x = ctx.QuerierPosition.X + MathF.Cos(angle) * r;
                float z = ctx.QuerierPosition.Z + MathF.Sin(angle) * r;
                items.Add(new EQSQueryItem(new Vector3(x, ctx.QuerierPosition.Y, z)));
            }

            return items;
        }
    }

    /// <summary>生成网格点。</summary>
    public sealed class EQSGenerator_Grid : EQSGenerator
    {
        public float GridSize { get; set; } = 100f;
        public float CellSize { get; set; } = 10f;
        public int Rows { get; set; } = 10;
        public int Columns { get; set; } = 10;
        public bool CenterOnQuerier { get; set; } = true;

        public EQSGenerator_Grid()
        {
            GeneratorName = "Grid";
        }

        public override List<EQSQueryItem> GenerateItems(EQSContext ctx)
        {
            var items = new List<EQSQueryItem>();
            float startX = CenterOnQuerier
                ? ctx.QuerierPosition.X - GridSize / 2f
                : ctx.QuerierPosition.X;
            float startZ = CenterOnQuerier
                ? ctx.QuerierPosition.Z - GridSize / 2f
                : ctx.QuerierPosition.Z;

            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Columns; col++)
                {
                    float x = startX + col * CellSize;
                    float z = startZ + row * CellSize;
                    items.Add(new EQSQueryItem(new Vector3(x, ctx.QuerierPosition.Y, z)));
                }
            }

            return items;
        }
    }

    /// <summary>沿路径生成点。</summary>
    public sealed class EQSGenerator_AlongPath : EQSGenerator
    {
        public float SamplingDistance { get; set; } = 5f;
        public int MaxSamples { get; set; } = 20;

        /// <summary>路径点列表（可由外部设置）。</summary>
        public List<Vector3> PathPoints { get; set; } = new List<Vector3>();

        public EQSGenerator_AlongPath()
        {
            GeneratorName = "AlongPath";
        }

        public override List<EQSQueryItem> GenerateItems(EQSContext ctx)
        {
            var items = new List<EQSQueryItem>();
            if (PathPoints == null || PathPoints.Count < 2) return items;

            float totalLength = 0f;
            for (int i = 1; i < PathPoints.Count; i++)
                totalLength += Vector3.Distance(PathPoints[i - 1], PathPoints[i]);

            int sampleCount = Math.Min(MaxSamples, Math.Max(1, (int)(totalLength / SamplingDistance)));
            float stepDist = totalLength / sampleCount;
            float accumulated = 0f;

            for (int i = 0; i < PathPoints.Count - 1 && items.Count < sampleCount; i++)
            {
                var a = PathPoints[i];
                var b = PathPoints[i + 1];
                float segLen = Vector3.Distance(a, b);

                while (accumulated < segLen && items.Count < sampleCount)
                {
                    float t = accumulated / segLen;
                    var point = Vector3.Lerp(a, b, t);
                    items.Add(new EQSQueryItem(point));
                    accumulated += stepDist;
                }
                accumulated -= segLen;
            }

            return items;
        }
    }

    /// <summary>生成当前位置。</summary>
    public sealed class EQSGenerator_CurrentLocation : EQSGenerator
    {
        public EQSGenerator_CurrentLocation()
        {
            GeneratorName = "CurrentLocation";
        }

        public override List<EQSQueryItem> GenerateItems(EQSContext ctx)
        {
            return new List<EQSQueryItem>
            {
                new EQSQueryItem(ctx.QuerierPosition, ctx.Querier)
            };
        }
    }

    // ========================================================================
    //  五、测试器基类 (EQSTest)
    // ========================================================================

    /// <summary>
    /// 得分曲线类型。
    /// </summary>
    public enum EQSCurveType
    {
        Linear,
        InverseLinear,
        Constant,
        Sine,
        Parabolic
    }

    /// <summary>
    /// 多次采样策略。
    /// </summary>
    public enum EQSMultipleSampling
    {
        Best,
        Average,
        Worst
    }

    /// <summary>
    /// EQS 测试器基类。对每个候选项目进行评分。
    /// </summary>
    public abstract class EQSTest
    {
        public string TestName { get; protected set; } = "UnnamedTest";
        public bool IsEnabled { get; set; } = true;
        public float Weight { get; set; } = 1f;
        public float MinScoreClamp { get; set; } = 0f;
        public float MaxScoreClamp { get; set; } = 1f;
        public EQSCurveType CurveType { get; set; } = EQSCurveType.Linear;
        public bool CanBeDisabled { get; set; } = false;

        /// <summary>运行测试，返回得分 (0~1)。</summary>
        public abstract float RunTest(EQSContext ctx, EQSQueryItem item);

        /// <summary>应用得分曲线。</summary>
        protected float ApplyCurve(float rawValue, float minValue, float maxValue)
        {
            float normalized = maxValue > minValue
                ? Math.Clamp((rawValue - minValue) / (maxValue - minValue), 0f, 1f)
                : 0f;

            switch (CurveType)
            {
                case EQSCurveType.Linear: return normalized;
                case EQSCurveType.InverseLinear: return 1f - normalized;
                case EQSCurveType.Constant: return 0.5f;
                case EQSCurveType.Sine: return MathF.Sin(normalized * MathF.PI / 2f);
                case EQSCurveType.Parabolic: return normalized * normalized;
                default: return normalized;
            }
        }

        /// <summary>钳制得分。</summary>
        protected float ClampScore(float score)
        {
            return Math.Clamp(score, MinScoreClamp, MaxScoreClamp);
        }

        public virtual EQSTest Clone()
        {
            var clone = (EQSTest)Activator.CreateInstance(GetType());
            clone.Weight = Weight;
            clone.MinScoreClamp = MinScoreClamp;
            clone.MaxScoreClamp = MaxScoreClamp;
            clone.CurveType = CurveType;
            clone.CanBeDisabled = CanBeDisabled;
            return clone;
        }
    }

    /// <summary>距离测试。距离 -> 得分曲线。</summary>
    public sealed class EQSTest_Distance : EQSTest
    {
        public float MinDistance { get; set; } = 0f;
        public float MaxDistance { get; set; } = 500f;
        public bool PreferCloser { get; set; } = true;

        public EQSTest_Distance()
        {
            TestName = "Distance";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            float dist = Vector3.Distance(ctx.QuerierPosition, item.Position);
            float score = ApplyCurve(dist, MinDistance, MaxDistance);
            if (PreferCloser) score = 1f - score;
            return ClampScore(score * Weight);
        }
    }

    /// <summary>射线检测测试。可见性/遮挡/穿透数。</summary>
    public sealed class EQSTest_Trace : EQSTest
    {
        public float MaxTraceDistance { get; set; } = 500f;
        public int MaxPenetrations { get; set; } = 0;
        public float VisibilityScore { get; set; } = 1f;
        public float BlockedScore { get; set; } = 0f;

        public EQSTest_Trace()
        {
            TestName = "Trace";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            float dist = Vector3.Distance(ctx.QuerierPosition, item.Position);
            if (dist > MaxTraceDistance) return ClampScore(BlockedScore * Weight);

            if (ctx.RaycastCheck != null)
            {
                bool blocked = ctx.RaycastCheck(ctx.QuerierPosition, item.Position);
                return ClampScore(blocked ? BlockedScore * Weight : VisibilityScore * Weight);
            }

            return ClampScore(VisibilityScore * Weight);
        }
    }

    /// <summary>点乘测试。朝向/夹角。</summary>
    public sealed class EQSTest_Dot : EQSTest
    {
        public float MinDot { get; set; } = 0f;
        public float MaxDot { get; set; } = 1f;

        public EQSTest_Dot()
        {
            TestName = "Dot";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            var toItem = item.Position - ctx.QuerierPosition;
            if (toItem.LengthSquared() < 0.0001f) return ClampScore(Weight);

            toItem = Vector3.Normalize(toItem);
            float dot = Vector3.Dot(ctx.QuerierForward, toItem);
            float score = ApplyCurve(dot, MinDot, MaxDot);
            return ClampScore(score * Weight);
        }
    }

    /// <summary>角度测试。</summary>
    public sealed class EQSTest_Angle : EQSTest
    {
        public float MinAngle { get; set; } = 0f;
        public float MaxAngle { get; set; } = 180f;
        public bool PreferSmallerAngle { get; set; } = true;

        public EQSTest_Angle()
        {
            TestName = "Angle";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            var toItem = item.Position - ctx.QuerierPosition;
            if (toItem.LengthSquared() < 0.0001f) return ClampScore(Weight);

            toItem = Vector3.Normalize(toItem);
            float dot = Vector3.Dot(ctx.QuerierForward, toItem);
            float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * (180f / MathF.PI);

            float score = ApplyCurve(angle, MinAngle, MaxAngle);
            if (PreferSmallerAngle) score = 1f - score;
            return ClampScore(score * Weight);
        }
    }

    /// <summary>路径花费测试。</summary>
    public sealed class EQSTest_PathCost : EQSTest
    {
        public float MaxPathCost { get; set; } = 1000f;

        public EQSTest_PathCost()
        {
            TestName = "PathCost";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            float cost = 0f;
            if (ctx.PathCostQuery != null)
                cost = ctx.PathCostQuery(ctx.QuerierPosition, item.Position);

            float score = ApplyCurve(cost, 0f, MaxPathCost);
            score = 1f - score; // 越低花费越好
            return ClampScore(score * Weight);
        }
    }

    /// <summary>黑板值测试。</summary>
    public sealed class EQSTest_Blackboard : EQSTest
    {
        public string KeyName { get; set; } = "";
        public float ExpectedValue { get; set; } = 1f;
        public float Tolerance { get; set; } = 0.1f;

        public EQSTest_Blackboard()
        {
            TestName = "Blackboard";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            if (ctx.Blackboard == null || string.IsNullOrEmpty(KeyName))
                return ClampScore(0f);

            float value = ctx.Blackboard.GetValue<float>(KeyName);
            float diff = Math.Abs(value - ExpectedValue);
            float score = diff <= Tolerance ? 1f : Math.Max(0f, 1f - (diff - Tolerance) / (ExpectedValue + 1f));
            return ClampScore(score * Weight);
        }
    }

    /// <summary>随机噪声测试。</summary>
    public sealed class EQSTest_Noise : EQSTest
    {
        public float NoiseMagnitude { get; set; } = 0.5f;
        private readonly Random _rng = new Random();

        public EQSTest_Noise()
        {
            TestName = "Noise";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            float noise = (float)(_rng.NextDouble() * 2 - 1) * NoiseMagnitude;
            return ClampScore((0.5f + noise) * Weight);
        }
    }

    /// <summary>高度差测试。</summary>
    public sealed class EQSTest_Height : EQSTest
    {
        public float PreferredHeight { get; set; } = 0f; // 相对查询者
        public float MaxHeightDifference { get; set; } = 20f;
        public bool PreferHigher { get; set; } = false;

        public EQSTest_Height()
        {
            TestName = "Height";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            float heightDiff = item.Position.Y - ctx.QuerierPosition.Y;
            float score = ApplyCurve(Math.Abs(heightDiff), 0f, MaxHeightDifference);
            score = 1f - score;

            if (PreferHigher && heightDiff > 0f)
                score += 0.3f;

            return ClampScore(score * Weight);
        }
    }

    /// <summary>遮盖测试。检查候选点是否有掩体保护。</summary>
    public sealed class EQSTest_Cover : EQSTest
    {
        public float CoverCheckDistance { get; set; } = 3f;
        public float CoverCheckRadius { get; set; } = 1f;
        public float NoCoverScore { get; set; } = 0f;
        public float HasCoverScore { get; set; } = 1f;

        public EQSTest_Cover()
        {
            TestName = "Cover";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            // 简化遮盖检测：检查候选点到查询者方向上是否有遮挡物
            if (ctx.RaycastCheck != null)
            {
                var dir = item.Position - ctx.QuerierPosition;
                if (dir.LengthSquared() > 0.0001f)
                {
                    dir = Vector3.Normalize(dir);
                    var checkPoint = item.Position + dir * CoverCheckDistance;
                    bool blocked = ctx.RaycastCheck(item.Position, checkPoint);
                    return ClampScore(blocked ? HasCoverScore * Weight : NoCoverScore * Weight);
                }
            }

            return ClampScore(NoCoverScore * Weight);
        }
    }

    /// <summary>多次采样取最佳/平均/最差。</summary>
    public sealed class EQSTest_Multiple : EQSTest
    {
        public EQSTest InnerTest { get; set; }
        public int SampleCount { get; set; } = 5;
        public EQSMultipleSampling SamplingStrategy { get; set; } = EQSMultipleSampling.Best;

        public EQSTest_Multiple()
        {
            TestName = "Multiple";
        }

        public override float RunTest(EQSContext ctx, EQSQueryItem item)
        {
            if (InnerTest == null) return 0f;

            float best = float.MinValue;
            float worst = float.MaxValue;
            float sum = 0f;

            for (int i = 0; i < SampleCount; i++)
            {
                // 给项目位置添加微小偏移来模拟多次采样
                var offsetItem = new EQSQueryItem(item.Position, item.AssociatedActor);
                float score = InnerTest.RunTest(ctx, offsetItem);

                best = Math.Max(best, score);
                worst = Math.Min(worst, score);
                sum += score;
            }

            switch (SamplingStrategy)
            {
                case EQSMultipleSampling.Best: return ClampScore(best * Weight);
                case EQSMultipleSampling.Average: return ClampScore((sum / SampleCount) * Weight);
                case EQSMultipleSampling.Worst: return ClampScore(worst * Weight);
                default: return ClampScore((sum / SampleCount) * Weight);
            }
        }

        public override EQSTest Clone()
        {
            var clone = (EQSTest_Multiple)base.Clone();
            clone.InnerTest = InnerTest?.Clone();
            clone.SampleCount = SampleCount;
            clone.SamplingStrategy = SamplingStrategy;
            return clone;
        }
    }

    // ========================================================================
    //  六、EQS 查询 (EQSQuery)
    // ========================================================================

    /// <summary>
    /// EQS 查询请求。组合生成器和测试器完成一次完整的环境查询。
    /// </summary>
    public sealed class EQSQuery
    {
        public Guid QueryId { get; } = Guid.NewGuid();
        public string QueryName { get; set; } = "UnnamedQuery";

        /// <summary>生成器。</summary>
        public EQSGenerator Generator { get; set; }
        /// <summary>测试器列表。</summary>
        public List<EQSTest> Tests { get; } = new List<EQSTest>();
        /// <summary>最大有效结果数量（0=不限）。</summary>
        public int MaxResults { get; set; } = 0;

        /// <summary>执行查询。</summary>
        public EQSResult Execute(EQSContext ctx)
        {
            if (Generator == null || !Generator.IsEnabled)
                return new EQSResult();

            // 1. 生成候选项目
            var items = Generator.GenerateItems(ctx);
            if (items.Count == 0)
                return new EQSResult();

            // 2. 运行所有测试
            foreach (var item in items)
            {
                item.RawScore = 0f;
                float totalWeight = 0f;

                foreach (var test in Tests)
                {
                    if (!test.IsEnabled) continue;
                    float score = test.RunTest(ctx, item);
                    item.RawScore += score;
                    totalWeight += test.Weight;
                }

                item.WeightedScore = totalWeight > 0f ? item.RawScore / totalWeight : 0f;
            }

            // 3. 构建结果
            var result = new EQSResult();
            result.SetItems(items);
            return result;
        }

        /// <summary>添加测试器。</summary>
        public T AddTest<T>(T test) where T : EQSTest
        {
            Tests.Add(test);
            return test;
        }

        /// <summary>克隆查询。</summary>
        public EQSQuery Clone()
        {
            var clone = new EQSQuery { QueryName = QueryName, MaxResults = MaxResults };
            if (Generator != null)
                clone.Generator = Generator.Clone();
            foreach (var test in Tests)
                clone.Tests.Add(test.Clone());
            return clone;
        }
    }

    // ========================================================================
    //  七、EQS 管理器 (EQSManager)
    // ========================================================================

    /// <summary>
    /// EQS 管理器。查询注册/执行/缓存。
    /// </summary>
    public sealed class EQSManager
    {
        private static readonly Lazy<EQSManager> _instance =
            new Lazy<EQSManager>(() => new EQSManager());
        public static EQSManager Instance => _instance.Value;

        private readonly Dictionary<string, EQSQuery> _registeredQueries =
            new Dictionary<string, EQSQuery>();

        /// <summary>查询结果缓存。</summary>
        private readonly Dictionary<string, (EQSResult Result, float Timestamp)> _cache =
            new Dictionary<string, (EQSResult, float)>();

        /// <summary>缓存有效期（秒）。</summary>
        public float CacheDuration { get; set; } = 0.5f;

        /// <summary>是否启用缓存。</summary>
        public bool EnableCache { get; set; } = true;

        /// <summary>注册查询模板。</summary>
        public void RegisterQuery(string name, EQSQuery query)
        {
            if (!string.IsNullOrEmpty(name) && query != null)
                _registeredQueries[name] = query;
        }

        /// <summary>注销查询模板。</summary>
        public void UnregisterQuery(string name)
        {
            _registeredQueries.Remove(name);
            _cache.Remove(name);
        }

        /// <summary>执行命名查询。</summary>
        public EQSResult ExecuteQuery(string name, EQSContext ctx)
        {
            if (!_registeredQueries.TryGetValue(name, out var query))
                return new EQSResult();

            return ExecuteQuery(query, name, ctx);
        }

        /// <summary>执行查询。</summary>
        public EQSResult ExecuteQuery(EQSQuery query, string cacheKey, EQSContext ctx)
        {
            // 检查缓存
            if (EnableCache && !string.IsNullOrEmpty(cacheKey))
            {
                if (_cache.TryGetValue(cacheKey, out var cached) &&
                    ctx.CurrentTime - cached.Timestamp < CacheDuration)
                {
                    return cached.Result;
                }
            }

            var result = query.Execute(ctx);

            // 存入缓存
            if (EnableCache && !string.IsNullOrEmpty(cacheKey))
                _cache[cacheKey] = (result, ctx.CurrentTime);

            return result;
        }

        /// <summary>清除缓存。</summary>
        public void ClearCache()
        {
            _cache.Clear();
        }

        /// <summary>清除过期缓存。</summary>
        public void ClearExpiredCache(float currentTime)
        {
            var expired = new List<string>();
            foreach (var kv in _cache)
            {
                if (currentTime - kv.Value.Timestamp >= CacheDuration)
                    expired.Add(kv.Key);
            }
            foreach (var key in expired)
                _cache.Remove(key);
        }

        /// <summary>获取所有已注册查询名称。</summary>
        public List<string> GetRegisteredQueryNames()
        {
            return _registeredQueries.Keys.ToList();
        }
    }
}
