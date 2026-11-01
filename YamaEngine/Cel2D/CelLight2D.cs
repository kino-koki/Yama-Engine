using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cel2D
{
    /// <summary>
    /// 2D 赛璐璐灯光类型。
    /// </summary>
    public enum CelLightType2D
    {
        /// <summary>定向光（平行光，无衰减，整场景同一方向）。</summary>
        Directional = 0,

        /// <summary>点光（从中心向外，距离衰减）。</summary>
        Point = 1,

        /// <summary>区域光（矩形区域，用于 UI 高光 / 渐变光斑）。</summary>
        Area = 2,
    }

    /// <summary>
    /// 2D 赛璐璐灯光。影响受光体的量化明暗与边缘光。
    /// 纯数据层：只携带参数，由 <see cref="CelShading2DSystem"/> 采样后参与着色。
    /// </summary>
    public sealed class CelLight2D
    {
        /// <summary>灯光类型。</summary>
        public CelLightType2D Type { get; set; } = CelLightType2D.Point;

        /// <summary>灯光颜色（RGB）。</summary>
        public Vector3 Color { get; set; } = Vector3.One;

        /// <summary>灯光强度（0 表示关闭）。</summary>
        public float Intensity { get; set; } = 1.0f;

        /// <summary>
        /// 定向光方向（单位向量，指向光线传播方向）。
        /// 对 Point / Area 类型忽略。
        /// </summary>
        public Vector2 Direction { get; set; } = new Vector2(0.0f, -1.0f);

        /// <summary>点光 / 区域光世界坐标。</summary>
        public Vector2 Position { get; set; }

        /// <summary>点光衰减半径（像素）。超过该半径不再受光。</summary>
        public float Radius { get; set; } = 512.0f;

        /// <summary>
        /// 衰减指数（平滑衰减）。1 = 线性，2 = 平方衰减（推荐）。
        /// </summary>
        public float Falloff { get; set; } = 2.0f;

        /// <summary>区域光尺寸（宽 / 高，像素）。仅 <see cref="CelLightType2D.Area"/>。</summary>
        public Vector2 AreaSize { get; set; } = new Vector2(256.0f, 256.0f);

        /// <summary>区域光旋转角（弧度）。仅 Area 类型。</summary>
        public float AreaRotation { get; set; }

        /// <summary>该灯光是否影响受光体量化（否则仅影响边缘光/高光）。</summary>
        public bool AffectsShading { get; set; } = true;

        /// <summary>该灯光是否参与边缘光（Rim）计算。</summary>
        public bool AffectsRim { get; set; } = true;

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 计算指定世界点在该灯光下的辐射强度（0..1）。
        /// 返回已乘 <see cref="Intensity"/> 的强度值。
        /// </summary>
        /// <param name="worldPoint">世界坐标。</param>
        /// <param name="normal">该点法线（用于定向光朗伯项，可选）。</param>
        public float Evaluate(Vector2 worldPoint, Vector2? normal = null)
        {
            if (!Enabled || Intensity <= 0.0f)
                return 0.0f;

            switch (Type)
            {
                case CelLightType2D.Directional:
                {
                    // 定向光：朗伯余弦项，法线朝光则满强度。
                    if (normal.HasValue)
                    {
                        float ndl = Vector2.Dot(normal.Value, -Direction);
                        if (ndl <= 0.0f)
                            return 0.0f;
                        return Intensity * ndl;
                    }
                    return Intensity;
                }

                case CelLightType2D.Point:
                {
                    float dist = Vector2.Distance(Position, worldPoint);
                    if (dist >= Radius || dist <= 0.0001f)
                        return 0.0f;
                    float t = dist / Radius;                 // 0..1
                    float fall = 1.0f - MathF.Pow(t, Falloff);
                    return Intensity * MathF.Max(0.0f, fall);
                }

                case CelLightType2D.Area:
                {
                    // 把点变换到区域光局部空间，按 AABB 距离衰减。
                    Vector2 local = Rotate(worldPoint - Position, -AreaRotation);
                    float halfW = AreaSize.X * 0.5f;
                    float halfH = AreaSize.Y * 0.5f;
                    float dx = MathF.Max(MathF.Abs(local.X) - halfW, 0.0f);
                    float dy = MathF.Max(MathF.Abs(local.Y) - halfH, 0.0f);
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float fall = 1.0f - MathF.Pow(MathF.Min(d / Radius, 1.0f), Falloff);
                    return Intensity * MathF.Max(0.0f, fall);
                }

                default:
                    return 0.0f;
            }
        }

        private static Vector2 Rotate(Vector2 v, float angle)
        {
            float c = MathF.Cos(angle);
            float s = MathF.Sin(angle);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }

        /// <summary>深拷贝。</summary>
        public CelLight2D Clone()
        {
            return new CelLight2D
            {
                Type = Type,
                Color = Color,
                Intensity = Intensity,
                Direction = Direction,
                Position = Position,
                Radius = Radius,
                Falloff = Falloff,
                AreaSize = AreaSize,
                AreaRotation = AreaRotation,
                AffectsShading = AffectsShading,
                AffectsRim = AffectsRim,
                Enabled = Enabled,
            };
        }
    }

    /// <summary>
    /// 2D 灯光集合管理器：持有灯光列表，并提供聚合求值。
    /// 纯数据层。
    /// </summary>
    public sealed class CelLightRig2D
    {
        private readonly List<CelLight2D> _lights = new();

        public IReadOnlyList<CelLight2D> Lights => _lights;

        public void Add(CelLight2D light) => _lights.Add(light);

        public void Remove(CelLight2D light) => _lights.Remove(light);

        public void Clear() => _lights.Clear();

        /// <summary>
        /// 求某点的总着色辐射（AffectsShading 的灯光累加，可超 1）。
        /// </summary>
        public float EvaluateShading(Vector2 worldPoint, Vector2? normal = null)
        {
            float sum = 0.0f;
            foreach (var l in _lights)
            {
                if (l.AffectsShading)
                    sum += l.Evaluate(worldPoint, normal);
            }
            return sum;
        }

        /// <summary>
        /// 求某点的总边缘光辐射（AffectsRim 的灯光累加，可超 1）。
        /// </summary>
        public float EvaluateRim(Vector2 worldPoint)
        {
            float sum = 0.0f;
            foreach (var l in _lights)
            {
                if (l.AffectsRim)
                    sum += l.Evaluate(worldPoint);
            }
            return sum;
        }

        /// <summary>
        /// 组合颜色：以主灯光（或纯白默认）为基，累加所有灯光的颜色贡献。
        /// </summary>
        public Vector3 EvaluateColor(Vector2 worldPoint, Vector3 baseColor)
        {
            Vector3 acc = Vector3.Zero;
            float maxI = 0.0f;
            foreach (var l in _lights)
            {
                float e = l.Evaluate(worldPoint);
                if (e > maxI) maxI = e;
                acc += l.Color * e;
            }
            if (maxI <= 0.0001f)
                return baseColor;
            // 用最大强度灯主导色调，其余叠加。
            Vector3 dominant = baseColor;
            foreach (var l in _lights)
            {
                if (l.Evaluate(worldPoint) >= maxI - 0.0001f)
                {
                    dominant = l.Color;
                    break;
                }
            }
            Vector3 result = dominant * maxI + acc * 0.35f;
            return new Vector3(
                MathF.Min(result.X, 1.0f),
                MathF.Min(result.Y, 1.0f),
                MathF.Min(result.Z, 1.0f));
        }
    }
}
