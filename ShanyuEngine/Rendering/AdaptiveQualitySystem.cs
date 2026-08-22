// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 自适应质量系统 (AdaptiveQualitySystem)
//
// 根据 GPU 帧时间动态调整渲染分辨率和质量设置，确保稳定帧率。
//
// 核心特性:
//   1. 指数移动平均 (EMA) 平滑 GPU 时间采样，避免抖动
//   2. 自动升降分辨率缩放 (Resolution Scale)
//   3. 5 个预设质量等级 (Low / Medium / High / Epic / Cinematic)
//   4. 级联降级策略: 分辨率 -> 阴影质量 -> SSAO -> 后处理 -> LOD 偏移
//   5. 可配置的目标帧率、升降速率、历史窗口长度
//   6. 输出回调: 当分辨率变化时通知渲染管线
//
// 对接系统: WorldRenderPipeline (分辨率缩放) / NeuralSuperResolution (上采样)
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;

namespace ShanyuEngine.Rendering
{
    // ──────────────────────────────────────────────────────────────
    // 质量等级枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 渲染质量等级。每个等级对应一组预配置的渲染参数。
    /// </summary>
    public enum QualityLevel
    {
        /// <summary>低质量 — 720p, 无阴影, 无 SSAO, 低 LOD。</summary>
        Low = 0,
        /// <summary>中质量 — 900p, 简单阴影, 低 SSAO, 中 LOD。</summary>
        Medium = 1,
        /// <summary>高质量 — 1080p, CSM 阴影, SSAO, TAA, 标准 LOD。</summary>
        High = 2,
        /// <summary>极致 — 1440p, 高质量阴影, 高 SSAO, 高 LOD, Bloom。</summary>
        Epic = 3,
        /// <summary>电影级 — 原生分辨率, 全特效, 最高 LOD, 全后处理。</summary>
        Cinematic = 4
    }

    // ──────────────────────────────────────────────────────────────
    // 降级策略枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 质量降级策略步骤。当 GPU 时间超标时，按此顺序逐步降级。
    /// </summary>
    public enum DegradationStep
    {
        None = 0,
        /// <summary>降低渲染分辨率缩放。</summary>
        ReduceResolution = 1,
        /// <summary>降低阴影贴图分辨率。</summary>
        ReduceShadowQuality = 2,
        /// <summary>禁用 SSAO。</summary>
        DisableSSAO = 3,
        /// <summary>简化后处理 (禁用 Bloom/MotionBlur 等)。</summary>
        ReducePostProcess = 4,
        /// <summary>增加 LOD 偏移 (使用更低细节的模型)。</summary>
        IncreaseLODBias = 5,
        /// <summary>降低纹理 Mip 偏移。</summary>
        ReduceTextureRes = 6,
        /// <summary>禁用 TAA (使用 FXAA 替代)。</summary>
        DisableTAA = 7
    }

    // ──────────────────────────────────────────────────────────────
    // 质量预设配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 质量预设: 定义每个质量等级对应的渲染参数。
    /// </summary>
    public sealed class QualityPreset
    {
        /// <summary>质量等级。</summary>
        public QualityLevel Level { get; set; }
        /// <summary>分辨率缩放因子。</summary>
        public float ResolutionScale { get; set; } = 1.0f;
        /// <summary>阴影贴图分辨率。</summary>
        public int ShadowMapSize { get; set; } = 2048;
        /// <summary>阴影级联数量。</summary>
        public int ShadowCascadeCount { get; set; } = 4;
        /// <summary>是否启用 SSAO。</summary>
        public bool EnableSSAO { get; set; } = true;
        /// <summary>SSAO 采样数。</summary>
        public int SSAOSampleCount { get; set; } = 16;
        /// <summary>是否启用 Bloom。</summary>
        public bool EnableBloom { get; set; } = true;
        /// <summary>是否启用运动模糊。</summary>
        public bool EnableMotionBlur { get; set; } = true;
        /// <summary>是否启用 TAA。</summary>
        public bool EnableTAA { get; set; } = true;
        /// <summary>LOD 偏移 (0=最高细节)。</summary>
        public int LODBias { get; set; } = 0;
        /// <summary>纹理 Mip 偏移。</summary>
        public int TextureMipBias { get; set; } = 0;
        /// <summary>最大光源数量。</summary>
        public int MaxLights { get; set; } = 256;
        /// <summary>抗锯齿方法 (0=FXAA, 1=TAA, 2=TSR)。</summary>
        public int AAMethod { get; set; } = 1;

