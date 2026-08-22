using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Physics;

public enum ShapeType { Sphere, Box, Capsule, Cylinder, Cone, Mesh, ConvexHull, Plane, Compound }
public enum BodyType { Static, Dynamic, Kinematic }
public enum CollisionChannel { Default, Static, Dynamic, Character, Pawn, PhysicsBody, Vehicle, Destructible, UI, Camera, Visibility }
public enum CollisionResponse { Ignore, Overlap, Block }
public enum ConstraintTypeKind { Point, Hinge, Slider, Spring, Fixed, Ball, ConeTwist, Gear, Pulley }


public struct RigidBodyConfig
{
    public BodyType Type; public float Mass, LinearDamping, AngularDamping;
    public float Friction, Restitution; public bool GravityEnabled, CCD;
    public RigidBodyConfig() { Type = BodyType.Dynamic; Mass = 1f; LinearDamping = 0.05f; AngularDamping = 0.05f; Friction = 0.5f; Restitution = 0.3f; GravityEnabled = true; CCD = false; }
}



public struct HitResult
{
    public bool Hit; public float Distance; public Vector3 Point, Normal;
    public int BodyId, ShapeIndex; public CollisionChannel Channel;
}

public struct ConstraintConfig
{
    public ConstraintTypeKind Type; public int BodyA, BodyB;
    public Vector3 PivotA, PivotB, AxisA, AxisB;
    public float MinAngle, MaxAngle, MinDistance, MaxDistance;
    public float Stiffness, Damping, RestLength;
    public bool EnableLimit, EnableMotor; public float MotorTarget, MotorMaxForce;
}

public sealed class DynamicBVH
{
    private struct Node { public AABB Bounds; public int Left, Right, BodyId; public Node() { Left = -1; Right = -1; BodyId = -1; } public bool IsLeaf => Left < 0 && Right < 0; }
    private readonly List<Node> _nodes = new(); private int _root = -1;

    public int Insert(int bodyId, AABB bounds)
    {
        _nodes.Add(new Node()); int idx = _nodes.Count - 1; _nodes[idx] = new Node { Bounds = bounds, BodyId = bodyId };
        if (_root < 0) { _root = idx; return idx; }
        int sibling = _root, best = _root; float bestCost = float.MaxValue;
        var q = new Stack<int>(); q.Push(_root);
        while (q.Count > 0)
        {
            int i = q.Pop(); if (i < 0) continue;
            float cost = AABB.Merge(bounds, _nodes[i].Bounds).SurfaceArea + _nodes[i].Bounds.SurfaceArea;
            if (cost < bestCost) { bestCost = cost; sibling = i; }
            if (!_nodes[i].IsLeaf) { float inherit = AABB.Merge(bounds, _nodes[i].Bounds).SurfaceArea - _nodes[i].Bounds.SurfaceArea; if (inherit < bestCost) { q.Push(_nodes[i].Left); q.Push(_nodes[i].Right); } }
        }
        int parent = _nodes.Count; _nodes.Add(new Node());
        int gp = GetParent(sibling);
        _nodes[parent].Bounds = AABB.Merge(bounds, _nodes[sibling].Bounds);
        _nodes[parent].Left = sibling; _nodes[parent].Right = idx;
        if (gp < 0) _root = parent;
        else if (_nodes[gp].Left == sibling) _nodes[gp].Left = parent;
        else _nodes[gp].Right = parent;
        Sync(parent); return idx;
    }

    public void Remove(int id)
    {
        int p = GetParent(id); if (p < 0) { _root = -1; return; }
        int gp = GetParent(p); int sib = _nodes[p].Left == id ? _nodes[p].Right : _nodes[p].Left;
        if (gp < 0) { _root = sib; return; }
        if (_nodes[gp].Left == p) _nodes[gp].Left = sib; else _nodes[gp].Right = sib;
        Sync(gp);
    }

