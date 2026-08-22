using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.Core;

public enum SplineType { Linear, Bezier, CatmullRom, Hermite, BSpline, Cubic, Quadratic }
public enum SplineWrapMode { Clamp, Loop, PingPong }
public enum SplineTangentMode { Manual, Auto, AutoClamped, Broken, Smooth }
public enum PathFollowMode { PositionOnly, LookAt, PathOriented, SplineOriented }

public struct SplinePoint
{
    public Vector3 Position, InTangent, OutTangent, UpVector;
    public float Scale = 1f, Rotation;
    public SplineTangentMode TangentMode = SplineTangentMode.Auto;
    public SplinePoint(Vector3 p) { Position = p; InTangent = Vector3.Zero; OutTangent = Vector3.Zero; UpVector = Vector3.UnitY; }
}

public sealed class Spline
{
    private readonly List<SplinePoint> _points = new();
    private SplineType _type = SplineType.Bezier;
    private SplineWrapMode _wrap = SplineWrapMode.Clamp;
    private bool _closed;
    private float[]? _segmentLengths;
    private float _totalLength;
    private int _subdivisionSteps = 20;
    private bool _dirty = true;

    public SplineType Type { get => _type; set { _type = value; _dirty = true; } }
    public SplineWrapMode Wrap { get => _wrap; set => _wrap = value; }
    public bool Closed { get => _closed; set { _closed = value; _dirty = true; } }
    public int PointCount => _points.Count;
    public int SegmentCount => _closed ? _points.Count : Math.Max(0, _points.Count - 1);
    public float Length { get { if (_dirty) Recalculate(); return _totalLength; } }

    public SplinePoint GetPoint(int i) => _points[WrapIndex(i)];

    public int AddPoint(Vector3 pos)
    {
        _points.Add(new SplinePoint(pos));
        if (_points.Count > 1) AutoSetTangents(_points.Count - 1);
        _dirty = true; return _points.Count - 1;
    }

    public void InsertPoint(int index, Vector3 pos)
    {
        _points.Insert(index, new SplinePoint(pos));
        AutoSetTangents(index); if (index > 0) AutoSetTangents(index - 1);
        if (index < _points.Count - 1) AutoSetTangents(index + 1);
        _dirty = true;
    }

    public void RemovePoint(int index)
    {
        if (_points.Count <= 2) return;
        _points.RemoveAt(index);
        AutoSetTangents(Math.Max(0, index - 1));
        _dirty = true;
    }

    public void SetPoint(int index, Vector3 pos)
    {
        _points[index] = new SplinePoint(pos);
        AutoSetTangents(index);
        _dirty = true;
    }

    public void Clear() { _points.Clear(); _dirty = true; }

    public Vector3 EvaluatePosition(float t)
    {
        if (_points.Count < 2) return _points.Count > 0 ? _points[0].Position : Vector3.Zero;
        t = NormalizeTime(t);
        int seg; float localT;
        GetSegment(t, out seg, out localT);
        if (seg < 0 || seg >= SegmentCount) return _points[0].Position;

        var p0 = _points[WrapIndex(seg)];
        var p1 = _points[WrapIndex(seg + 1)];
        var p2 = _points[WrapIndex(seg + 2)];
        var p3 = _points[WrapIndex(seg + 3)];

        return _type switch
        {
            SplineType.Linear => Vector3.Lerp(p0.Position, p1.Position, localT),
            SplineType.Bezier => EvaluateCubicBezier(p0.Position, p0.OutTangent, p1.InTangent, p1.Position, localT),
            SplineType.CatmullRom => EvaluateCatmullRom(p0.Position, p1.Position, p2.Position, p3.Position, localT),
            SplineType.Hermite => EvaluateHermite(p0.Position, p1.Position, p0.OutTangent, p1.InTangent, localT),
            SplineType.Cubic => EvaluateCubicBezier(p0.Position, p0.OutTangent, p1.InTangent, p1.Position, localT),
            _ => Vector3.Lerp(p0.Position, p1.Position, localT),
        };
    }

    public Vector3 EvaluateTangent(float t)
    {
        if (_points.Count < 2) return Vector3.Forward;
        t = NormalizeTime(t);
        int seg; float localT;
        GetSegment(t, out seg, out localT);
        if (seg < 0) return Vector3.Forward;

        var p0 = _points[WrapIndex(seg)];
        var p1 = _points[WrapIndex(seg + 1)];
        var p2 = _points[WrapIndex(seg + 2)];

        return _type switch
        {
            SplineType.Linear => Vector3.Normalize(p1.Position - p0.Position),
            SplineType.Bezier => EvaluateCubicBezierTangent(p0.Position, p0.OutTangent, p1.InTangent, p1.Position, localT),
            SplineType.CatmullRom => Vector3.Normalize(p2.Position - p0.Position),
            _ => Vector3.Normalize(p1.Position - p0.Position),
        };
    }

