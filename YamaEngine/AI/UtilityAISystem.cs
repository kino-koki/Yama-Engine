// ============================================================================
//  YamaEngine.AI.UtilityAISystem.cs
//  yama引擎 -- 效用 AI 系统 (Utility AI)
//  对齐 Unreal Mass / Dave Mark's Utility AI / Sims 4
// ----------------------------------------------------------------------------
//  效用 AI 不用状态机也不用行为树，而是每帧给每个候选 action 算一个 0~1 的
//  "效用分数"(基于多条曲线组合)，选分数最高的执行。
//
//  本文件实现：
//    1. ResponseCurve       - 响应曲线(线性/对数/指数/S曲线/高斯/阶梯)
//    2. Consideration       - 考虑因素(input -> curve -> score)
//    3. UtilityAction       - 候选动作(多个 consideration 用复合方式合并)
//    4. ScoringMethod       - 分数合并策略(乘/加/几何平均/最小值)
//    5. UtilityAIBrain      - 大脑(每帧评分选 action)
//    6. UtilityAIContext    - 上下文(提供 input 数值)
//    7. UtilityAICombiner   - 分数组合器(带权重和锚点)
// ----------------------------------------------------------------------------
//  Sims 系列就是这种思路，能让 AI 行为看起来很"自然"。
// ============================================================================
using System;
using System.Collections.Generic;

namespace YamaEngine.AI
{
    // ========================================================================
    //  一、响应曲线
    // ========================================================================

    /// <summary>曲线类型。</summary>
    public enum CurveType
    {
        Linear,
        Logistic,    // S 曲线
        Exponential,
        Logarithmic,
        Gaussian,    // 钟形
        Step,        // 阶跃
        Quadratic,
        InverseLinear,
        Custom
    }

    /// <summary>
    /// 响应曲线。把 input(0~1) 映射到 score(0~1)。
    /// 阈值/斜率/中心点等参数控制曲线形状。
    /// </summary>
    public struct ResponseCurve
    {
        public CurveType Type;
        public float Slope;       // 斜率
        public float Exponent;    // 指数(曲线弯曲程度)
        public float Center;      // 中心点(0.5=中点)
        public float Threshold;   // 阈值(Step 用)
        public float Width;       // 宽度(Gaussian 用)
        public Func<float, float>? CustomFn;

        public static ResponseCurve Linear(float slope = 1f, float center = 0f)
            => new() { Type = CurveType.Linear, Slope = slope, Center = center };

        public static ResponseCurve Logistic(float slope = 4f, float center = 0.5f)
            => new() { Type = CurveType.Logistic, Slope = slope, Center = center };

        public static ResponseCurve Exponential(float exponent = 2f)
            => new() { Type = CurveType.Exponential, Exponent = exponent };

        public static ResponseCurve Logarithmic()
            => new() { Type = CurveType.Logarithmic };

        public static ResponseCurve Gaussian(float center = 0.5f, float width = 0.2f)
            => new() { Type = CurveType.Gaussian, Center = center, Width = width };

        public static ResponseCurve Step(float threshold = 0.5f)
            => new() { Type = CurveType.Step, Threshold = threshold };

        public static ResponseCurve InverseLinear(float slope = -1f, float center = 1f)
            => new() { Type = CurveType.InverseLinear, Slope = slope, Center = center };

        public static ResponseCurve Custom(Func<float, float> fn)
            => new() { Type = CurveType.Custom, CustomFn = fn };

        /// <summary>对 input 求值，返回 0~1。</summary>
        public float Evaluate(float input)
        {
            input = Math.Clamp(input, 0f, 1f);
            float result = Type switch
            {
                CurveType.Linear => Slope * (input - Center) + 0.5f,
                CurveType.Logistic => 1f / (1f + MathF.Exp(-Slope * (input - Center))),
                CurveType.Exponential => MathF.Pow(input, Exponent),
                CurveType.Logarithmic => MathF.Log(1f + 9f * input) / MathF.Log(10f),
                CurveType.Gaussian => MathF.Exp(-MathF.Pow((input - Center) / Width, 2f)),
                CurveType.Step => input >= Threshold ? 1f : 0f,
                CurveType.Quadratic => input * input,
                CurveType.InverseLinear => Math.Clamp(Slope * (input - Center) + 0.5f, 0f, 1f),
                CurveType.Custom => CustomFn?.Invoke(input) ?? 0f,
                _ => input
            };
            return Math.Clamp(result, 0f, 1f);
        }
    }