    public void Update(int id, AABB b) { if (id >= 0 && id < _nodes.Count) { _nodes[id] = _nodes[id] with { Bounds = b }; int p = GetParent(id); if (p >= 0) Sync(p); } }

    public void Query(Frustum f, List<int> r)
    {
        if (_root < 0) return; var s = new Stack<int>(); s.Push(_root);
        while (s.Count > 0) { int i = s.Pop(); if (i < 0 || f.Intersects(_nodes[i].Bounds) == IntersectionResult.Outside) continue; if (_nodes[i].IsLeaf) { r.Add(_nodes[i].BodyId); } else { s.Push(_nodes[i].Left); s.Push(_nodes[i].Right); } }
    }

    public void QueryRay(Ray ray, List<int> r, float maxDist = float.MaxValue)
    {
        if (_root < 0) return; var s = new Stack<int>(); s.Push(_root);
        while (s.Count > 0) { int i = s.Pop(); if (i < 0) continue; if (!RayIntersectsAABB(ray, _nodes[i].Bounds, out float t) || t > maxDist) continue; if (_nodes[i].IsLeaf) { r.Add(_nodes[i].BodyId); } else { s.Push(_nodes[i].Left); s.Push(_nodes[i].Right); } }
    }

    public void QueryOverlap(AABB b, List<int> r)
    {
        if (_root < 0) return; var s = new Stack<int>(); s.Push(_root);
        while (s.Count > 0) { int i = s.Pop(); if (i < 0 || !b.Intersects(_nodes[i].Bounds)) continue; if (_nodes[i].IsLeaf) { r.Add(_nodes[i].BodyId); } else { s.Push(_nodes[i].Left); s.Push(_nodes[i].Right); } }
    }

    private int GetParent(int idx) { for (int i = 0; i < _nodes.Count; i++) if (!_nodes[i].IsLeaf && (_nodes[i].Left == idx || _nodes[i].Right == idx)) return i; return -1; }
    private void Sync(int idx) { while (idx >= 0) { if (_nodes[idx].Left >= 0 && _nodes[idx].Right >= 0) { var n = _nodes[idx]; n.Bounds = AABB.Merge(_nodes[n.Left].Bounds, _nodes[n.Right].Bounds); _nodes[idx] = n; } idx = GetParent(idx); } }
    private static bool RayIntersectsAABB(Ray r, AABB b, out float t) { t = 0; float tmin = 0, tmax = float.MaxValue; for (int i = 0; i < 3; i++) { float o = i==0?r.Origin.X:i==1?r.Origin.Y:r.Origin.Z, d = i==0?r.Direction.X:i==1?r.Direction.Y:r.Direction.Z, bmin = i==0?b.Min.X:i==1?b.Min.Y:b.Min.Z, bmax = i==0?b.Max.X:i==1?b.Max.Y:b.Max.Z; if (MathF.Abs(d) < 1e-8f) { if (o < bmin || o > bmax) return false; } else { float inv = 1f/d, t0 = (bmin-o)*inv, t1 = (bmax-o)*inv; if (t0 > t1) { var tmp = t0; t0 = t1; t1 = tmp; } tmin = MathF.Max(tmin, t0); tmax = MathF.Min(tmax, t1); if (tmin > tmax) return false; } } t = tmin; return t >= 0; }
}