        /// <summary>获取内置质量预设。</summary>
        public static QualityPreset GetPreset(QualityLevel level) => level switch
        {
            QualityLevel.Low => new QualityPreset
            {
                Level = QualityLevel.Low,
                ResolutionScale = 0.5f,
                ShadowMapSize = 512,
                ShadowCascadeCount = 2,
                EnableSSAO = false,
                SSAOSampleCount = 0,
                EnableBloom = false,
                EnableMotionBlur = false,
                EnableTAA = false,
                LODBias = 2,
                TextureMipBias = 1,
                MaxLights = 64,
                AAMethod = 0
            },
            QualityLevel.Medium => new QualityPreset
            {
                Level = QualityLevel.Medium,
                ResolutionScale = 0.67f,
                ShadowMapSize = 1024,
                ShadowCascadeCount = 3,
                EnableSSAO = true,
                SSAOSampleCount = 8,
                EnableBloom = true,
                EnableMotionBlur = false,
                EnableTAA = true,
                LODBias = 1,
                TextureMipBias = 0,
                MaxLights = 128,
                AAMethod = 1
            },
            QualityLevel.High => new QualityPreset
            {
                Level = QualityLevel.High,
                ResolutionScale = 0.85f,
                ShadowMapSize = 2048,
                ShadowCascadeCount = 4,
                EnableSSAO = true,
                SSAOSampleCount = 16,
                EnableBloom = true,
                EnableMotionBlur = true,
                EnableTAA = true,
                LODBias = 0,
                TextureMipBias = 0,
                MaxLights = 256,
                AAMethod = 1
            },
            QualityLevel.Epic => new QualityPreset
            {
                Level = QualityLevel.Epic,
                ResolutionScale = 1.0f,
                ShadowMapSize = 4096,
                ShadowCascadeCount = 4,
                EnableSSAO = true,
                SSAOSampleCount = 32,
                EnableBloom = true,
                EnableMotionBlur = true,
                EnableTAA = true,
                LODBias = 0,
                TextureMipBias = 0,
                MaxLights = 512,
                AAMethod = 1
            },
            QualityLevel.Cinematic => new QualityPreset
            {
                Level = QualityLevel.Cinematic,
                ResolutionScale = 1.0f,
                ShadowMapSize = 4096,
                ShadowCascadeCount = 4,
                EnableSSAO = true,
                SSAOSampleCount = 64,
                EnableBloom = true,
                EnableMotionBlur = true,
                EnableTAA = true,
                LODBias = 0,
                TextureMipBias = 0,
                MaxLights = 1024,
                AAMethod = 2 // TSR
            },
            _ => GetPreset(QualityLevel.High)
        };
    }

    // ──────────────────────────────────────────────────────────────
    // 自适应质量状态快照
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 当前自适应质量系统的状态快照，用于 UI 显示或调试。
    /// </summary>
    public struct AdaptiveQualityState
    {
        /// <summary>当前质量等级。</summary>
        public QualityLevel CurrentQualityLevel;
        /// <summary>当前分辨率缩放。</summary>
        public float ResolutionScale;
        /// <summary>平滑后的 GPU 时间 (毫秒)。</summary>
        public float SmoothedGPUTimeMs;
        /// <summary>目标帧时间 (毫秒)。</summary>
        public float TargetFrameTimeMs;
        /// <summary>帧率 (FPS)。</summary>
        public float CurrentFPS;
        /// <summary>是否正在降级。</summary>
        public bool IsDegrading;
        /// <summary>已启用的降级步骤。</summary>
        public DegradationStep ActiveDegradations;
        /// <summary>连续超标帧数。</summary>
        public int OverBudgetFrames;
        /// <summary>连续低于预算帧数。</summary>
        public int UnderBudgetFrames;
    }

    // ══════════════════════════════════════════════════════════════
    //  AdaptiveQualitySystem — 自适应质量系统
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 自适应质量系统: 根据实时 GPU 帧时间动态调整渲染分辨率和渲染质量，
    /// 确保帧率稳定在目标范围内。使用指数移动平均 (EMA) 平滑输入，
    /// 避免分辨率频繁跳变导致画面闪烁。
    /// </summary>
    public sealed class AdaptiveQualitySystem
    {
        // ───── 帧时间配置 ─────
        private float _targetGPUTimeMs = 16.67f;    // 60fps
        private float _targetFPSThreshold = 60.0f;

        // ───── 分辨率缩放 ─────
        private float _minResolutionScale = 0.5f;
        private float _maxResolutionScale = 1.5f;
        private float _resolutionScale = 1.0f;