    // ========================================================================
    //  二、考虑因素
    // ========================================================================

    /// <summary>
    /// 考虑因素。一个 input 值经过 curve 转换得到一个 0~1 分数。
    /// 比如 "血量低 -> 想治疗的分数高"。
    /// </summary>
    public sealed class Consideration
    {
        public string Name;
        /// <summary>从上下文获取 input 值(0~1 归一化)。</summary>
        public Func<UtilityAIContext, float> InputFn;
        /// <summary>响应曲线。</summary>
        public ResponseCurve Curve;
        /// <summary>权重(影响最终分数)。</summary>
        public float Weight = 1f;
        /// <summary>是否启用。</summary>
        public bool Enabled = true;
        /// <summary>逆操作(input 反转，比如把"血量高"转成"血量低")。</summary>
        public bool InvertInput = false;

        public Consideration(string name, Func<UtilityAIContext, float> inputFn, ResponseCurve curve)
        {
            Name = name; InputFn = inputFn; Curve = curve;
        }

        /// <summary>计算分数。</summary>
        public float Score(UtilityAIContext ctx)
        {
            if (!Enabled) return 1f;
            float input = InputFn(ctx);
            if (InvertInput) input = 1f - input;
            input = Math.Clamp(input, 0f, 1f);
            float s = Curve.Evaluate(input);
            // 应用权重(用幂函数)
            if (Weight != 1f && Weight > 0)
                s = MathF.Pow(s, Weight);
            return s;
        }

        public override string ToString() => $"Consideration('{Name}', w={Weight})";
    }

    // ========================================================================
    //  三、分数合并策略
    // ========================================================================

    /// <summary>多个 consideration 分数合并方式。</summary>
    public enum ScoringMethod
    {
        /// <summary>乘积(任一为 0 则整体为 0，最严格)。</summary>
        Multiply,
        /// <summary>加权平均(宽容)。</summary>
        WeightedAverage,
        /// <summary>几何平均(对乘积取根，比 Multiply 温和)。</summary>
        GeometricMean,
        /// <summary>取最小(短板效应)。</summary>
        Minimum,
        /// <summary>取最大。</summary>
        Maximum,
        /// <summary>加权和。</summary>
        Sum
    }

    // ========================================================================
    //  四、效用动作
    // ========================================================================

    /// <summary>
    /// 效用动作。包含多个考虑因素，最终分数决定是否被选中。
    /// </summary>
    public sealed class UtilityAction
    {
        public string Name;
        public List<Consideration> Considerations = new();
        public ScoringMethod Method = ScoringMethod.Multiply;
        /// <summary>动作基础权重(在所有 action 之间的相对权重)。</summary>
        public float BaseWeight = 1f;
        /// <summary>动作的执行器。</summary>
        public Action<UtilityAIContext>? Executor;
        /// <summary>是否当前可执行。</summary>
        public Func<UtilityAIContext, bool>? CanExecute;
        /// <summary>执行冷却(秒)。</summary>
        public float CoolDown = 0f;
        /// <summary>上次执行时间。</summary>
        public float LastExecutedTime = -1e9f;
        /// <summary>是否在持续执行(每帧 tick)。</summary>
        public bool IsContinuous = false;
        /// <summary>持续执行时的结束条件。</summary>
        public Func<UtilityAIContext, bool>? ShouldStop;
        /// <summary>加锚点(避免一个 consideration 为 0 直接清零整体)。</summary>
        public float Anchor = 0f; // 0=无锚点
        /// <summary>最小分数阈值(低于此分数不参与选择)。</summary>
        public float MinScoreThreshold = 0f;
        /// <summary>当前分数(调试用)。</summary>
        public float CurrentScore;

        public UtilityAction(string name) { Name = name; }

        public UtilityAction AddConsideration(Consideration c)
        {
            Considerations.Add(c);
            return this;
        }

        /// <summary>计算最终分数。</summary>
        public float CalculateScore(UtilityAIContext ctx)
        {
            if (Considerations.Count == 0) return 1f;

            float score = Method switch
            {
                ScoringMethod.Multiply => CalcMultiply(ctx),
                ScoringMethod.WeightedAverage => CalcWeightedAverage(ctx),
                ScoringMethod.GeometricMean => CalcGeometricMean(ctx),
                ScoringMethod.Minimum => CalcMinimum(ctx),
                ScoringMethod.Maximum => CalcMaximum(ctx),
                ScoringMethod.Sum => CalcSum(ctx),
                _ => CalcMultiply(ctx)
            };

            // 应用锚点(防止 0 分清零)
            if (Anchor > 0)
                score = MathF.Max(score, Anchor * 0.1f);

            score *= BaseWeight;
            CurrentScore = score;
            return score;
        }