public static class CollisionUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SphereSphere(Vector3 ca, float ra, Vector3 cb, float rb, out ContactPoint cp)
    {
        cp = default; var d = ca - cb; float dsq = d.LengthSquared(); float rs = ra + rb;
        if (dsq >= rs*rs) return false; float dist = MathF.Sqrt(dsq);
        cp.Normal = dist > 0 ? d/dist : Vector3.UnitY; cp.PenetrationDepth = rs - dist; cp.Position = ca + cp.Normal*(ra - cp.PenetrationDepth*0.5f); return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AABB_AABB_Test(AABB a, AABB b, out ContactPoint cp)
    {
        cp = default; if (!a.Intersects(b)) return false;
        var ov = new Vector3(MathF.Min(a.Max.X,b.Max.X)-MathF.Max(a.Min.X,b.Min.X), MathF.Min(a.Max.Y,b.Max.Y)-MathF.Max(a.Min.Y,b.Min.Y), MathF.Min(a.Max.Z,b.Max.Z)-MathF.Max(a.Min.Z,b.Min.Z));
        float m = MathF.Min(ov.X, MathF.Min(ov.Y, ov.Z)); cp.PenetrationDepth = m;
        cp.Normal = ov.X <= m ? new(a.Center.X < b.Center.X ? -1 : 1, 0, 0) : ov.Y <= m ? new(0, a.Center.Y < b.Center.Y ? -1 : 1, 0) : new(0, 0, a.Center.Z < b.Center.Z ? -1 : 1);
        cp.Position = (a.Center + b.Center)*0.5f; return true;
    }

    public static bool RayTriangle(Ray r, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0; var e1 = v1-v0; var e2 = v2-v0; var p = Vector3.Cross(r.Direction, e2);
        float det = Vector3.Dot(e1, p); if (MathF.Abs(det) < 1e-8f) return false; float inv = 1f/det;
        var s = r.Origin - v0; float u = Vector3.Dot(s, p)*inv; if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1); float v = Vector3.Dot(r.Direction, q)*inv; if (v < 0 || u+v > 1) return false;
        t = Vector3.Dot(e2, q)*inv; return t >= 0;
    }

    public static bool RayMesh(Ray r, Vector3[] verts, int[] indices, out float t, out int tri)
    {
        t = float.MaxValue; tri = -1;
        for (int i = 0; i < indices.Length; i += 3)
            if (RayTriangle(r, verts[indices[i]], verts[indices[i+1]], verts[indices[i+2]], out float ht) && ht < t) { t = ht; tri = i/3; }
        return tri >= 0;
    }

    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1*d4 - d3*d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5*d2 - d1*d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3*d6 - d5*d4;
        if (va <= 0 && (d4-d3) >= 0 && (d5-d6) >= 0) return b + (c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
        float denom = 1f/(va+vb+vc); return a*va*denom + b*vb*denom + c*vc*denom;
    }
}


public sealed class PhysicsWorldSystem : IDisposable
{
    private readonly DynamicBVH _bvh = new();
    private readonly List<RigidBodyConfig> _configs = new();
    private readonly List<CollisionShape> _shapes = new();
    private readonly List<CollisionFilter> _filters = new();
    private readonly List<Vector3> _positions = new(), _velocities = new(), _angularVel = new();
    private readonly List<Quaternion> _rotations = new();
    private readonly List<int> _proxyIds = new();
    private readonly ConstraintSolver _constraints = new();
    private readonly List<ContactPoint> _contacts = new();
    private Vector3 _gravity = new(0, -9.81f, 0);
    private bool _disposed;

    public Vector3 Gravity { get => _gravity; set => _gravity = value; }
    public int BodyCount => _configs.Count;
    public IReadOnlyList<ContactPoint> Contacts => _contacts;
    public ConstraintSolver Constraints => _constraints;

    public int CreateBody(RigidBodyConfig cfg, CollisionShape shape, CollisionFilter? filter = null)
    {
        int i = _configs.Count;
        _configs.Add(cfg); _shapes.Add(shape); _filters.Add(filter ?? new CollisionFilter(CollisionChannel.Default));
        _positions.Add(Vector3.Zero); _rotations.Add(Quaternion.Identity); _velocities.Add(Vector3.Zero); _angularVel.Add(Vector3.Zero);
        _proxyIds.Add(_bvh.Insert(i, shape.ComputeBounds(Vector3.Zero, Quaternion.Identity)));
        return i;
    }