        // ───── EMA 平滑 ─────
        private float _smoothedGPUTimeMs = 16.67f;
        private float _emaAlpha = 0.1f;  // EMA 平滑系数 (越小越平滑，响应越慢)
        private int _historyWindow = 30; // 历史窗口长度 (帧)
        private readonly float[] _gpuTimeHistory = new float[30];
        private int _historyIndex;
        private int _historyCount;

        // ───── 自动调整参数 ─────
        private float _upScaleRate = 0.01f;     // 每帧最大升分辨率速率
        private float _downScaleRate = 0.05f;   // 每帧最大降分辨率速率
        private float _overBudgetThreshold = 1.1f;  // GPU 时间超过目标 110% 触发降级
        private float _underBudgetThreshold = 0.8f; // GPU 时间低于目标 80% 允许升级

        // ───── 质量状态 ─────
        private QualityLevel _currentQualityLevel = QualityLevel.High;
        private QualityPreset _currentPreset = QualityPreset.GetPreset(QualityLevel.High);
        private DegradationStep _activeDegradations;
        private bool _isDegrading;
        private int _overBudgetFrames;
        private int _underBudgetFrames;
        private int _overBudgetTolerance = 5;   // 连续 N 帧超标才开始降级
        private int _underBudgetTolerance = 30;  // 连续 N 帧低于预算才升级 (保守)

        // ───── 事件回调 ─────
        /// <summary>
        /// 分辨率缩放变化时触发。参数: 新的分辨率缩放值。
        /// </summary>
        public event Action<float>? OnResolutionScaleChanged;

        /// <summary>
        /// 质量等级变化时触发。参数: 新的质量等级。
        /// </summary>
        public event Action<QualityLevel>? OnQualityLevelChanged;

        /// <summary>
        /// 降级步骤变化时触发。参数: 当前活跃的降级标志。
        /// </summary>
        public event Action<DegradationStep>? OnDegradationChanged;

        // ════════════════════════════════════════════════════════
        //  属性
        // ════════════════════════════════════════════════════════

        /// <summary>目标帧时间 (毫秒)。默认 16.67ms (60fps)。</summary>
        public float TargetGPUTime
        {
            get => _targetGPUTimeMs;
            set => _targetGPUTimeMs = Math.Clamp(value, 5.0f, 50.0f);
        }

        /// <summary>最低分辨率缩放因子。默认 0.5。</summary>
        public float MinResolutionScale
        {
            get => _minResolutionScale;
            set => _minResolutionScale = Math.Clamp(value, 0.25f, 1.0f);
        }

        /// <summary>最高分辨率缩放因子。默认 1.5。</summary>
        public float MaxResolutionScale
        {
            get => _maxResolutionScale;
            set => _maxResolutionScale = Math.Clamp(value, 1.0f, 2.0f);
        }

        /// <summary>当前分辨率缩放因子。</summary>
        public float ResolutionScale => _resolutionScale;

        /// <summary>当前质量等级。</summary>
        public QualityLevel CurrentQualityLevel => _currentQualityLevel;

        /// <summary>平滑后的 GPU 时间 (毫秒)。</summary>
        public float SmoothedGPUTimeMs => _smoothedGPUTimeMs;

        /// <summary>当前质量预设。</summary>
        public QualityPreset CurrentPreset => _currentPreset;

        /// <summary>当前帧率 (基于平滑 GPU 时间)。</summary>
        public float CurrentFPS => _smoothedGPUTimeMs > 0.01f ? 1000.0f / _smoothedGPUTimeMs : 999.0f;

        // ════════════════════════════════════════════════════════
        //  核心更新
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 每帧更新。根据 GPU 时间自动调整分辨率和质量。
        /// </summary>
        /// <param name="gpuTimeMs">当前帧 GPU 执行时间 (毫秒)。</param>
        /// <param name="deltaTime">帧间隔时间 (秒)。</param>
        public void Update(float gpuTimeMs, float deltaTime)
        {
            // 1. 更新 EMA 平滑值
            UpdateEMA(gpuTimeMs);

            // 2. 判断帧时间状态
            float ratio = _smoothedGPUTimeMs / _targetGPUTimeMs;

            if (ratio > _overBudgetThreshold)
            {
                // GPU 时间超标
                _overBudgetFrames++;
                _underBudgetFrames = 0;

                if (_overBudgetFrames >= _overBudgetTolerance)
                {
                    DegradeQuality(ratio);
                }
            }
            else if (ratio < _underBudgetThreshold)
            {
                // GPU 时间低于预算，有提升空间
                _underBudgetFrames++;
                _overBudgetFrames = 0;

                if (_underBudgetFrames >= _underBudgetTolerance)
                {
                    UpgradeQuality(ratio);
                }
            }
            else
            {
                // 在目标范围内
                _overBudgetFrames = Math.Max(0, _overBudgetFrames - 1);
                _underBudgetFrames = Math.Max(0, _underBudgetFrames - 1);
            }
        }