        private float CalcMultiply(UtilityAIContext ctx)
        {
            float product = 1f;
            foreach (var c in Considerations)
            {
                if (!c.Enabled) continue;
                product *= c.Score(ctx);
            }
            return product;
        }

        private float CalcWeightedAverage(UtilityAIContext ctx)
        {
            float sum = 0, totalW = 0;
            foreach (var c in Considerations)
            {
                if (!c.Enabled) continue;
                float w = c.Weight;
                sum += c.Score(ctx) * w;
                totalW += w;
            }
            return totalW > 0 ? sum / totalW : 0f;
        }

        private float CalcGeometricMean(UtilityAIContext ctx)
        {
            float product = 1f;
            int n = 0;
            foreach (var c in Considerations)
            {
                if (!c.Enabled) continue;
                product *= c.Score(ctx);
                n++;
            }
            if (n == 0) return 0f;
            return MathF.Pow(product, 1f / n);
        }

        private float CalcMinimum(UtilityAIContext ctx)
        {
            float min = 1f;
            foreach (var c in Considerations)
            {
                if (!c.Enabled) continue;
                min = MathF.Min(min, c.Score(ctx));
            }
            return min;
        }

        private float CalcMaximum(UtilityAIContext ctx)
        {
            float max = 0f;
            foreach (var c in Considerations)
            {
                if (!c.Enabled) continue;
                max = MathF.Max(max, c.Score(ctx));
            }
            return max;
        }

        private float CalcSum(UtilityAIContext ctx)
        {
            float sum = 0;
            foreach (var c in Considerations)
            {
                if (!c.Enabled) continue;
                sum += c.Score(ctx);
            }
            return Math.Clamp(sum / Considerations.Count, 0f, 1f);
        }

        /// <summary>是否可执行(冷却 + CanExecute 检查)。</summary>
        public bool CanRun(UtilityAIContext ctx)
        {
            if (ctx.CurrentTime - LastExecutedTime < CoolDown) return false;
            if (CanExecute != null && !CanExecute(ctx)) return false;
            return true;
        }

        /// <summary>执行动作。</summary>
        public void Execute(UtilityAIContext ctx)
        {
            LastExecutedTime = ctx.CurrentTime;
            Executor?.Invoke(ctx);
        }

        public override string ToString() => $"UtilityAction('{Name}', score={CurrentScore:F3})";
    }

    // ========================================================================
    //  五、上下文
    // ========================================================================

    /// <summary>
    /// Utility AI 上下文。提供 consideration 需要的 input。
    /// 由 agent 系统每帧填充。
    /// </summary>
    public sealed class UtilityAIContext
    {
        public int AgentId;
        public float CurrentTime;
        public object? AgentObject;
        /// <summary>关键变量缓存(给 InputFn 快速访问)。</summary>
        public Dictionary<string, float> Variables = new();
        /// <summary>外部提供的数据(感知/记忆/世界状态)。</summary>
        public Dictionary<string, object> Memory = new();

        public UtilityAIContext(int agentId) { AgentId = agentId; }

        public float GetVar(string key, float def = 0f)
            => Variables.TryGetValue(key, out var v) ? v : def;
        public void SetVar(string key, float v) => Variables[key] = v;

        public T? GetMemory<T>(string key) where T : class
            => Memory.TryGetValue(key, out var v) && v is T t ? t : null;

        public void SetMemory(string key, object value) => Memory[key] = value;
    }

    // ========================================================================
    //  六、大脑
    // ========================================================================

    /// <summary>
    /// Utility AI 大脑。每帧给所有 action 评分，选最高分执行。
    /// </summary>
    public sealed class UtilityAIBrain
    {
        private readonly List<UtilityAction> _actions = new();
        private UtilityAction? _currentAction;
        private float _decisionTimer;
        private UtilityAIContext? _context;