    public Vector3 EvaluateUpVector(float t)
    {
        var p = EvaluatePosition(t); var tgt = EvaluateTangent(t);
        int seg; float lt; GetSegment(NormalizeTime(t), out seg, out lt);
        var sp = _points[WrapIndex(seg + 1)];
        return Vector3.Normalize(Vector3.Cross(tgt, sp.UpVector));
    }

    public Quaternion EvaluateRotation(float t)
    {
        var tgt = EvaluateTangent(t);
        var up = EvaluateUpVector(t);
        if (tgt.LengthSquared() < 0.0001f) return Quaternion.Identity;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, tgt, up)));
    }

    public Matrix4x4 EvaluateWorldMatrix(float t, Vector3 offset = default)
    {
        var pos = EvaluatePosition(t);
        var rot = EvaluateRotation(t);
        return Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos + offset);
    }

    public Vector3 EvaluateDistanceAlong(float distance)
    {
        if (_totalLength <= 0) return _points.Count > 0 ? _points[0].Position : Vector3.Zero;
        return EvaluatePosition(distance / _totalLength);
    }

    public float FindClosestPoint(Vector3 worldPos, int samples = 50)
    {
        float bestT = 0, bestD = float.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            var p = EvaluatePosition(t);
            float d = Vector3.DistanceSquared(p, worldPos);
            if (d < bestD) { bestD = d; bestT = t; }
        }
        // Refine with binary search
        for (int iter = 0; iter < 5; iter++)
        {
            float step = 1f / (samples * (1 << (iter + 1)));
            for (float dt = -step; dt <= step; dt += step * 0.5f)
            {
                float t = Math.Clamp(bestT + dt, 0, 1);
                var p = EvaluatePosition(t);
                float d = Vector3.DistanceSquared(p, worldPos);
                if (d < bestD) { bestD = d; bestT = t; }
            }
        }
        return bestT;
    }

    public (Vector3 position, Vector3 tangent, float curvature) EvaluateCurvature(float t)
    {
        var p = EvaluatePosition(t);
        var tgt = EvaluateTangent(t);
        float eps = 0.001f;
        var p2 = EvaluatePosition(Math.Clamp(t + eps, 0, 1));
        var tgt2 = EvaluateTangent(Math.Clamp(t + eps, 0, 1));
        return (p, tgt, Vector3.Distance(tgt2, tgt) / eps);
    }

    public SplinePoint[] GetPoints() => _points.ToArray();

    private int WrapIndex(int i)
    {
        if (_closed)
        {
            if (i < 0) i += _points.Count;
            return i % _points.Count;
        }
        return Math.Clamp(i, 0, _points.Count - 1);
    }

    private float NormalizeTime(float t)
    {
        switch (_wrap)
        {
            case SplineWrapMode.Clamp: return Math.Clamp(t, 0, 1);
            case SplineWrapMode.Loop: return t - MathF.Floor(t);
            case SplineWrapMode.PingPong:
                float p = t - MathF.Floor(t * 0.5f) * 2;
                return p < 1 ? p : 2 - p;
            default: return Math.Clamp(t, 0, 1);
        }
    }

    private void GetSegment(float t, out int seg, out float localT)
    {
        if (_segmentLengths == null) Recalculate();
        if (_segmentLengths == null || _segmentLengths.Length == 0) { seg = 0; localT = 0; return; }

        float target = t * _totalLength;
        float accum = 0;
        for (int i = 0; i < _segmentLengths.Length; i++)
        {
            if (target <= accum + _segmentLengths[i])
            {
                seg = i; localT = _segmentLengths[i] > 0 ? (target - accum) / _segmentLengths[i] : 0;
                localT = Math.Clamp(localT, 0, 1); return;
            }
            accum += _segmentLengths[i];
        }
        seg = _segmentLengths.Length - 1; localT = 1;
    }

    private void Recalculate()
    {
        int segCount = SegmentCount;
        if (segCount <= 0) { _totalLength = 0; _segmentLengths = null; _dirty = false; return; }

        _segmentLengths = new float[segCount];
        _totalLength = 0;
        for (int i = 0; i < segCount; i++)
        {
            float len = 0; Vector3 prev = _points[WrapIndex(i)].Position;
            for (int s = 1; s <= _subdivisionSteps; s++)
            {
                float t = (float)s / _subdivisionSteps;
                float segT = (float)(i + t) / segCount;
                var curr = EvaluatePositionRaw(i, t);
                len += Vector3.Distance(prev, curr); prev = curr;
            }
            _segmentLengths[i] = len; _totalLength += len;
        }
        _dirty = false;
    }

    private Vector3 EvaluatePositionRaw(int seg, float t)
    {
        var p0 = _points[WrapIndex(seg)];
        var p1 = _points[WrapIndex(seg + 1)];
        if (_points.Count >= 4) { var p2 = _points[WrapIndex(seg + 2)]; var p3 = _points[WrapIndex(seg + 3)]; return EvaluateCatmullRom(p0.Position, p1.Position, p2.Position, p3.Position, t); }
        return Vector3.Lerp(p0.Position, p1.Position, t);
    }

    private void AutoSetTangents(int index)
    {
        if (_points.Count < 2) return;
        var p = _points[index];
        if (p.TangentMode != SplineTangentMode.Auto) return;

        Vector3 prev = index > 0 ? _points[index - 1].Position : _points[0].Position;
        Vector3 next = index < _points.Count - 1 ? _points[index + 1].Position : _points[_points.Count - 1].Position;

        if (_closed && index == 0) prev = _points[_points.Count - 1].Position;
        if (_closed && index == _points.Count - 1) next = _points[0].Position;

        p.InTangent = (p.Position - prev) * 0.5f;
        p.OutTangent = (next - p.Position) * 0.5f;
        _points[index] = p;
    }

    private static Vector3 EvaluateCubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1 - t; float tt = t * t, uu = u * u, uuu = uu * u, ttt = tt * t;
        return uuu * p0 + 3 * uu * t * p1 + 3 * u * tt * p2 + ttt * p3;
    }

    private static Vector3 EvaluateCubicBezierTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1 - t; float ut = 3 * u * u, tt = 3 * t * t;
        return Vector3.Normalize(ut * (p1 - p0) + 6 * u * t * (p2 - p1) + tt * (p3 - p2));
    }

    private static Vector3 EvaluateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float tt = t * t, ttt = tt * t;
        return 0.5f * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * tt + (-p0 + 3 * p1 - 3 * p2 + p3) * ttt);
    }

    private static Vector3 EvaluateHermite(Vector3 p0, Vector3 p1, Vector3 t0, Vector3 t1, float t)
    {
        float tt = t * t, ttt = tt * t;
        float h00 = 2 * ttt - 3 * tt + 1, h10 = ttt - 2 * tt + t, h01 = -2 * ttt + 3 * tt, h11 = ttt - tt;
        return h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
    }
}