        // ════════════════════════════════════════════════════════
        //  EMA 平滑
        // ════════════════════════════════════════════════════════

        private void UpdateEMA(float gpuTimeMs)
        {
            // 过滤异常值 (如加载帧、卡顿帧)
            if (gpuTimeMs > _targetGPUTimeMs * 5.0f || gpuTimeMs < 0.1f)
            {
                // 不更新 EMA，避免尖峰污染
                return;
            }

            // 指数移动平均
            _smoothedGPUTimeMs = _emaAlpha * gpuTimeMs + (1.0f - _emaAlpha) * _smoothedGPUTimeMs;

            // 记录历史
            _gpuTimeHistory[_historyIndex] = gpuTimeMs;
            _historyIndex = (_historyIndex + 1) % _historyWindow;
            if (_historyCount < _historyWindow)
                _historyCount++;
        }

        // ════════════════════════════════════════════════════════
        //  降级逻辑
        // ════════════════════════════════════════════════════════

        private void DegradeQuality(float overRatio)
        {
            _isDegrading = true;

            // 策略1: 降低分辨率缩放 (最直接有效)
            if (_resolutionScale > _minResolutionScale)
            {
                float excess = (overRatio - 1.0f) * _resolutionScale;
                float newScale = _resolutionScale - Math.Min(excess, _downScaleRate);
                newScale = Math.Clamp(newScale, _minResolutionScale, _maxResolutionScale);
                SetResolutionScaleInternal(newScale);
                return;
            }

            // 策略2: 分辨率已到下限，开始降级其他质量
            if (!HasDegradation(DegradationStep.ReduceShadowQuality))
            {
                ApplyDegradation(DegradationStep.ReduceShadowQuality);
                return;
            }

            if (!HasDegradation(DegradationStep.DisableSSAO))
            {
                ApplyDegradation(DegradationStep.DisableSSAO);
                return;
            }

            if (!HasDegradation(DegradationStep.ReducePostProcess))
            {
                ApplyDegradation(DegradationStep.ReducePostProcess);
                return;
            }

            if (!HasDegradation(DegradationStep.IncreaseLODBias))
            {
                ApplyDegradation(DegradationStep.IncreaseLODBias);
                return;
            }
        }

        private void UpgradeQuality(float underRatio)
        {
            _isDegrading = false;

            // 策略: 先恢复降级步骤，再提升分辨率

            if (HasDegradation(DegradationStep.IncreaseLODBias))
            {
                RemoveDegradation(DegradationStep.IncreaseLODBias);
                return;
            }

            if (HasDegradation(DegradationStep.ReducePostProcess))
            {
                RemoveDegradation(DegradationStep.ReducePostProcess);
                return;
            }

            if (HasDegradation(DegradationStep.DisableSSAO))
            {
                RemoveDegradation(DegradationStep.DisableSSAO);
                return;
            }

            if (HasDegradation(DegradationStep.ReduceShadowQuality))
            {
                RemoveDegradation(DegradationStep.ReduceShadowQuality);
                return;
            }

            // 所有降级已恢复，开始提升分辨率
            if (_resolutionScale < _currentPreset.ResolutionScale)
            {
                float newScale = _resolutionScale + Math.Min(_upScaleRate, 0.01f);
                newScale = Math.Clamp(newScale, _minResolutionScale, Math.Min(_currentPreset.ResolutionScale, _maxResolutionScale));
                SetResolutionScaleInternal(newScale);
            }
        }

        // ════════════════════════════════════════════════════════
        //  降级步骤管理
        // ════════════════════════════════════════════════════════

        private bool HasDegradation(DegradationStep step)
            => (_activeDegradations & step) != 0;

        private void ApplyDegradation(DegradationStep step)
        {
            _activeDegradations |= step;
            OnDegradationChanged?.Invoke(_activeDegradations);
        }

        private void RemoveDegradation(DegradationStep step)
        {
            _activeDegradations &= ~step;
            OnDegradationChanged?.Invoke(_activeDegradations);
        }

        // ════════════════════════════════════════════════════════
        //  分辨率缩放
        // ════════════════════════════════════════════════════════

        private void SetResolutionScaleInternal(float newScale)
        {
            if (Math.Abs(newScale - _resolutionScale) < 0.001f)
                return;

            _resolutionScale = newScale;
            OnResolutionScaleChanged?.Invoke(_resolutionScale);
        }