        /// <summary>当前执行的动作。</summary>
        public UtilityAction? CurrentAction => _currentAction;
        /// <summary>决策间隔(秒)。0=每帧决策，>0=按间隔。</summary>
        public float DecisionInterval = 0.2f;
        /// <summary>选择策略。</summary>
        public SelectionStrategy Strategy = SelectionStrategy.HighestScore;
        /// <summary>历史加权(避免动作频繁切换)。越大越粘。</summary>
        public float InertiaWeight = 0.1f;
        /// <summary>随机探索概率(偶尔选非最优，增加多样性)。</summary>
        public float ExplorationRate = 0f;
        /// <summary>当前最优分数(调试)。</summary>
        public float BestScore;

        public int ActionCount => _actions.Count;
        public IReadOnlyList<UtilityAction> Actions => _actions;

        public event Action<UtilityAction?>? OnActionChanged;

        private Random _rng = new();

        public UtilityAIBrain() { }

        public void AddAction(UtilityAction action) => _actions.Add(action);
        public void RemoveAction(string name) => _actions.RemoveAll(a => a.Name == name);
        public void ClearActions() => _actions.Clear();
        public void SetContext(UtilityAIContext context) => _context = context;

        /// <summary>每帧更新。</summary>
        public void Update(float dt)
        {
            if (_context == null) return;
            _context.CurrentTime += dt;

            // 持续动作的检查
            if (_currentAction != null && _currentAction.IsContinuous)
            {
                if (_currentAction.ShouldStop != null && _currentAction.ShouldStop(_context))
                {
                    SwitchTo(null);
                }
                else
                {
                    _currentAction.Execute(_context);
                    // 持续动作期间也按间隔重新评分(可能切到更高分的)
                    _decisionTimer += dt;
                    if (_decisionTimer >= DecisionInterval)
                    {
                        _decisionTimer = 0;
                        TryDecision();
                    }
                    return;
                }
            }

            _decisionTimer += dt;
            if (_decisionTimer >= DecisionInterval)
            {
                _decisionTimer = 0;
                TryDecision();
            }

            // 执行当前动作(非持续的每帧执行一次)
            if (_currentAction != null && !_currentAction.IsContinuous)
            {
                _currentAction.Execute(_context);
            }
        }

        private void TryDecision()
        {
            if (_context == null) return;
            UtilityAction? best = null;
            float bestScore = 0f;

            foreach (var action in _actions)
            {
                if (!action.CanRun(_context)) continue;
                float score = action.CalculateScore(_context);
                if (score < action.MinScoreThreshold) continue;
                // 惯性：当前动作加分
                if (action == _currentAction)
                    score += InertiaWeight;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                }
            }

            // 随机探索
            if (ExplorationRate > 0 && _rng.NextDouble() < ExplorationRate)
            {
                var candidates = new List<UtilityAction>();
                foreach (var a in _actions)
                    if (a.CanRun(_context) && a.CalculateScore(_context) > a.MinScoreThreshold)
                        candidates.Add(a);
                if (candidates.Count > 0)
                    best = candidates[_rng.Next(candidates.Count)];
            }

            BestScore = bestScore;
            if (best != _currentAction)
                SwitchTo(best);
        }

        private void SwitchTo(UtilityAction? newAction)
        {
            _currentAction = newAction;
            OnActionChanged?.Invoke(newAction);
        }

        /// <summary>强制切换到指定动作(无视评分)。</summary>
        public void ForceAction(string name)
        {
            var a = _actions.Find(x => x.Name == name);
            if (a != null) SwitchTo(a);
        }

        /// <summary>停止当前动作。</summary>
        public void Stop() => SwitchTo(null);

