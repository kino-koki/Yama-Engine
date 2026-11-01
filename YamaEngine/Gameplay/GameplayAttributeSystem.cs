// YamaEngine - yama游戏引擎
// GameplayAttributeSystem.cs - 属性系统 属性计算
// 属性系统管理角色的所有数值属性 生命 攻击 防御等
// 支持基础值 当前值 修饰器 计算公式 属性依赖
// 属性计算要快 每帧都在跑

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YamaEngine.Gameplay
{
    #region 属性基础类型

    /// <summary>
    /// 属性类型 分类用
    /// </summary>
    public enum AttributeCategory : byte
    {
        // 基础属性 生命 法力 等
        Vital = 0,
        // 战斗属性 攻击 防御 等
        Combat = 1,
        // 移动属性 速度 跳跃 等
        Movement = 2,
        // 防御属性 抗性 减伤 等
        Defense = 3,
        // 资源属性 怒气 能量 等
        Resource = 4,
        // 派生属性 暴击率 命中率 等
        Derived = 5,
        // 自定义
        Custom = 255,
    }

    /// <summary>
    /// 属性值类型 决定怎么显示和处理
    /// </summary>
    public enum AttributeValueType : byte
    {
        // 整数 显示时不带小数
        Integer = 0,
        // 浮点数
        Float = 1,
        // 百分比 显示为%
        Percentage = 2,
        // 范围值 有最小最大
        Ranged = 3,
    }

    #endregion

    #region 属性修饰器

    /// <summary>
    /// 属性修饰器操作符
    /// </summary>
    public enum AttributeModifierOp : byte
    {
        // 加法
        Add = 0,
        // 减法
        Subtract = 1,
        // 乘法
        Multiply = 2,
        // 除法
        Divide = 3,
        // 覆盖
        Override = 4,
        // 百分比加成 基于基础值
        PercentAdd = 5,
        // 百分比乘法 基于当前值
        PercentMultiply = 6,
        // 设置最小值
        SetMin = 7,
        // 设置最大值
        SetMax = 8,
    }

    /// <summary>
    /// 属性修饰器 临时修改属性值
    /// 比如装备加成 buff加成 技能加成
    /// </summary>
    public class AttributeModifier
    {
        // 修饰器唯一ID
        public long ModifierId { get; internal set; }

        // 操作符
        public AttributeModifierOp Op { get; set; }

        // 修改量
        public float Magnitude { get; set; }

        // 来源标签 标识这个修饰器从哪来
        public string SourceTag { get; set; } = string.Empty;

        // 优先级 数值大的先应用
        public int Priority { get; set; }

        // 是否是临时修饰器 临时修饰器会过期
        public bool IsTemporary { get; set; }

        // 剩余时间 临时修饰器用
        public float RemainingDuration { get; set; }

        // 已经过去的时间
        public float ElapsedDuration { get; set; }

        // 修饰器标签 用于过滤
        public List<string> Tags { get; set; } = new();

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        // 是否已经过期
        public bool IsExpired
        {
            get
            {
                if (!IsTemporary) return false;
                return RemainingDuration <= 0f;
            }
        }

        // 来源效果句柄 标识这个修改器由哪个效果实例施加
        public int SourceEffectHandle { get; set; }

        public AttributeModifier()
        {
            ModifierId = System.Threading.Interlocked.Increment(ref _nextModifierId);
        }

        /// <summary>
        /// 创建带来源效果句柄的修改器
        /// </summary>
        public AttributeModifier(AttributeModifierOp op, float magnitude, int sourceHandle = 0)
            : this()
        {
            Op = op;
            Magnitude = magnitude;
            SourceEffectHandle = sourceHandle;
        }

        private static long _nextModifierId = 1;

        /// <summary>
        /// 创建加法修饰器
        /// </summary>
        public static AttributeModifier CreateAdd(float magnitude, string sourceTag = "")
        {
            return new AttributeModifier
            {
                Op = AttributeModifierOp.Add,
                Magnitude = magnitude,
                SourceTag = sourceTag,
            };
        }

        /// <summary>
        /// 创建百分比加成修饰器
        /// </summary>
        public static AttributeModifier CreatePercentAdd(float percent, string sourceTag = "")
        {
            return new AttributeModifier
            {
                Op = AttributeModifierOp.PercentAdd,
                Magnitude = percent,
                SourceTag = sourceTag,
            };
        }

        /// <summary>
        /// 创建临时修饰器
        /// </summary>
        public static AttributeModifier CreateTemporary(AttributeModifierOp op, float magnitude, float duration, string sourceTag = "")
        {
            return new AttributeModifier
            {
                Op = op,
                Magnitude = magnitude,
                SourceTag = sourceTag,
                IsTemporary = true,
                RemainingDuration = duration,
            };
        }

        public override string ToString()
        {
            string opStr = Op switch
            {
                AttributeModifierOp.Add => $"+{Magnitude}",
                AttributeModifierOp.Subtract => $"-{Magnitude}",
                AttributeModifierOp.Multiply => $"*{Magnitude}",
                AttributeModifierOp.Divide => $"/{Magnitude}",
                AttributeModifierOp.Override => $"={Magnitude}",
                AttributeModifierOp.PercentAdd => $"+{Magnitude*100}%",
                AttributeModifierOp.PercentMultiply => $"*{Magnitude*100}%",
                AttributeModifierOp.SetMin => $"min={Magnitude}",
                AttributeModifierOp.SetMax => $"max={Magnitude}",
                _ => $"?{Magnitude}",
            };
            return $"[Mod {opStr} '{SourceTag}']";
        }
    }

    #endregion

    #region 属性计算公式

    /// <summary>
    /// 属性计算公式接口 自定义计算逻辑
    /// </summary>
    public interface IAttributeFormula
    {
        float Calculate(AttributeCalculationContext context);
    }

    /// <summary>
    /// 属性计算上下文
    /// </summary>
    public class AttributeCalculationContext
    {
        // 属性集
        public GameplayAttributeSetFull? AttributeSet { get; set; }

        // 属性名字
        public string AttributeName { get; set; } = string.Empty;

        // 基础值
        public float BaseValue { get; set; }

        // 当前所有修饰器
        public List<AttributeModifier> Modifiers { get; set; } = new();

        // 自定义数据
        public Dictionary<string, object?> CustomData { get; } = new();

        /// <summary>
        /// 获取另一个属性的当前值
        /// </summary>
        public float GetAttributeValue(string name)
        {
            return AttributeSet?.GetAttribute(name)?.CurrentValue ?? 0f;
        }

        /// <summary>
        /// 获取另一个属性的基础值
        /// </summary>
        public float GetAttributeBaseValue(string name)
        {
            return AttributeSet?.GetAttribute(name)?.BaseValue ?? 0f;
        }
    }

    /// <summary>
    /// 简单公式 基础值+所有加法修饰器
    /// </summary>
    public class SimpleFormula : IAttributeFormula
    {
        public float Calculate(AttributeCalculationContext context)
        {
            float result = context.BaseValue;

            // 按优先级排序
            var sorted = context.Modifiers.OrderBy(m => m.Priority).ToList();

            // 先应用加法
            float addSum = 0;
            foreach (var mod in sorted)
            {
                if (mod.Op == AttributeModifierOp.Add)
                    addSum += mod.Magnitude;
                else if (mod.Op == AttributeModifierOp.Subtract)
                    addSum -= mod.Magnitude;
            }
            result += addSum;

            // 再应用百分比加成
            float percentSum = 0;
            foreach (var mod in sorted)
            {
                if (mod.Op == AttributeModifierOp.PercentAdd)
                    percentSum += mod.Magnitude;
            }
            result += context.BaseValue * percentSum;

            // 再应用乘法
            foreach (var mod in sorted)
            {
                if (mod.Op == AttributeModifierOp.Multiply)
                    result *= mod.Magnitude;
                else if (mod.Op == AttributeModifierOp.PercentMultiply)
                    result *= (1f + mod.Magnitude);
                else if (mod.Op == AttributeModifierOp.Divide && mod.Magnitude != 0f)
                    result /= mod.Magnitude;
            }

            // 最后应用覆盖
            foreach (var mod in sorted)
            {
                if (mod.Op == AttributeModifierOp.Override)
                    result = mod.Magnitude;
            }

            // 应用最小最大值
            float? minVal = null;
            float? maxVal = null;
            foreach (var mod in sorted)
            {
                if (mod.Op == AttributeModifierOp.SetMin)
                    minVal = minVal.HasValue ? Math.Max(minVal.Value, mod.Magnitude) : mod.Magnitude;
                if (mod.Op == AttributeModifierOp.SetMax)
                    maxVal = maxVal.HasValue ? Math.Min(maxVal.Value, mod.Magnitude) : mod.Magnitude;
            }

            if (minVal.HasValue && result < minVal.Value)
                result = minVal.Value;
            if (maxVal.HasValue && result > maxVal.Value)
                result = maxVal.Value;

            return result;
        }
    }

    /// <summary>
    /// 派生属性公式 根据其他属性计算
    /// </summary>
    public class DerivedAttributeFormula : IAttributeFormula
    {
        private readonly string _sourceAttributeName;
        private readonly float _coefficient;
        private readonly float _offset;

        public DerivedAttributeFormula(string sourceAttributeName, float coefficient, float offset = 0f)
        {
            _sourceAttributeName = sourceAttributeName;
            _coefficient = coefficient;
            _offset = offset;
        }

        public float Calculate(AttributeCalculationContext context)
        {
            float sourceValue = context.GetAttributeValue(_sourceAttributeName);
            return sourceValue * _coefficient + _offset + context.BaseValue;
        }
    }

    /// <summary>
    /// 线性公式 result = a*level + b*attr + c
    /// </summary>
    public class LinearAttributeFormula : IAttributeFormula
    {
        private readonly string _sourceAttr;
        private readonly float _a;
        private readonly float _b;
        private readonly float _c;

        public LinearAttributeFormula(string sourceAttr, float a, float b, float c)
        {
            _sourceAttr = sourceAttr;
            _a = a;
            _b = b;
            _c = c;
        }

        public float Calculate(AttributeCalculationContext context)
        {
            float level = context.GetAttributeValue(_sourceAttr);
            return _a * level + _b * context.BaseValue + _c;
        }
    }

    #endregion

    #region 属性定义

    /// <summary>
    /// 属性定义 静态描述一个属性
    /// </summary>
    public class AttributeDefinition
    {
        // 属性名字 唯一标识
        public string Name { get; set; } = string.Empty;

        // 显示名字 UI用
        public string DisplayName { get; set; } = string.Empty;

        // 描述
        public string Description { get; set; } = string.Empty;

        // 分类
        public AttributeCategory Category { get; set; } = AttributeCategory.Vital;

        // 值类型
        public AttributeValueType ValueType { get; set; } = AttributeValueType.Float;

        // 默认基础值
        public float DefaultValue { get; set; } = 0f;

        // 最小值
        public float MinValue { get; set; } = float.MinValue;

        // 最大值
        public float MaxValue { get; set; } = float.MaxValue;

        // 是否有最大值上限 比如生命值有上限
        public bool HasMaxCap { get; set; } = false;

        // 最大值属性名 比如Health的max是MaxHealth
        public string? MaxAttribute { get; set; }

        // 是否允许负数
        public bool AllowNegative { get; set; } = false;

        // 是否每帧重新计算
        public bool RecalculatePerFrame { get; set; } = false;

        // 计算公式
        public IAttributeFormula? Formula { get; set; }

        // 依赖的属性 当这些属性变化时 本属性也要重新计算
        public List<string> Dependencies { get; set; } = new();

        // UI图标
        public string IconPath { get; set; } = string.Empty;

        // 单位 显示用 比如 HP MP
        public string Unit { get; set; } = string.Empty;

        // 是否在UI上显示
        public bool IsVisible { get; set; } = true;

        // 元数据
        public Dictionary<string, string> Metadata { get; set; } = new();

        public override string ToString()
        {
            return $"[AttrDef {Name}]";
        }
    }

    /// <summary>
    /// 属性定义仓库
    /// </summary>
    public class AttributeDefinitionRepository
    {
        private readonly Dictionary<string, AttributeDefinition> _defs = new();
        private readonly object _lock = new();

        public bool Register(AttributeDefinition def)
        {
            if (def == null || string.IsNullOrEmpty(def.Name)) return false;

            lock (_lock)
            {
                if (_defs.ContainsKey(def.Name)) return false;
                _defs[def.Name] = def;
                return true;
            }
        }

        public AttributeDefinition? Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _defs.TryGetValue(name, out var def);
                return def;
            }
        }

        public List<AttributeDefinition> GetAll()
        {
            lock (_lock)
            {
                return _defs.Values.ToList();
            }
        }

        public List<AttributeDefinition> GetByCategory(AttributeCategory category)
        {
            lock (_lock)
            {
                return _defs.Values.Where(d => d.Category == category).ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _defs.Clear();
            }
        }

        public int Count
        {
            get { lock (_lock) { return _defs.Count; } }
        }
    }

    #endregion

    #region 属性实例 运行时

    /// <summary>
    /// 属性实例 完整实现 替代GAS V2里的占位
    /// 一个属性包含基础值 当前值 修饰器列表
    /// </summary>
    public class GameplayAttributeFull
    {
        // 属性定义
        public AttributeDefinition Definition { get; }

        // 基础值 不受修饰器影响
        public float BaseValue { get; set; }

        // 当前值 计算后的值
        public float CurrentValue { get; set; }

        // 修饰器列表
        private readonly List<AttributeModifier> _modifiers = new();
        private readonly object _modifiersLock = new();

        // 是否需要重新计算
        private bool _dirty = true;

        // 所属的属性集
        public GameplayAttributeSetFull? OwnerSet { get; internal set; }

        // 属性变化事件
        public event Action<GameplayAttributeFull, float, float>? ValueChanged;

        public GameplayAttributeFull(AttributeDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            BaseValue = definition.DefaultValue;
            CurrentValue = definition.DefaultValue;
        }

        /// <summary>
        /// 修饰器数量
        /// </summary>
        public int ModifierCount
        {
            get
            {
                lock (_modifiersLock)
                {
                    return _modifiers.Count;
                }
            }
        }

        /// <summary>
        /// 添加修饰器
        /// </summary>
        public void AddModifier(AttributeModifier modifier)
        {
            if (modifier == null) return;

            lock (_modifiersLock)
            {
                _modifiers.Add(modifier);
            }

            _dirty = true;
            RecalculateIfNeeded();
        }

        /// <summary>
        /// 移除修饰器
        /// </summary>
        public bool RemoveModifier(long modifierId)
        {
            lock (_modifiersLock)
            {
                int removed = _modifiers.RemoveAll(m => m.ModifierId == modifierId);
                if (removed > 0)
                {
                    _dirty = true;
                    RecalculateIfNeeded();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 按来源标签移除修饰器
        /// </summary>
        public int RemoveModifiersBySource(string sourceTag)
        {
            lock (_modifiersLock)
            {
                int removed = _modifiers.RemoveAll(m => m.SourceTag == sourceTag);
                if (removed > 0)
                {
                    _dirty = true;
                    RecalculateIfNeeded();
                }
                return removed;
            }
        }

        /// <summary>
        /// 获取所有修饰器
        /// </summary>
        public List<AttributeModifier> GetAllModifiers()
        {
            lock (_modifiersLock)
            {
                return _modifiers.ToList();
            }
        }

        /// <summary>
        /// 设置基础值
        /// </summary>
        public void SetBaseValue(float value)
        {
            BaseValue = value;
            _dirty = true;
            RecalculateIfNeeded();
        }

        /// <summary>
        /// 修改基础值
        /// </summary>
        public void ModifyBaseValue(float delta)
        {
            BaseValue += delta;
            _dirty = true;
            RecalculateIfNeeded();
        }

        /// <summary>
        /// 直接修改当前值 不走修饰器
        /// 比如伤害扣除
        /// </summary>
        public void ModifyCurrentValue(float delta)
        {
            float oldValue = CurrentValue;
            CurrentValue += delta;
            ApplyClamp();

            if (Math.Abs(oldValue - CurrentValue) > 0.0001f)
            {
                ValueChanged?.Invoke(this, oldValue, CurrentValue);
            }
        }

        /// <summary>
        /// 直接设置当前值
        /// </summary>
        public void SetCurrentValue(float value)
        {
            float oldValue = CurrentValue;
            CurrentValue = value;
            ApplyClamp();

            if (Math.Abs(oldValue - CurrentValue) > 0.0001f)
            {
                ValueChanged?.Invoke(this, oldValue, CurrentValue);
            }
        }

        /// <summary>
        /// 重新计算
        /// </summary>
        public void Recalculate()
        {
            float oldValue = CurrentValue;

            List<AttributeModifier> snapshot;
            lock (_modifiersLock)
            {
                snapshot = _modifiers.ToList();
            }

            if (Definition.Formula != null)
            {
                var context = new AttributeCalculationContext
                {
                    AttributeSet = OwnerSet,
                    AttributeName = Definition.Name,
                    BaseValue = BaseValue,
                    Modifiers = snapshot,
                };

                CurrentValue = Definition.Formula.Calculate(context);
            }
            else
            {
                // 默认计算 简单公式
                var context = new AttributeCalculationContext
                {
                    AttributeSet = OwnerSet,
                    AttributeName = Definition.Name,
                    BaseValue = BaseValue,
                    Modifiers = snapshot,
                };

                CurrentValue = new SimpleFormula().Calculate(context);
            }

            ApplyClamp();
            _dirty = false;

            if (Math.Abs(oldValue - CurrentValue) > 0.0001f)
            {
                ValueChanged?.Invoke(this, oldValue, CurrentValue);
            }
        }

        /// <summary>
        /// 如果脏了就重新计算
        /// </summary>
        public void RecalculateIfNeeded()
        {
            if (_dirty)
            {
                Recalculate();
            }
        }

        /// <summary>
        /// 标记为脏 下次访问时重新计算
        /// </summary>
        public void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>
        /// 应用上下限
        /// </summary>
        private void ApplyClamp()
        {
            // 检查最大值属性
            if (Definition.HasMaxCap && !string.IsNullOrEmpty(Definition.MaxAttribute) && OwnerSet != null)
            {
                var maxAttr = OwnerSet.GetAttribute(Definition.MaxAttribute);
                if (maxAttr != null)
                {
                    CurrentValue = Math.Min(CurrentValue, maxAttr.CurrentValue);
                }
            }

            // 定义的最小最大值
            CurrentValue = Math.Max(CurrentValue, Definition.MinValue);
            CurrentValue = Math.Min(CurrentValue, Definition.MaxValue);

            // 不允许负数
            if (!Definition.AllowNegative && CurrentValue < 0f)
            {
                CurrentValue = 0f;
            }
        }

        /// <summary>
        /// Tick更新 处理临时修饰器
        /// </summary>
        public void Tick(float deltaTime)
        {
            bool anyExpired = false;

            lock (_modifiersLock)
            {
                foreach (var mod in _modifiers)
                {
                    if (mod.IsTemporary)
                    {
                        mod.RemainingDuration -= deltaTime;
                        mod.ElapsedDuration += deltaTime;

                        if (mod.IsExpired)
                        {
                            anyExpired = true;
                        }
                    }
                }

                if (anyExpired)
                {
                    _modifiers.RemoveAll(m => m.IsExpired);
                    _dirty = true;
                }
            }

            if (Definition.RecalculatePerFrame)
            {
                _dirty = true;
            }

            if (_dirty)
            {
                Recalculate();
            }
        }

        /// <summary>
        /// 清空所有修饰器
        /// </summary>
        public void ClearAllModifiers()
        {
            lock (_modifiersLock)
            {
                _modifiers.Clear();
            }
            _dirty = true;
            RecalculateIfNeeded();
        }

        /// <summary>
        /// 重置到基础值
        /// </summary>
        public void ResetToBase()
        {
            ClearAllModifiers();
            SetCurrentValue(BaseValue);
        }

        /// <summary>
        /// 获取当前值百分比 相对于最大值
        /// </summary>
        public float GetPercentage()
        {
            float max = Definition.MaxValue;
            if (Definition.HasMaxCap && !string.IsNullOrEmpty(Definition.MaxAttribute) && OwnerSet != null)
            {
                var maxAttr = OwnerSet.GetAttribute(Definition.MaxAttribute);
                if (maxAttr != null)
                    max = maxAttr.CurrentValue;
            }

            if (max <= 0f) return 0f;
            return CurrentValue / max;
        }

        public override string ToString()
        {
            return $"{Definition.Name}: {CurrentValue:F1} (base {BaseValue:F1}, mods {ModifierCount})";
        }
    }

    #endregion

    #region 属性集 完整实现

    /// <summary>
    /// 属性集 完整实现 替代GAS V2里的占位
    /// 管理一组属性 提供统一访问
    /// </summary>
    public class GameplayAttributeSetFull
    {
        // 所有属性 按名字索引
        private readonly Dictionary<string, GameplayAttributeFull> _attributes = new();

        // 属性定义仓库
        private readonly AttributeDefinitionRepository _defs;

        // 锁
        private readonly object _lock = new();

        // 所属的能力组件
        public GameplayAbilityComponent? OwnerComponent { get; set; }

        // 属性变化事件
        public event Action<GameplayAttributeSetFull, GameplayAttributeFull, float, float>? AttributeChanged;

        public GameplayAttributeSetFull(AttributeDefinitionRepository defs)
        {
            _defs = defs ?? throw new ArgumentNullException(nameof(defs));
        }

        /// <summary>
        /// 属性数量
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _attributes.Count;
                }
            }
        }

        /// <summary>
        /// 创建属性 根据定义
        /// </summary>
        public GameplayAttributeFull? CreateAttribute(string name)
        {
            var def = _defs.Get(name);
            if (def == null) return null;

            lock (_lock)
            {
                if (_attributes.ContainsKey(name))
                    return _attributes[name];

                var attr = new GameplayAttributeFull(def)
                {
                    OwnerSet = this,
                };

                attr.ValueChanged += (a, oldV, newV) =>
                {
                    AttributeChanged?.Invoke(this, a, oldV, newV);

                    // 通知依赖这个属性的其他属性
                    NotifyDependents(name);
                };

                _attributes[name] = attr;
                return attr;
            }
        }

        /// <summary>
        /// 通知依赖属性重新计算
        /// </summary>
        private void NotifyDependents(string attributeName)
        {
            List<GameplayAttributeFull> dependents;

            lock (_lock)
            {
                dependents = _attributes.Values
                    .Where(a => a.Definition.Dependencies.Contains(attributeName))
                    .ToList();
            }

            foreach (var dep in dependents)
            {
                dep.MarkDirty();
                dep.RecalculateIfNeeded();
            }
        }

        /// <summary>
        /// 获取属性
        /// </summary>
        public GameplayAttributeFull? GetAttribute(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (_lock)
            {
                _attributes.TryGetValue(name, out var attr);
                return attr;
            }
        }

        /// <summary>
        /// 设置基础值
        /// </summary>
        public void SetAttribute(string name, float baseValue)
        {
            var attr = GetAttribute(name);
            if (attr == null)
            {
                attr = CreateAttribute(name);
                if (attr == null)
                {
                    // 定义不存在 临时创建一个简单定义
                    var tempDef = new AttributeDefinition { Name = name, DefaultValue = baseValue };
                    lock (_lock)
                    {
                        attr = new GameplayAttributeFull(tempDef) { OwnerSet = this };
                        _attributes[name] = attr;
                    }
                    attr.SetBaseValue(baseValue);
                    return;
                }
            }
            attr.SetBaseValue(baseValue);
        }

        /// <summary>
        /// 修改当前值
        /// </summary>
        public bool ModifyAttribute(string name, float delta)
        {
            var attr = GetAttribute(name);
            if (attr == null) return false;
            attr.ModifyCurrentValue(delta);
            return true;
        }

        /// <summary>
        /// 设置当前值
        /// </summary>
        public bool SetAttributeValue(string name, float value)
        {
            var attr = GetAttribute(name);
            if (attr == null) return false;
            attr.SetCurrentValue(value);
            return true;
        }

        /// <summary>
        /// 获取属性当前值
        /// </summary>
        public float GetAttributeValue(string name)
        {
            var attr = GetAttribute(name);
            return attr?.CurrentValue ?? 0f;
        }

        /// <summary>
        /// 获取属性基础值
        /// </summary>
        public float GetAttributeBaseValue(string name)
        {
            var attr = GetAttribute(name);
            return attr?.BaseValue ?? 0f;
        }

        /// <summary>
        /// 添加修饰器到属性
        /// </summary>
        public bool AddModifier(string name, AttributeModifier modifier)
        {
            var attr = GetAttribute(name);
            if (attr == null) return false;
            attr.AddModifier(modifier);
            return true;
        }

        /// <summary>
        /// 按来源移除修饰器
        /// </summary>
        public int RemoveModifiersBySource(string name, string sourceTag)
        {
            var attr = GetAttribute(name);
            if (attr == null) return 0;
            return attr.RemoveModifiersBySource(sourceTag);
        }

        /// <summary>
        /// 获取所有属性
        /// </summary>
        public List<GameplayAttributeFull> GetAllAttributes()
        {
            lock (_lock)
            {
                return _attributes.Values.ToList();
            }
        }

        /// <summary>
        /// 按分类获取属性
        /// </summary>
        public List<GameplayAttributeFull> GetAttributesByCategory(AttributeCategory category)
        {
            lock (_lock)
            {
                return _attributes.Values
                    .Where(a => a.Definition.Category == category)
                    .ToList();
            }
        }

        /// <summary>
        /// 批量初始化属性 根据定义仓库
        /// </summary>
        public void InitializeFromDefinitions()
        {
            foreach (var def in _defs.GetAll())
            {
                CreateAttribute(def.Name);
            }
        }

        /// <summary>
        /// Tick更新所有属性
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<GameplayAttributeFull> snapshot;
            lock (_lock)
            {
                snapshot = _attributes.Values.ToList();
            }

            foreach (var attr in snapshot)
            {
                attr.Tick(deltaTime);
            }
        }

        /// <summary>
        /// 重新计算所有属性
        /// </summary>
        public void RecalculateAll()
        {
            List<GameplayAttributeFull> snapshot;
            lock (_lock)
            {
                snapshot = _attributes.Values.ToList();
            }

            foreach (var attr in snapshot)
            {
                attr.Recalculate();
            }
        }

        /// <summary>
        /// 清空所有属性
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _attributes.Clear();
            }
        }

        /// <summary>
        /// 转为字符串
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"AttributeSet ({Count} attributes):");
            lock (_lock)
            {
                foreach (var attr in _attributes.Values)
                {
                    sb.AppendLine($"  {attr}");
                }
            }
            return sb.ToString();
        }
    }

    #endregion

    #region 属性系统管理器

    /// <summary>
    /// 属性系统管理器 全局入口
    /// </summary>
    public class GameplayAttributeSystem
    {
        // 单例
        private static GameplayAttributeSystem? _instance;
        private static readonly object _instanceLock = new();

        public static GameplayAttributeSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new GameplayAttributeSystem();
                    }
                }
                return _instance;
            }
        }

        // 属性定义仓库
        public AttributeDefinitionRepository Definitions { get; } = new();

        // 所有属性集 跟踪
        private readonly List<GameplayAttributeSetFull> _sets = new();
        private readonly object _setsLock = new();

        // 全局事件
        public event Action<GameplayAttributeSetFull, GameplayAttributeFull, float, float>? GlobalAttributeChanged;

        private GameplayAttributeSystem() { }

        /// <summary>
        /// 创建属性集
        /// </summary>
        public GameplayAttributeSetFull CreateSet()
        {
            var set = new GameplayAttributeSetFull(Definitions);

            set.AttributeChanged += (s, attr, oldV, newV) =>
            {
                GlobalAttributeChanged?.Invoke(s, attr, oldV, newV);
            };

            lock (_setsLock)
            {
                _sets.Add(set);
            }

            return set;
        }

        /// <summary>
        /// 销毁属性集
        /// </summary>
        public void DestroySet(GameplayAttributeSetFull set)
        {
            if (set == null) return;
            lock (_setsLock)
            {
                _sets.Remove(set);
            }
            set.Clear();
        }

        /// <summary>
        /// 获取所有属性集
        /// </summary>
        public List<GameplayAttributeSetFull> GetAllSets()
        {
            lock (_setsLock)
            {
                return _sets.ToList();
            }
        }

        /// <summary>
        /// 全局Tick
        /// </summary>
        public void Tick(float deltaTime)
        {
            List<GameplayAttributeSetFull> snapshot;
            lock (_setsLock)
            {
                snapshot = _sets.ToList();
            }

            foreach (var set in snapshot)
            {
                set.Tick(deltaTime);
            }
        }

        /// <summary>
        /// 注册属性定义
        /// </summary>
        public bool RegisterDefinition(AttributeDefinition def)
        {
            return Definitions.Register(def);
        }

        /// <summary>
        /// 关闭系统
        /// </summary>
        public void Shutdown()
        {
            lock (_setsLock)
            {
                foreach (var s in _sets)
                {
                    s.Clear();
                }
                _sets.Clear();
            }
            Definitions.Clear();
        }
    }

    #endregion

    #region 属性预设 工厂方法

    /// <summary>
    /// 属性预设工厂 创建常用属性定义
    /// </summary>
    public static class AttributePresetFactory
    {
        /// <summary>
        /// 创建生命值属性
        /// </summary>
        public static AttributeDefinition CreateHealth()
        {
            return new AttributeDefinition
            {
                Name = "Health",
                DisplayName = "生命值",
                Description = "角色的生命值 归零就死了",
                Category = AttributeCategory.Vital,
                ValueType = AttributeValueType.Float,
                DefaultValue = 100f,
                MinValue = 0f,
                HasMaxCap = true,
                MaxAttribute = "MaxHealth",
                AllowNegative = false,
                Unit = "HP",
                IconPath = "UI/Icons/Health",
            };
        }

        /// <summary>
        /// 创建最大生命值
        /// </summary>
        public static AttributeDefinition CreateMaxHealth()
        {
            return new AttributeDefinition
            {
                Name = "MaxHealth",
                DisplayName = "最大生命值",
                Description = "生命值上限",
                Category = AttributeCategory.Vital,
                ValueType = AttributeValueType.Float,
                DefaultValue = 100f,
                MinValue = 1f,
                AllowNegative = false,
                Unit = "HP",
            };
        }

        /// <summary>
        /// 创建法力值
        /// </summary>
        public static AttributeDefinition CreateMana()
        {
            return new AttributeDefinition
            {
                Name = "Mana",
                DisplayName = "法力值",
                Description = "施法消耗的资源",
                Category = AttributeCategory.Resource,
                ValueType = AttributeValueType.Float,
                DefaultValue = 50f,
                MinValue = 0f,
                HasMaxCap = true,
                MaxAttribute = "MaxMana",
                AllowNegative = false,
                Unit = "MP",
            };
        }

        /// <summary>
        /// 创建最大法力值
        /// </summary>
        public static AttributeDefinition CreateMaxMana()
        {
            return new AttributeDefinition
            {
                Name = "MaxMana",
                DisplayName = "最大法力值",
                Category = AttributeCategory.Resource,
                DefaultValue = 50f,
                MinValue = 1f,
                Unit = "MP",
            };
        }

        /// <summary>
        /// 创建体力值
        /// </summary>
        public static AttributeDefinition CreateStamina()
        {
            return new AttributeDefinition
            {
                Name = "Stamina",
                DisplayName = "体力",
                Description = "冲刺翻滚消耗",
                Category = AttributeCategory.Resource,
                DefaultValue = 100f,
                MinValue = 0f,
                HasMaxCap = true,
                MaxAttribute = "MaxStamina",
                AllowNegative = false,
                Unit = "SP",
            };
        }

        /// <summary>
        /// 创建最大体力
        /// </summary>
        public static AttributeDefinition CreateMaxStamina()
        {
            return new AttributeDefinition
            {
                Name = "MaxStamina",
                DisplayName = "最大体力",
                Category = AttributeCategory.Resource,
                DefaultValue = 100f,
                MinValue = 1f,
                Unit = "SP",
            };
        }

        /// <summary>
        /// 创建攻击力
        /// </summary>
        public static AttributeDefinition CreateAttack()
        {
            return new AttributeDefinition
            {
                Name = "Attack",
                DisplayName = "攻击力",
                Description = "物理伤害基础值",
                Category = AttributeCategory.Combat,
                DefaultValue = 10f,
                MinValue = 0f,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建法术强度
        /// </summary>
        public static AttributeDefinition CreateSpellPower()
        {
            return new AttributeDefinition
            {
                Name = "SpellPower",
                DisplayName = "法术强度",
                Description = "魔法伤害加成",
                Category = AttributeCategory.Combat,
                DefaultValue = 10f,
                MinValue = 0f,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建防御力
        /// </summary>
        public static AttributeDefinition CreateDefense()
        {
            return new AttributeDefinition
            {
                Name = "Defense",
                DisplayName = "防御力",
                Description = "减少受到的物理伤害",
                Category = AttributeCategory.Defense,
                DefaultValue = 5f,
                MinValue = 0f,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建魔法抗性
        /// </summary>
        public static AttributeDefinition CreateMagicResist()
        {
            return new AttributeDefinition
            {
                Name = "MagicResist",
                DisplayName = "魔法抗性",
                Description = "减少受到的魔法伤害",
                Category = AttributeCategory.Defense,
                DefaultValue = 5f,
                MinValue = 0f,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建移动速度
        /// </summary>
        public static AttributeDefinition CreateMoveSpeed()
        {
            return new AttributeDefinition
            {
                Name = "MoveSpeed",
                DisplayName = "移动速度",
                Category = AttributeCategory.Movement,
                DefaultValue = 5f,
                MinValue = 0.1f,
                MaxValue = 100f,
                AllowNegative = false,
                RecalculatePerFrame = false,
            };
        }

        /// <summary>
        /// 创建攻击速度
        /// </summary>
        public static AttributeDefinition CreateAttackSpeed()
        {
            return new AttributeDefinition
            {
                Name = "AttackSpeed",
                DisplayName = "攻击速度",
                Category = AttributeCategory.Combat,
                DefaultValue = 1f,
                MinValue = 0.1f,
                MaxValue = 10f,
                ValueType = AttributeValueType.Percentage,
            };
        }

        /// <summary>
        /// 创建暴击率
        /// </summary>
        public static AttributeDefinition CreateCritRate()
        {
            return new AttributeDefinition
            {
                Name = "CritRate",
                DisplayName = "暴击率",
                Description = "攻击暴击的概率",
                Category = AttributeCategory.Derived,
                ValueType = AttributeValueType.Percentage,
                DefaultValue = 0.05f,
                MinValue = 0f,
                MaxValue = 1f,
            };
        }

        /// <summary>
        /// 创建暴击伤害
        /// </summary>
        public static AttributeDefinition CreateCritDamage()
        {
            return new AttributeDefinition
            {
                Name = "CritDamage",
                DisplayName = "暴击伤害",
                Description = "暴击时的伤害倍率",
                Category = AttributeCategory.Derived,
                ValueType = AttributeValueType.Percentage,
                DefaultValue = 1.5f,
                MinValue = 1f,
                MaxValue = 10f,
            };
        }

        /// <summary>
        /// 创建护盾值
        /// </summary>
        public static AttributeDefinition CreateShield()
        {
            return new AttributeDefinition
            {
                Name = "Shield",
                DisplayName = "护盾",
                Description = "吸收伤害的护盾值",
                Category = AttributeCategory.Defense,
                DefaultValue = 0f,
                MinValue = 0f,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建等级
        /// </summary>
        public static AttributeDefinition CreateLevel()
        {
            return new AttributeDefinition
            {
                Name = "Level",
                DisplayName = "等级",
                Category = AttributeCategory.Vital,
                ValueType = AttributeValueType.Integer,
                DefaultValue = 1,
                MinValue = 1,
                MaxValue = 100,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建经验值
        /// </summary>
        public static AttributeDefinition CreateExperience()
        {
            return new AttributeDefinition
            {
                Name = "Experience",
                DisplayName = "经验值",
                Category = AttributeCategory.Vital,
                ValueType = AttributeValueType.Integer,
                DefaultValue = 0,
                MinValue = 0,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 创建金币
        /// </summary>
        public static AttributeDefinition CreateGold()
        {
            return new AttributeDefinition
            {
                Name = "Gold",
                DisplayName = "金币",
                Category = AttributeCategory.Vital,
                ValueType = AttributeValueType.Integer,
                DefaultValue = 0,
                MinValue = 0,
                AllowNegative = false,
            };
        }

        /// <summary>
        /// 获取所有预设属性定义
        /// </summary>
        public static List<AttributeDefinition> GetAllPresets()
        {
            return new List<AttributeDefinition>
            {
                CreateHealth(),
                CreateMaxHealth(),
                CreateMana(),
                CreateMaxMana(),
                CreateStamina(),
                CreateMaxStamina(),
                CreateAttack(),
                CreateSpellPower(),
                CreateDefense(),
                CreateMagicResist(),
                CreateMoveSpeed(),
                CreateAttackSpeed(),
                CreateCritRate(),
                CreateCritDamage(),
                CreateShield(),
                CreateLevel(),
                CreateExperience(),
                CreateGold(),
            };
        }

        /// <summary>
        /// 注册所有预设到系统
        /// </summary>
        public static void RegisterAll(GameplayAttributeSystem system)
        {
            foreach (var def in GetAllPresets())
            {
                system.RegisterDefinition(def);
            }
        }
    }

    #endregion

    #region 属性UI数据

    /// <summary>
    /// 属性UI数据 提供给前端显示
    /// </summary>
    public class AttributeUIData
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public float BaseValue { get; set; }
        public float CurrentValue { get; set; }
        public float Percentage { get; set; }
        public AttributeValueType ValueType { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public AttributeCategory Category { get; set; }
        public bool IsVisible { get; set; } = true;
        public List<string> ModifierSources { get; set; } = new();
    }

    /// <summary>
    /// 属性UI数据收集器
    /// </summary>
    public static class AttributeUIDataCollector
    {
        /// <summary>
        /// 收集属性集的UI数据
        /// </summary>
        public static List<AttributeUIData> Collect(GameplayAttributeSetFull set, bool onlyVisible = true)
        {
            var result = new List<AttributeUIData>();

            if (set == null) return result;

            foreach (var attr in set.GetAllAttributes())
            {
                if (onlyVisible && !attr.Definition.IsVisible)
                    continue;

                var uiData = new AttributeUIData
                {
                    Name = attr.Definition.Name,
                    DisplayName = attr.Definition.DisplayName,
                    Description = attr.Definition.Description,
                    BaseValue = attr.BaseValue,
                    CurrentValue = attr.CurrentValue,
                    Percentage = attr.GetPercentage(),
                    ValueType = attr.Definition.ValueType,
                    Unit = attr.Definition.Unit,
                    IconPath = attr.Definition.IconPath,
                    Category = attr.Definition.Category,
                    IsVisible = attr.Definition.IsVisible,
                    ModifierSources = attr.GetAllModifiers().Select(m => m.SourceTag).Distinct().ToList(),
                };

                result.Add(uiData);
            }

            return result;
        }

        /// <summary>
        /// 按分类收集
        /// </summary>
        public static List<AttributeUIData> CollectByCategory(GameplayAttributeSetFull set, AttributeCategory category)
        {
            var result = new List<AttributeUIData>();

            if (set == null) return result;

            foreach (var attr in set.GetAttributesByCategory(category))
            {
                if (!attr.Definition.IsVisible)
                    continue;

                var uiData = new AttributeUIData
                {
                    Name = attr.Definition.Name,
                    DisplayName = attr.Definition.DisplayName,
                    Description = attr.Definition.Description,
                    BaseValue = attr.BaseValue,
                    CurrentValue = attr.CurrentValue,
                    Percentage = attr.GetPercentage(),
                    ValueType = attr.Definition.ValueType,
                    Unit = attr.Definition.Unit,
                    IconPath = attr.Definition.IconPath,
                    Category = attr.Definition.Category,
                };

                result.Add(uiData);
            }

            return result;
        }
    }

    #endregion

    #region 属性快照 存档用

    /// <summary>
    /// 属性快照 用于存档
    /// </summary>
    public class AttributeSetSnapshot
    {
        public Dictionary<string, float> BaseValues { get; set; } = new();
        public Dictionary<string, float> CurrentValues { get; set; } = new();
        public List<ModifierSnapshot> Modifiers { get; set; } = new();
    }

    /// <summary>
    /// 修饰器快照
    /// </summary>
    public class ModifierSnapshot
    {
        public string AttributeName { get; set; } = string.Empty;
        public AttributeModifierOp Op { get; set; }
        public float Magnitude { get; set; }
        public string SourceTag { get; set; } = string.Empty;
        public bool IsTemporary { get; set; }
        public float RemainingDuration { get; set; }
    }

    /// <summary>
    /// 属性快照工具
    /// </summary>
    public static class AttributeSetSnapshotTool
    {
        /// <summary>
        /// 创建快照
        /// </summary>
        public static AttributeSetSnapshot CreateSnapshot(GameplayAttributeSetFull set)
        {
            var snapshot = new AttributeSetSnapshot();

            if (set == null) return snapshot;

            foreach (var attr in set.GetAllAttributes())
            {
                snapshot.BaseValues[attr.Definition.Name] = attr.BaseValue;
                snapshot.CurrentValues[attr.Definition.Name] = attr.CurrentValue;

                foreach (var mod in attr.GetAllModifiers())
                {
                    snapshot.Modifiers.Add(new ModifierSnapshot
                    {
                        AttributeName = attr.Definition.Name,
                        Op = mod.Op,
                        Magnitude = mod.Magnitude,
                        SourceTag = mod.SourceTag,
                        IsTemporary = mod.IsTemporary,
                        RemainingDuration = mod.RemainingDuration,
                    });
                }
            }

            return snapshot;
        }

        /// <summary>
        /// 从快照恢复
        /// </summary>
        public static void RestoreFromSnapshot(GameplayAttributeSetFull set, AttributeSetSnapshot snapshot)
        {
            if (set == null || snapshot == null) return;

            // 恢复基础值
            foreach (var kv in snapshot.BaseValues)
            {
                set.SetAttribute(kv.Key, kv.Value);
            }

            // 恢复当前值
            foreach (var kv in snapshot.CurrentValues)
            {
                set.SetAttributeValue(kv.Key, kv.Value);
            }

            // 恢复修饰器
            foreach (var modSnap in snapshot.Modifiers)
            {
                var mod = new AttributeModifier
                {
                    Op = modSnap.Op,
                    Magnitude = modSnap.Magnitude,
                    SourceTag = modSnap.SourceTag,
                    IsTemporary = modSnap.IsTemporary,
                    RemainingDuration = modSnap.RemainingDuration,
                };

                set.AddModifier(modSnap.AttributeName, mod);
            }
        }
    }

    #endregion

    #region 属性系统初始化辅助

    /// <summary>
    /// 属性系统启动器
    /// </summary>
    public static class AttributeSystemBootstrap
    {
        public static GameplayAttributeSystem Initialize()
        {
            var system = GameplayAttributeSystem.Instance;
            AttributePresetFactory.RegisterAll(system);
            return system;
        }

        public static string RunSmokeTest()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Attribute System Smoke Test ===");

            var system = Initialize();
            sb.AppendLine($"System initialized with {system.Definitions.Count} definitions");

            // 创建属性集
            var set = system.CreateSet();
            set.InitializeFromDefinitions();

            // 设置基础值
            set.SetAttribute("MaxHealth", 200f);
            set.SetAttribute("Health", 200f);
            set.SetAttribute("MaxMana", 100f);
            set.SetAttribute("Mana", 100f);
            set.SetAttribute("Attack", 20f);
            set.SetAttribute("Defense", 10f);

            sb.AppendLine("Initial attributes:");
            sb.AppendLine(set.ToString());

            // 添加修饰器
            set.AddModifier("Attack", AttributeModifier.CreateAdd(15f, "Equipment"));
            set.AddModifier("Attack", AttributeModifier.CreatePercentAdd(0.2f, "Buff"));
            set.AddModifier("Defense", AttributeModifier.CreateAdd(5f, "Equipment"));

            sb.AppendLine("After modifiers:");
            sb.AppendLine(set.ToString());

            // 受到伤害
            set.ModifyAttribute("Health", -50f);
            sb.AppendLine($"After taking 50 damage: Health = {set.GetAttributeValue("Health"):F1}");

            // 添加临时修饰器
            set.AddModifier("Attack", AttributeModifier.CreateTemporary(
                AttributeModifierOp.PercentAdd, 0.5f, 5f, "TempBuff"));

            sb.AppendLine($"With temp buff: Attack = {set.GetAttributeValue("Attack"):F1}");

            // Tick几次
            for (int i = 0; i < 10; i++)
            {
                system.Tick(1f);
            }

            sb.AppendLine($"After 10s (temp buff expired): Attack = {set.GetAttributeValue("Attack"):F1}");

            // 创建快照
            var snapshot = AttributeSetSnapshotTool.CreateSnapshot(set);
            sb.AppendLine($"Snapshot created: {snapshot.BaseValues.Count} attrs, {snapshot.Modifiers.Count} mods");

            system.Shutdown();
            sb.AppendLine("Test complete");
            return sb.ToString();
        }
    }

    #endregion

} // namespace YamaEngine.Gameplay
