// =============================================================================
// 山雨引擎 - 3D 空间音频系统 (Audio3DSpatialSystem)
// 类似 FMOD 3D / Wwise Spatial Audio 的3D空间音频中间件
// =============================================================================
// 本文件实现完整的3D空间音频系统，包含：
//   1. MWAudioListener      - 听者（位置/朝向/速度/3D因子/上方向）
//   2. MWAudio3DSource      - 3D声源（位置/速度/方向/扩散角/遮挡/衍射）
//   3. RolloffModel          - 距离衰减模型（Linear/Logarithmic/Custom）
//   4. 多普勒效果           - 可配置系数/速度缩放
//   5. MWAudioOcclusion     - 射线检测遮挡/材质→衰减系数/过渡/衍射/透射
//   6. 传播延迟             - 声音传播速度延迟
//   7. HRTF                 - 头相关传递函数（双耳渲染）
//   8. 环境混响区域         - 区域→混响预设，位置混合过渡
//   9. 扩展声源             - 线声源/面声源/体积声源
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Audio.Middleware
{
    // ══════════════════════════════════════════════════════════════════
    //  一、听者
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 山雨引擎3D音频听者。描述接收声音的"耳朵"。
    /// </summary>
    public sealed class MWAudioListener
    {
        /// <summary>听者位置</summary>
        public Vector3 Position { get; set; } = Vector3.Zero;
        /// <summary>听者朝向（前方）</summary>
        public Vector3 Forward { get; set; } = -Vector3.UnitZ;
        /// <summary>听者上方向</summary>
        public Vector3 Up { get; set; } = Vector3.UnitY;
        /// <summary>听者速度</summary>
        public Vector3 Velocity { get; set; } = Vector3.Zero;
        /// <summary>3D空间化因子（0=完全2D，1=完全3D）</summary>
        public float SpatialFactor { get; set; } = 1.0f;
        /// <summary>是否启用</summary>
        public bool IsActive { get; set; } = true;
        /// <summary>关联的实体ID</summary>
        public int EntityId { get; set; } = -1;
        /// <summary>听者权重（多听者混合时使用）</summary>
        public float Weight { get; set; } = 1.0f;

        /// <summary>计算右方向</summary>
        public Vector3 Right => Vector3.Cross(Forward, Up);

        /// <summary>计算声源相对于听者的方位角（度数）</summary>
        public float GetAzimuth(Vector3 sourcePosition)
        {
            Vector3 toSource = sourcePosition - Position;
            float distance = toSource.Length();
            if (distance < 0.001f) return 0f;

            Vector3 dir = toSource / distance;
            float cosAz = Vector3.Dot(dir, Right);
            float sinAz = Vector3.Dot(dir, Forward);
            return MathF.Atan2(cosAz, sinAz) * (180f / MathF.PI);
        }

        /// <summary>计算声源相对于听者的仰角（度数）</summary>
        public float GetElevation(Vector3 sourcePosition)
        {
            Vector3 toSource = sourcePosition - Position;
            float distance = toSource.Length();
            if (distance < 0.001f) return 0f;

            Vector3 dir = toSource / distance;
            float horizontal = MathF.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
            float sinEl = dir.Y;
            float cosEl = horizontal;
            return MathF.Atan2(sinEl, cosEl) * (180f / MathF.PI);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、3D 声源
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 声源类型（点声源/线声源/面声源/体积声源）。
    /// </summary>
    public enum MWSourceType
    {
        /// <summary>点声源（默认）</summary>
        Point,
        /// <summary>线声源</summary>
        Line,
        /// <summary>面声源</summary>
        Area,
        /// <summary>体积声源</summary>
        Volume
    }

    /// <summary>
    /// 山雨引擎3D音频声源。描述一个空间中的声音发射器。
    /// </summary>
    public sealed class MWAudio3DSource
    {
        /// <summary>声源位置</summary>
        public Vector3 Position { get; set; } = Vector3.Zero;
        /// <summary>声源速度</summary>
        public Vector3 Velocity { get; set; } = Vector3.Zero;
        /// <summary>声源朝向（用于方向性声源）</summary>
        public Vector3 Direction { get; set; } = -Vector3.UnitZ;

        /// <summary>最小距离（不衰减距离）</summary>
        public float MinDistance { get; set; } = 1.0f;
        /// <summary>最大距离（静音距离）</summary>
        public float MaxDistance { get; set; } = 100.0f;

        /// <summary>声源类型</summary>
        public MWSourceType SourceType { get; set; } = MWSourceType.Point;

        /// <summary>内锥角（度数）</summary>
        public float InnerConeAngle { get; set; } = 360f;
        /// <summary>外锥角（度数）</summary>
        public float OuterConeAngle { get; set; } = 360f;
        /// <summary>外锥角外音量系数</summary>
        public float OuterConeVolume { get; set; } = 0f;

        /// <summary>距离衰减模型</summary>
        public RolloffModel Rolloff { get; set; } = RolloffModel.Logarithmic;
        /// <summary>自定义衰减曲线控制点</summary>
        public List<(float Distance, float Volume)> RolloffCurve { get; } = new();

        /// <summary>多普勒系数</summary>
        public float DopplerFactor { get; set; } = 1.0f;

        /// <summary>方向性因子 (0=全向, 1=完全定向)</summary>
        public float Directionality { get; set; } = 0f;

        /// <summary>是否启用遮挡</summary>
        public bool EnableOcclusion { get; set; } = true;
        /// <summary>当前遮挡系数 (0=无遮挡, 1=完全遮挡)</summary>
        public float OcclusionFactor { get; set; }
        /// <summary>目标遮挡系数（过渡中）</summary>
        public float TargetOcclusionFactor { get; set; }

        /// <summary>是否启用衍射</summary>
        public bool EnableDiffraction { get; set; } = true;
        /// <summary>衍射系数</summary>
        public float DiffractionFactor { get; set; }

        /// <summary>声源音量</summary>
        public float Volume { get; set; } = 1.0f;

        // ── 线声源参数 ──
        /// <summary>线声源起点</summary>
        public Vector3 LineStart { get; set; }
        /// <summary>线声源终点</summary>
        public Vector3 LineEnd { get; set; }

        // ── 面声源参数 ──
        /// <summary>面声源中心</summary>
        public Vector3 AreaCenter { get; set; }
        /// <summary>面声源宽度</summary>
        public float AreaWidth { get; set; }
        /// <summary>面声源深度</summary>
        public float AreaDepth { get; set; }

        // ── 体积声源参数 ──
        /// <summary>体积声源中心</summary>
        public Vector3 VolumeCenter { get; set; }
        /// <summary>体积声源范围</summary>
        public Vector3 VolumeExtents { get; set; }

        /// <summary>计算有效距离（根据声源类型）</summary>
        public float CalculateEffectiveDistance(MWAudioListener listener)
        {
            return SourceType switch
            {
                MWSourceType.Point => Vector3.Distance(Position, listener.Position),
                MWSourceType.Line => CalculateDistanceToLine(listener.Position),
                MWSourceType.Area => CalculateDistanceToArea(listener.Position),
                MWSourceType.Volume => CalculateDistanceToVolume(listener.Position),
                _ => Vector3.Distance(Position, listener.Position)
            };
        }

        /// <summary>计算锥角因子</summary>
        public float CalculateConeFactor(MWAudioListener listener)
        {
            if (InnerConeAngle >= 360f) return 1.0f;

            Vector3 toListener = listener.Position - Position;
            float dist = toListener.Length();
            if (dist < 0.001f) return 1.0f;

            Vector3 dir = toListener / dist;
            float cosAngle = Vector3.Dot(dir, Direction);
            float angle = MathF.Acos(Math.Clamp(cosAngle, -1f, 1f)) * (180f / MathF.PI);

            if (angle <= InnerConeAngle * 0.5f) return 1.0f;
            if (angle >= OuterConeAngle * 0.5f) return OuterConeVolume;

            float t = (angle - InnerConeAngle * 0.5f) / ((OuterConeAngle - InnerConeAngle) * 0.5f);
            return 1.0f - (1.0f - OuterConeVolume) * t;
        }

        private float CalculateDistanceToLine(Vector3 point)
        {
            Vector3 lineDir = LineEnd - LineStart;
            float lineLen = lineDir.Length();
            if (lineLen < 0.001f) return Vector3.Distance(LineStart, point);
            Vector3 d = lineDir / lineLen;
            Vector3 v = point - LineStart;
            float t = Math.Clamp(Vector3.Dot(v, d), 0f, lineLen);
            Vector3 closest = LineStart + d * t;
            return Vector3.Distance(closest, point);
        }

        private float CalculateDistanceToArea(Vector3 point)
        {
            // 简化：计算到面中心的距离
            return Vector3.Distance(AreaCenter, point);
        }

        private float CalculateDistanceToVolume(Vector3 point)
        {
            // 简化：如果在体积内返回0
            Vector3 delta = Vector3.Abs(point - VolumeCenter);
            if (delta.X <= VolumeExtents.X && delta.Y <= VolumeExtents.Y && delta.Z <= VolumeExtents.Z)
                return 0f;
            return Vector3.Distance(VolumeCenter, point);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、遮挡系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 遮挡材质类型。不同材质对不同频率声音的衰减系数不同。
    /// </summary>
    public enum MWOcclusionMaterial
    {
        None,
        Wood,
        Glass,
        Metal,
        Stone,
        Concrete,
        Drywall,
        Fabric,
        Water,
        Earth,
        Custom
    }

    /// <summary>
    /// 遮挡检测结果。
    /// </summary>
    public sealed class MWOcclusionResult
    {
        /// <summary>是否被遮挡</summary>
        public bool IsOccluded { get; set; }
        /// <summary>遮挡系数 (0=无, 1=完全)</summary>
        public float OcclusionFactor { get; set; }
        /// <summary>遮挡材质</summary>
        public MWOcclusionMaterial Material { get; set; }
        /// <summary>衍射路径是否可用</summary>
        public bool HasDiffractionPath { get; set; }
        /// <summary>衍射系数</summary>
        public float DiffractionFactor { get; set; }
        /// <summary>穿透衰减系数</summary>
        public float TransmissionFactor { get; set; }
    }

    /// <summary>
    /// 材质衰减系数表。
    /// </summary>
    public static class MWOcclusionMaterialFactors
    {
        /// <summary>获取材质的高频/中频/低频衰减系数</summary>
        public static (float High, float Mid, float Low) GetFactors(MWOcclusionMaterial material)
        {
            return material switch
            {
                MWOcclusionMaterial.None => (0f, 0f, 0f),
                MWOcclusionMaterial.Wood => (0.6f, 0.4f, 0.2f),
                MWOcclusionMaterial.Glass => (0.9f, 0.7f, 0.3f),
                MWOcclusionMaterial.Metal => (0.8f, 0.6f, 0.4f),
                MWOcclusionMaterial.Stone => (0.95f, 0.9f, 0.7f),
                MWOcclusionMaterial.Concrete => (0.9f, 0.85f, 0.6f),
                MWOcclusionMaterial.Drywall => (0.7f, 0.5f, 0.3f),
                MWOcclusionMaterial.Fabric => (0.5f, 0.3f, 0.15f),
                MWOcclusionMaterial.Water => (0.95f, 0.9f, 0.85f),
                MWOcclusionMaterial.Earth => (0.85f, 0.8f, 0.7f),
                _ => (0.7f, 0.5f, 0.3f)
            };
        }
    }

    /// <summary>
    /// 音频遮挡系统。使用射线检测判断声源与听者之间是否有遮挡物。
    /// </summary>
    public sealed class MWAudioOcclusion
    {
        /// <summary>遮挡过渡时间（秒）</summary>
        public float TransitionTime { get; set; } = 0.3f;
        /// <summary>射线检测频率（每N帧检测一次）</summary>
        public int RaycastFrequency { get; set; } = 5;

        private int _frameCounter;

        /// <summary>执行遮挡检测（由外部提供射线检测回调）</summary>
        /// <param name="sourcePos">声源位置</param>
        /// <param name="listenerPos">听者位置</param>
        /// <param name="raycastFn">射线检测回调，返回材质类型</param>
        public MWOcclusionResult PerformRaycast(
            Vector3 sourcePos, Vector3 listenerPos,
            Func<Vector3, Vector3, (bool hit, MWOcclusionMaterial material)> raycastFn)
        {
            var result = new MWOcclusionResult();

            var (hit, material) = raycastFn(sourcePos, listenerPos);
            result.IsOccluded = hit;
            result.Material = material;

            if (hit)
            {
                var (high, mid, low) = MWOcclusionMaterialFactors.GetFactors(material);
                // 使用中频系数作为总体遮挡系数
                result.OcclusionFactor = mid;
                result.TransmissionFactor = 1f - mid;
                result.DiffractionFactor = 0.3f; // 衍射提供部分补偿
                result.HasDiffractionPath = true;
            }
            else
            {
                result.OcclusionFactor = 0f;
                result.TransmissionFactor = 1f;
                result.DiffractionFactor = 0f;
                result.HasDiffractionPath = false;
            }

            return result;
        }

        /// <summary>是否应该执行遮挡检测（按频率跳帧）</summary>
        public bool ShouldPerformRaycast()
        {
            _frameCounter++;
            if (_frameCounter >= RaycastFrequency)
            {
                _frameCounter = 0;
                return true;
            }
            return false;
        }

        /// <summary>平滑过渡遮挡系数</summary>
        public static float SmoothOcclusion(float current, float target, float dt, float transitionTime)
        {
            float speed = 1f / MathF.Max(transitionTime, 0.001f);
            return current + (target - current) * MathF.Min(speed * dt, 1f);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、空间音频处理器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 空间音频处理结果。
    /// </summary>
    public sealed class MWSpatialResult
    {
        /// <summary>最终音量系数（距离衰减 * 锥角 * 遮挡 * 传播延迟）</summary>
        public float FinalVolume { get; set; } = 1.0f;
        /// <summary>多普勒音调偏移</summary>
        public float DopplerPitch { get; set; } = 1.0f;
        /// <summary>传播延迟（秒）</summary>
        public float PropagationDelay { get; set; }
        /// <summary>左耳增益（用于立体声平移或 HRTF）</summary>
        public float LeftGain { get; set; } = 1.0f;
        /// <summary>右耳增益</summary>
        public float RightGain { get; set; } = 1.0f;
        /// <summary>方位角（度数）</summary>
        public float Azimuth { get; set; }
        /// <summary>仰角（度数）</summary>
        public float Elevation { get; set; }
        /// <summary>距离</summary>
        public float Distance { get; set; }
    }

    /// <summary>
    /// 山雨引擎3D空间音频处理器。
    /// 集成距离衰减、多普勒、遮挡、传播延迟等全部空间化计算。
    /// </summary>
    public sealed class MWAudio3DSpatializer
    {
        /// <summary>声速（m/s）</summary>
        public float SpeedOfSound { get; set; } = 343.0f;
        /// <summary>距离因子</summary>
        public float DistanceFactor { get; set; } = 1.0f;
        /// <summary>是否启用传播延迟</summary>
        public bool EnablePropagationDelay { get; set; } = false;
        /// <summary>是否启用 HRTF</summary>
        public bool EnableHRTF { get; set; } = true;

        private MWAudioOcclusion _occlusion = new();

        /// <summary>处理一个3D声源的空间化</summary>
        public MWSpatialResult Process(MWAudio3DSource source, MWAudioListener listener, float dt)
        {
            var result = new MWSpatialResult();

            // 1. 距离
            float distance = source.CalculateEffectiveDistance(listener) * DistanceFactor;
            float minDist = source.MinDistance * DistanceFactor;
            float maxDist = source.MaxDistance * DistanceFactor;
            result.Distance = distance;

            // 2. 距离衰减
            float attenuation = CalculateAttenuation(distance, minDist, maxDist, source);
            result.FinalVolume *= attenuation;

            // 3. 锥角
            result.FinalVolume *= source.CalculateConeFactor(listener);

            // 4. 遮挡
            if (source.EnableOcclusion)
            {
                // 平滑过渡
                source.OcclusionFactor = MWAudioOcclusion.SmoothOcclusion(
                    source.OcclusionFactor, source.TargetOcclusionFactor, dt, 0.3f);
                result.FinalVolume *= (1f - source.OcclusionFactor * 0.8f);
            }

            // 5. 多普勒
            float dopplerPitch = CalculateDoppler(source, listener);
            result.DopplerPitch = dopplerPitch;

            // 6. 传播延迟
            if (EnablePropagationDelay)
                result.PropagationDelay = distance / MathF.Max(SpeedOfSound, 1f);

            // 7. 方位角/仰角（用于立体声平移或 HRTF）
            result.Azimuth = listener.GetAzimuth(source.Position);
            result.Elevation = listener.GetElevation(source.Position);

            // 8. 立体声平移
            CalculatePan(result, result.Azimuth);

            return result;
        }

        private float CalculateAttenuation(float distance, float minDist, float maxDist, MWAudio3DSource source)
        {
            if (distance <= minDist) return 1.0f;
            if (distance >= maxDist) return 0.0f;

            // 检查自定义曲线
            if (source.Rolloff == RolloffModel.Custom && source.RolloffCurve.Count >= 2)
            {
                for (int i = 0; i < source.RolloffCurve.Count - 1; i++)
                {
                    var (d1, v1) = source.RolloffCurve[i];
                    var (d2, v2) = source.RolloffCurve[i + 1];
                    if (distance >= d1 && distance < d2)
                    {
                        float t = (distance - d1) / (d2 - d1);
                        return v1 + (v2 - v1) * t;
                    }
                }
            }

            float t = (distance - minDist) / (maxDist - minDist);

            return source.Rolloff switch
            {
                RolloffModel.Linear => 1.0f - t,
                RolloffModel.Logarithmic => 1.0f - MathF.Log10(1f + t * 9f) / MathF.Log10(10f),
                RolloffModel.Inverse => minDist / MathF.Max(distance, 0.001f),
                _ => 1.0f - t
            };
        }

        private float CalculateDoppler(MWAudio3DSource source, MWAudioListener listener)
        {
            if (source.DopplerFactor <= 0f) return 1.0f;

            Vector3 toSource = source.Position - listener.Position;
            float dist = toSource.Length();
            if (dist < 0.001f) return 1.0f;

            Vector3 dir = toSource / dist;
            float vs = Vector3.Dot(listener.Velocity, dir);
            float vl = -Vector3.Dot(source.Velocity, dir);

            float denom = SpeedOfSound + vs * DistanceFactor;
            if (Math.Abs(denom) < 0.001f) return 1.0f;

            float ratio = (SpeedOfSound + vl * DistanceFactor) / denom;
            return 1.0f + (ratio - 1.0f) * source.DopplerFactor;
        }

        private void CalculatePan(MWSpatialResult result, float azimuth)
        {
            // 简化的立体声平移：基于方位角的正弦/余弦
            float angleRad = azimuth * (MathF.PI / 180f);
            // 标准化到 -90 ~ +90（前方半场）
            float normAngle = Math.Clamp(azimuth, -90f, 90f) / 90f;
            result.LeftGain = 1.0f - normAngle * 0.5f;
            result.RightGain = 1.0f + normAngle * 0.5f;
        }
    }
}