public sealed class SplinePathFollower
{
    public Spline Spline = new(); public float Speed = 1f, Progress;
    public PathFollowMode Mode = PathFollowMode.PositionOnly;
    public Vector3 Offset; public bool FollowRotation = true;
    public bool PingPong, Loop, AutoStart = true;
    public float SmoothRotation = 0.5f;
    private bool _forward = true;
    public event Action? OnPathComplete, OnWaypointReached, OnLoop;

    public void Update(float dt, ref Vector3 position, ref Quaternion rotation)
    {
        if (!AutoStart) return;
        float delta = Speed * dt * (_forward ? 1 : -1);
        Progress = Math.Clamp(Progress + delta, 0, 1);

        position = Spline.EvaluatePosition(Progress) + Offset;

        if (FollowRotation && Spline.PointCount >= 2)
        {
            var tgt = Spline.EvaluateTangent(Progress);
            var up = Spline.EvaluateUpVector(Progress);
            if (tgt.LengthSquared() > 0.0001f)
            {
                var targetRot = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, tgt, up));
                rotation = Quaternion.Slerp(rotation, targetRot, Math.Clamp(dt / SmoothRotation, 0, 1));
            }
        }

        if (Progress >= 1)
        {
            OnWaypointReached?.Invoke();
            if (PingPong) { _forward = false; Progress = 1; OnLoop?.Invoke(); }
            else if (Loop) { Progress = 0; OnLoop?.Invoke(); }
            else { AutoStart = false; OnPathComplete?.Invoke(); }
        }
        else if (Progress <= 0 && !_forward)
        {
            OnWaypointReached?.Invoke();
            if (PingPong) { _forward = true; OnLoop?.Invoke(); }
        }
    }

    public void Start() { AutoStart = true; _forward = true; }
    public void Stop() { AutoStart = false; }
    public void Reset(float progress = 0) { Progress = progress; _forward = true; }
    public void Reverse() { _forward = !_forward; }
}
