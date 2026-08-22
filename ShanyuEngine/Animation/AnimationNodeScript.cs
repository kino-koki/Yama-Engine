using System; using System.Collections.Generic; using System.Numerics; namespace ShanyuEngine.Animation;

public sealed class AnimationNodeScript
{
    public class AnimNode
    {
        public string Name = "", Type = "";
        public List<AnimNode> Inputs = new();
        public List<(float pos, string name)> BlendPositions = new();
        public float Weight = 1f, Speed = 1f;
        public bool Loop = true, Mirror;
        public AnimationClipCore? Clip;
        public Func<AnimNode, float, RigPose, RigPose>? Evaluate;
    }

    private readonly Dictionary<string, AnimNode> _nodes = new();
    public AnimNode? OutputNode { get; set; }

    public AnimNode AddNode(string name, string type)
    {
        var n = new AnimNode { Name = name, Type = type }; _nodes[name] = n; return n;
    }

    public void Connect(string from, string to) { if (_nodes.TryGetValue(from, out var fn) && _nodes.TryGetValue(to, out var tn)) tn.Inputs.Add(fn); }

    public void Evaluate(float time, RigPose pose)
    {
        if (OutputNode == null) return;
        EvaluateNode(OutputNode, time, pose);
    }

    private void EvaluateNode(AnimNode node, float time, RigPose pose)
    {
        if (node.Evaluate != null) { node.Evaluate(node, time, pose); return; }
        switch (node.Type)
        {
            case "Clip":
                node.Clip?.SamplePose(pose, time * node.Speed, node.Loop);
                break;
            case "Blend":
                if (node.Inputs.Count >= 2)
                {
                    var p1 = new RigPose();
                    EvaluateNode(node.Inputs[0], time, p1);
                    EvaluateNode(node.Inputs[1], time, pose);
                    pose.BlendTo(p1, node.Weight);
                }
                break;
            case "BlendSpace1D":
                if (node.BlendPositions.Count > 0 && node.Inputs.Count > 0)
                {
                    float pos = node.Weight; int idx = 0;
                    for (int i = 0; i < node.BlendPositions.Count - 1 && node.BlendPositions[i+1].pos < pos; i++) idx = i + 1;
                    if (idx < node.Inputs.Count) EvaluateNode(node.Inputs[idx], time, pose);
                }
                break;
            case "Invert":
                if (node.Inputs.Count > 0) { for (int i = 0; i < pose.BoneCount; i++) { pose.Positions[i] = -pose.Positions[i]; } EvaluateNode(node.Inputs[0], time, pose); }
                break;
            case "Additive":
                if (node.Inputs.Count >= 2)
                {
                    var basePose = new RigPose();
                    EvaluateNode(node.Inputs[0], time, basePose);
                    EvaluateNode(node.Inputs[1], time, pose);
                    pose.ApplyAdditive(basePose);
                }
                break;
        }
    }
}

public sealed class AnimationInstancing
{
    public class InstanceData
    {
        public Matrix4x4[] BoneMatrices = Array.Empty<Matrix4x4>();
        public float Time;
        public bool Active = true;
        public Vector3 Position, Scale = Vector3.One;
        public Quaternion Rotation = Quaternion.Identity;
    }

    private readonly List<InstanceData> _instances = new();
    private readonly AnimationClipCore? _clip;

    public AnimationInstancing(AnimationClipCore? clip) { _clip = clip; }

    public InstanceData AddInstance(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var inst = new InstanceData { Position = position, Rotation = rotation, Scale = scale };
        _instances.Add(inst); return inst;
    }

    public void RemoveInstance(InstanceData inst) => _instances.Remove(inst);
    public int InstanceCount => _instances.Count;
    public IReadOnlyList<InstanceData> Instances => _instances;