        /// <summary>获取所有动作的当前分数(调试/UI 用)。</summary>
        public List<(string name, float score)> GetAllScores()
        {
            var result = new List<(string, float)>();
            if (_context == null) return result;
            foreach (var a in _actions)
            {
                if (!a.CanRun(_context)) { result.Add((a.Name, 0f)); continue; }
                result.Add((a.Name, a.CalculateScore(_context)));
            }
            return result;
        }
    }

    /// <summary>动作选择策略。</summary>
    public enum SelectionStrategy
    {
        HighestScore,
        WeightedRandom,   // 按分数加权随机
        Softmax,          // softmax 采样
        Tournament        // 锦标赛选择
    }

    // ========================================================================
    //  七、常见 Consideration 预设
    // ========================================================================

    /// <summary>常见考虑因素预设。</summary>
    public static class ConsiderationPresets
    {
        /// <summary>血量低 -> 高分(想治疗)。</summary>
        public static Consideration LowHealth(Func<UtilityAIContext, float> healthFn)
            => new("LowHealth", ctx => 1f - healthFn(ctx), ResponseCurve.Logistic(4f, 0.3f));

        /// <summary>血量满 -> 高分(可攻击)。</summary>
        public static Consideration FullHealth(Func<UtilityAIContext, float> healthFn)
            => new("FullHealth", healthFn, ResponseCurve.Linear(1f, 0f));

        /// <summary>敌人近距离 -> 高分(可攻击)。</summary>
        public static Consideration EnemyClose(Func<UtilityAIContext, float> distFn)
            => new("EnemyClose", ctx => 1f - distFn(ctx), ResponseCurve.Exponential(2f));

        /// <summary>敌人远距离 -> 高分(需追击)。</summary>
        public static Consideration EnemyFar(Func<UtilityAIContext, float> distFn)
            => new("EnemyFar", distFn, ResponseCurve.Linear(1f, 0f));

        /// <summary>弹药充足 -> 高分(可射击)。</summary>
        public static Consideration HasAmmo(Func<UtilityAIContext, float> ammoFn)
            => new("HasAmmo", ammoFn, ResponseCurve.Linear(2f, 0f));

        /// <summary>弹药少 -> 高分(需装弹)。</summary>
        public static Consideration LowAmmo(Func<UtilityAIContext, float> ammoFn)
            => new("LowAmmo", ctx => 1f - ammoFn(ctx), ResponseCurve.Logistic(6f, 0.2f));

        /// <summary>目标可见 -> 高分。</summary>
        public static Consideration TargetVisible(Func<UtilityAIContext, bool> visibleFn)
            => new("TargetVisible", ctx => visibleFn(ctx) ? 1f : 0f, ResponseCurve.Step(0.5f));

        /// <summary>威胁等级高 -> 高分(需逃跑)。</summary>
        public static Consideration HighThreat(Func<UtilityAIContext, float> threatFn)
            => new("HighThreat", threatFn, ResponseCurve.Exponential(3f));

        /// <summary>疲劳度高 -> 高分(需休息)。</summary>
        public static Consideration HighFatigue(Func<UtilityAIContext, float> fatigueFn)
            => new("HighFatigue", fatigueFn, ResponseCurve.Logistic(4f, 0.5f));

        /// <summary>附近有掩体 -> 高分(可躲藏)。</summary>
        public static Consideration CoverAvailable(Func<UtilityAIContext, float> coverFn)
            => new("CoverAvailable", coverFn, ResponseCurve.Step(0.3f));

        /// <summary>饥饿 -> 高分(需觅食)。</summary>
        public static Consideration Hungry(Func<UtilityAIContext, float> hungerFn)
            => new("Hungry", hungerFn, ResponseCurve.Logistic(5f, 0.4f));

        /// <summary>空闲 -> 高分(可游荡)。</summary>
        public static Consideration Idle(Func<UtilityAIContext, bool> idleFn)
            => new("Idle", ctx => idleFn(ctx) ? 1f : 0f, ResponseCurve.Step(0.5f));
    }

    // ========================================================================
    //  八、常用动作预设
    // ========================================================================

    /// <summary>常见效用动作预设。</summary>
    public static class UtilityActionPresets
    {
        /// <summary>攻击动作。</summary>
        public static UtilityAction Attack(
            Func<UtilityAIContext, float> healthFn,
            Func<UtilityAIContext, float> distFn,
            Func<UtilityAIContext, float> ammoFn,
            Func<UtilityAIContext, bool> visibleFn,
            Action<UtilityAIContext> executor)
        {
            var a = new UtilityAction("Attack") { BaseWeight = 1.2f, CoolDown = 0.5f };
            a.AddConsideration(ConsiderationPresets.FullHealth(healthFn).With(w => w.Weight = 0.5f));
            a.AddConsideration(ConsiderationPresets.EnemyClose(distFn).With(w => w.Weight = 1.5f));
            a.AddConsideration(ConsiderationPresets.HasAmmo(ammoFn).With(w => w.Weight = 1f));
            a.AddConsideration(ConsiderationPresets.TargetVisible(visibleFn));
            a.Executor = executor;
            a.Method = ScoringMethod.Multiply;
            a.Anchor = 0.05f;
            return a;
        }

        /// <summary>逃跑动作。</summary>
        public static UtilityAction Flee(
            Func<UtilityAIContext, float> healthFn,
            Func<UtilityAIContext, float> threatFn,
            Action<UtilityAIContext> executor)
        {
            var a = new UtilityAction("Flee") { BaseWeight = 1.5f };
            a.AddConsideration(ConsiderationPresets.LowHealth(healthFn).With(w => w.Weight = 2f));
            a.AddConsideration(ConsiderationPresets.HighThreat(threatFn).With(w => w.Weight = 2f));
            a.Executor = executor;
            a.Method = ScoringMethod.Multiply;
            return a;
        }

        /// <summary>装弹动作。</summary>
        public static UtilityAction Reload(
            Func<UtilityAIContext, float> ammoFn,
            Func<UtilityAIContext, bool> inCombatFn,
            Action<UtilityAIContext> executor)
        {
            var a = new UtilityAction("Reload") { CoolDown = 2f };
            a.AddConsideration(ConsiderationPresets.LowAmmo(ammoFn).With(w => w.Weight = 2f));
            a.AddConsideration(new Consideration("NotInCombat", ctx => inCombatFn(ctx) ? 0f : 1f, ResponseCurve.Step(0.5f)));
            a.Executor = executor;
            a.Method = ScoringMethod.Multiply;
            return a;
        }

        /// <summary>游荡动作。</summary>
        public static UtilityAction Wander(
            Func<UtilityAIContext, bool> idleFn,
            Func<UtilityAIContext, float> threatFn,
            Action<UtilityAIContext> executor)
        {
            var a = new UtilityAction("Wander") { BaseWeight = 0.5f, CoolDown = 5f };
            a.AddConsideration(ConsiderationPresets.Idle(idleFn));
            a.AddConsideration(new Consideration("LowThreat", ctx => 1f - threatFn(ctx), ResponseCurve.Linear(1f, 0f)));
            a.Executor = executor;
            a.Method = ScoringMethod.Multiply;
            return a;
        }

        /// <summary>治疗动作。</summary>
        public static UtilityAction Heal(
            Func<UtilityAIContext, float> healthFn,
            Func<UtilityAIContext, float> hasItemFn,
            Action<UtilityAIContext> executor)
        {
            var a = new UtilityAction("Heal") { CoolDown = 3f };
            a.AddConsideration(ConsiderationPresets.LowHealth(healthFn).With(w => w.Weight = 2f));
            a.AddConsideration(new Consideration("HasHealItem", hasItemFn, ResponseCurve.Step(0.5f)));
            a.Executor = executor;
            a.Method = ScoringMethod.Multiply;
            return a;
        }
    }

    /// <summary>Consideration 配置扩展方法。</summary>
    public static class ConsiderationExtensions
    {
        public static Consideration With(this Consideration c, Action<Consideration> config)
        {
            config(c);
            return c;
        }
    }

    // ========================================================================
    //  九、多 agent 管理
    // ========================================================================

    /// <summary>
    /// Utility AI 管理器。管理多个 agent 的大脑。
    /// </summary>
    public sealed class UtilityAIManager
    {
        private readonly Dictionary<int, UtilityAIBrain> _brains = new();
        private readonly Dictionary<int, UtilityAIContext> _contexts = new();

        public int Count => _brains.Count;
        public float GlobalDecisionInterval { get; set; } = 0.2f;

        public void Register(int agentId, UtilityAIBrain brain, UtilityAIContext context)
        {
            brain.SetContext(context);
            if (brain.DecisionInterval == 0)
                brain.DecisionInterval = GlobalDecisionInterval;
            _brains[agentId] = brain;
            _contexts[agentId] = context;
        }

        public void Unregister(int agentId)
        {
            _brains.Remove(agentId);
            _contexts.Remove(agentId);
        }

        public UtilityAIBrain? GetBrain(int agentId)
            => _brains.TryGetValue(agentId, out var b) ? b : null;

        public void Update(float dt)
        {
            foreach (var (id, brain) in _brains)
                brain.Update(dt);
        }

        public void SetVariable(int agentId, string key, float value)
        {
            if (_contexts.TryGetValue(agentId, out var ctx))
                ctx.SetVar(key, value);
        }

        public void BroadcastVariable(string key, float value)
        {
            foreach (var ctx in _contexts.Values)
                ctx.SetVar(key, value);
        }
    }
}