        /// <summary>
        /// 手动设置分辨率缩放 (覆盖自适应调整)。
        /// </summary>
        public void SetResolutionScaleManual(float scale)
        {
            _resolutionScale = Math.Clamp(scale, _minResolutionScale, _maxResolutionScale);
            // 重置降级计数器
            _overBudgetFrames = 0;
            _underBudgetFrames = 0;
        }

        // ════════════════════════════════════════════════════════
        //  质量等级设置
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 设置质量等级预设。重置所有降级步骤并应用新预设。
        /// </summary>
        /// <param name="level">目标质量等级。</param>
        public void SetQualityLevel(QualityLevel level)
        {
            _currentQualityLevel = level;
            _currentPreset = QualityPreset.GetPreset(level);

            // 重置分辨率到预设值
            _resolutionScale = _currentPreset.ResolutionScale;

            // 清除所有降级
            _activeDegradations = DegradationStep.None;
            _isDegrading = false;
            _overBudgetFrames = 0;
            _underBudgetFrames = 0;

            // 触发事件
            OnQualityLevelChanged?.Invoke(level);
            OnResolutionScaleChanged?.Invoke(_resolutionScale);
            OnDegradationChanged?.Invoke(_activeDegradations);
        }

        /// <summary>
        /// 根据目标帧率自动选择合适的质量等级。
        /// </summary>
        public void AutoDetectQualityLevel(float targetFPS)
        {
            QualityLevel recommended = targetFPS switch
            {
                >= 120 => QualityLevel.Low,
                >= 90 => QualityLevel.Medium,
                >= 60 => QualityLevel.High,
                >= 30 => QualityLevel.Epic,
                _ => QualityLevel.Cinematic
            };
            SetQualityLevel(recommended);
        }

        // ════════════════════════════════════════════════════════
        //  状态查询
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 获取当前自适应质量系统的完整状态快照。
        /// </summary>
        public AdaptiveQualityState GetState()
        {
            return new AdaptiveQualityState
            {
                CurrentQualityLevel = _currentQualityLevel,
                ResolutionScale = _resolutionScale,
                SmoothedGPUTimeMs = _smoothedGPUTimeMs,
                TargetFrameTimeMs = _targetGPUTimeMs,
                CurrentFPS = CurrentFPS,
                IsDegrading = _isDegrading,
                ActiveDegradations = _activeDegradations,
                OverBudgetFrames = _overBudgetFrames,
                UnderBudgetFrames = _underBudgetFrames
            };
        }

        /// <summary>
        /// 获取历史 GPU 时间平均值 (毫秒)。
        /// </summary>
        public float GetAverageGPUTime()
        {
            if (_historyCount == 0) return _smoothedGPUTimeMs;
            float sum = 0f;
            int count = Math.Min(_historyCount, _historyWindow);
            for (int i = 0; i < count; i++)
                sum += _gpuTimeHistory[i];
            return sum / count;
        }

        /// <summary>
        /// 获取历史 GPU 时间中第 N 百分位值。
        /// </summary>
        public float GetGPUTimePercentile(float percentile)
        {
            if (_historyCount == 0) return _smoothedGPUTimeMs;

            int count = Math.Min(_historyCount, _historyWindow);
            var samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = _gpuTimeHistory[i];

            Array.Sort(samples);
            int idx = Math.Clamp((int)(percentile * count / 100.0f), 0, count - 1);
            return samples[idx];
        }

        /// <summary>
        /// 重置自适应系统到初始状态。
        /// </summary>
        public void Reset()
        {
            _smoothedGPUTimeMs = _targetGPUTimeMs;
            _historyIndex = 0;
            _historyCount = 0;
            _overBudgetFrames = 0;
            _underBudgetFrames = 0;
            _isDegrading = false;
            _activeDegradations = DegradationStep.None;
            _resolutionScale = _currentPreset.ResolutionScale;

            for (int i = 0; i < _historyWindow; i++)
                _gpuTimeHistory[i] = 0f;
        }

        /// <summary>
        /// 生成调试信息字符串。
        /// </summary>
        public override string ToString()
        {
            var state = GetState();
            return $"[AQS] Level={state.CurrentQualityLevel} Scale={state.ResolutionScale:F3} " +
                   $"GPU={state.SmoothedGPUTimeMs:F2}ms FPS={state.CurrentFPS:F1} " +
                   $"Degrading={state.IsDegrading} Degradations={state.ActiveDegradations}";
        }
    }
}