    public void RemoveBody(int i) { if (i < 0 || i >= _configs.Count) return; _bvh.Remove(_proxyIds[i]); }
    public void SetTransform(int i, Vector3 pos, Quaternion rot) { if (i < 0 || i >= _configs.Count) return; _positions[i]=pos; _rotations[i]=rot; _bvh.Update(_proxyIds[i], shape.ComputeBounds(pos,rot)); }

    public void Step(float dt)
    {
        float subDt = MathF.Min(dt, 0.016f);
        for (int sub = 0; sub < 2; sub++)
        {
            for (int i = 0; i < _configs.Count; i++)
            {
                if (_configs[i].Type != BodyType.Dynamic) continue;
                if (_configs[i].GravityEnabled) _velocities[i] += _gravity*subDt;
                _velocities[i] *= (1f-_configs[i].LinearDamping*subDt);
                _angularVel[i] *= (1f-_configs[i].AngularDamping*subDt);
            }
            _contacts.Clear();
            var b = new AABB[_configs.Count];
            for (int i = 0; i < _configs.Count; i++) b[i] = _shapes[i].ComputeBounds(_positions[i], _rotations[i]);
            for (int i = 0; i < _configs.Count; i++)
                for (int j = i+1; j < _configs.Count; j++)
                {
                    if (_configs[i].Type == BodyType.Static && _configs[j].Type == BodyType.Static) continue;
                    if (!_filters[i].Passes(_filters[j]) || !b[i].Intersects(b[j])) continue;
                    bool hit = false; ContactPoint cp = default;
                    var sa = _shapes[i]; var sb = _shapes[j];
                    Vector3 pa = _positions[i]+Vector3.Transform(sa.Offset,_rotations[i]);
                    Vector3 pb = _positions[j]+Vector3.Transform(sb.Offset,_rotations[j]);
                    if (sa.Type==ShapeType.Sphere && sb.Type==ShapeType.Sphere) hit = CollisionUtils.SphereSphere(pa,sa.Radius,pb,sb.Radius,out cp);
                    else if (sa.Type==ShapeType.Box && sb.Type==ShapeType.Box) hit = CollisionUtils.AABB_AABB_Test(new AABB(pa-sa.Size*0.5f,pa+sa.Size*0.5f),new AABB(pb-sb.Size*0.5f,pb+sb.Size*0.5f),out cp);
                    if (hit)
                    {
                        cp.BodyAIndex=i; cp.BodyBIndex=j; _contacts.Add(cp);
                        var n = cp.Normal;
                        if (_configs[i].Type==BodyType.Dynamic && _configs[j].Type==BodyType.Dynamic) { _positions[i]+=n*cp.PenetrationDepth*0.5f; _positions[j]-=n*cp.PenetrationDepth*0.5f; }
                        else if (_configs[i].Type==BodyType.Dynamic) _positions[i]+=n*cp.PenetrationDepth;
                        else if (_configs[j].Type==BodyType.Dynamic) _positions[j]-=n*cp.PenetrationDepth;
                    }
                }
            _constraints.Solve(CollectionsMarshal.AsSpan(_positions), CollectionsMarshal.AsSpan(_rotations), subDt);
            for (int i = 0; i < _configs.Count; i++)
            {
                if (_configs[i].Type != BodyType.Dynamic) continue;
                _positions[i] += _velocities[i]*subDt;
                if (_angularVel[i].LengthSquared() > 1e-10f)
                {
                    float a = _angularVel[i].Length()*subDt;
                    _rotations[i] = Quaternion.CreateFromAxisAngle(Vector3.Normalize(_angularVel[i]),a)*_rotations[i];
                    _rotations[i] = Quaternion.Normalize(_rotations[i]);
                }
                _bvh.Update(_proxyIds[i], _shapes[i].ComputeBounds(_positions[i], _rotations[i]));
            }
        }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _configs.Clear(); _positions.Clear(); _rotations.Clear(); _velocities.Clear(); _angularVel.Clear(); _contacts.Clear(); }
}