    public void Update(float dt, RigPose bindPose, Matrix4x4[] bindInv)
    {
        foreach (var inst in _instances)
        {
            if (!inst.Active) continue;
            inst.Time += dt;
            var pose = new RigPose();
            _clip?.SamplePose(pose, inst.Time, true);
            var skin = pose.BuildSkinMatrices(bindInv);
            for (int i = 0; i < skin.Length; i++)
                inst.BoneMatrices[i] = skin[i] * Matrix4x4.CreateScale(inst.Scale) * Matrix4x4.CreateFromQuaternion(inst.Rotation) * Matrix4x4.CreateTranslation(inst.Position);
        }
    }
}

public sealed class AnimationLODSystem
{
    public struct LODLevel
    {
        public float Distance; public float SampleRate; public float BoneWeight;
        public bool DisableIK, DisablePhysics, DisableBlendShapes;
        public int MaxBones;
    }

    private readonly List<LODLevel> _levels = new();
    private int _currentLOD;

    public AnimationLODSystem()
    {
        _levels.Add(new LODLevel { Distance = 0, SampleRate = 60, BoneWeight = 1, MaxBones = 100, DisableIK = false, DisablePhysics = false, DisableBlendShapes = false });
        _levels.Add(new LODLevel { Distance = 20, SampleRate = 30, BoneWeight = 0.5f, MaxBones = 50, DisableIK = true, DisablePhysics = false, DisableBlendShapes = false });
        _levels.Add(new LODLevel { Distance = 50, SampleRate = 15, BoneWeight = 0.25f, MaxBones = 25, DisableIK = true, DisablePhysics = true, DisableBlendShapes = true });
        _levels.Add(new LODLevel { Distance = 100, SampleRate = 8, BoneWeight = 0.1f, MaxBones = 10, DisableIK = true, DisablePhysics = true, DisableBlendShapes = true });
    }

    public void AddLevel(LODLevel l) => _levels.Add(l);
    public void Clear() { _levels.Clear(); _currentLOD = 0; }
    public LODLevel GetLevel(int i) => _levels[i];
    public int CurrentLOD => _currentLOD;
    public int LevelCount => _levels.Count;

    public int SelectLOD(Vector3 cameraPos, Vector3 objectPos)
    {
        float dist = Vector3.Distance(cameraPos, objectPos);
        for (int i = _levels.Count - 1; i >= 0; i--)
        {
            if (dist >= _levels[i].Distance) { _currentLOD = i; return i; }
        }
        _currentLOD = 0; return 0;
    }

    public LODLevel GetCurrentLOD() => _levels[_currentLOD];

    public void ApplyLOD(AnimationClipCore clip, int lod)
    {
        if (lod >= 0 && lod < _levels.Count)
        {
            var l = _levels[lod];
            // Reduce keyframe sampling
            clip.FrameRate = l.SampleRate;
        }
    }
}


public sealed class RetargetingSystem
{
    public struct BoneMapping
    {
        public string SourceBone, TargetBone;
        public float RotationAdjust, PositionAdjust;
        public bool Mirror;
    }

    private readonly List<BoneMapping> _mappings = new();

    public void AddMapping(string src, string tgt, bool mirror = false, float rotAdjust = 0) =>
        _mappings.Add(new BoneMapping { SourceBone = src, TargetBone = tgt, Mirror = mirror, RotationAdjust = rotAdjust });

    public RigPose Retarget(RigPose sourcePose, RigPose targetPose, Vector3 sourceVel, Vector3 targetVel)
    {
        var result = new RigPose();
        for (int i = 0; i < _mappings.Count && i < Math.Min(sourcePose.BoneCount, targetPose.BoneCount); i++)
        {
            int src = i % sourcePose.BoneCount;
            int tgt = i % targetPose.BoneCount;
            result.Positions[tgt] = sourcePose.Positions[src] * (targetVel.Length() / MathF.Max(sourceVel.Length(), 0.001f));
            result.Rotations[tgt] = sourcePose.Rotations[src];
        }
        return result;
    }
}
