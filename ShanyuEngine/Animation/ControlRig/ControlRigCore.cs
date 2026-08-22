// ============================================================================
// ControlRigCore.cs
// 山雨游戏引擎 - Control Rig 核心数据模型
// 对标 UE5 Control Rig / RigVM 的数据层：元素、控制、曲线、变换值与数学工具。
// 这一层不依赖图形/编辑器，纯数据与算法，可独立单元测试。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation.ControlRig;

// ───────────────────────────────────────────────────────────────────────────
// 变换值（在引脚之间流动的核心数据类型）
// ───────────────────────────────────────────────────────────────────────────

public enum RigValueType
{
    Transform, Vector3, Float, Int, Bool, String, Quaternion, BoneIndex
}

/// <summary>引脚上流动的值。重量级实时求值场景里节点数有限，装箱开销可忽略。</summary>
public sealed class RigValue
{
    public RigValueType Type;
    public object? Data;

    public static RigValue Transform(RigTransform t) => new() { Type = RigValueType.Transform, Data = t };
    public static RigValue Vector3(Vector3 v) => new() { Type = RigValueType.Vector3, Data = v };
    public static RigValue Float(float f) => new() { Type = RigValueType.Float, Data = f };
    public static RigValue Int(int i) => new() { Type = RigValueType.Int, Data = i };
    public static RigValue Bool(bool b) => new() { Type = RigValueType.Bool, Data = b };
    public static RigValue String(string s) => new() { Type = RigValueType.String, Data = s };
    public static RigValue Quaternion(Quaternion q) => new() { Type = RigValueType.Quaternion, Data = q };
    public static RigValue BoneIndex(int i) => new() { Type = RigValueType.BoneIndex, Data = i };

    public RigTransform AsTransform()
    {
        if (Type == RigValueType.Transform) return (RigTransform)Data!;
        if (Type == RigValueType.Vector3) return new RigTransform((Vector3)Data!, Quaternion.Identity, Vector3.One);
        return RigTransform.Identity;
    }
    public Vector3 AsVector3()
    {
        if (Type == RigValueType.Vector3) return (Vector3)Data!;
        if (Type == RigValueType.Transform) return ((RigTransform)Data!).Translation;
        if (Type == RigValueType.Float) return new Vector3((float)Data!);
        return Vector3.Zero;
    }
    public float AsFloat()
    {
        if (Type == RigValueType.Float) return (float)Data!;
        if (Type == RigValueType.Int) return (int)Data!;
        if (Type == RigValueType.Bool) return (bool)Data! ? 1f : 0f;
        if (Type == RigValueType.Vector3) return ((Vector3)Data!).X;
        return 0f;
    }
    public int AsInt() => (int)Math.Round(AsFloat());
    public bool AsBool() => Type == RigValueType.Bool ? (bool)Data! : AsFloat() != 0f;
    public string AsString() => Data?.ToString() ?? "";
    public Quaternion AsQuaternion()
    {
        if (Type == RigValueType.Quaternion) return (Quaternion)Data!;
        if (Type == RigValueType.Transform) return ((RigTransform)Data!).Rotation;
        return Quaternion.Identity;
    }
    public int AsBoneIndex() => AsInt();
}

// ───────────────────────────────────────────────────────────────────────────
// 变换（位置/旋转/缩放），世界或局部坐标系
// ───────────────────────────────────────────────────────────────────────────

public struct RigTransform
{
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static readonly RigTransform Identity = new()
    {
        Translation = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    public RigTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Translation = translation; Rotation = rotation; Scale = scale;
    }

    public Matrix4x4 ToMatrix()
        => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);

    public static RigTransform FromMatrix(Matrix4x4 m)
    {
        Matrix4x4.Decompose(m, out var s, out var r, out var t);
        return new RigTransform(t, r, s);
    }

    public RigTransform Inverse()
    {
        if (!Matrix4x4.Invert(ToMatrix(), out var inv)) return Identity;
        return FromMatrix(inv);
    }
}

// ───────────────────────────────────────────────────────────────────────────
// Rig 元素：骨骼 / 控制 / 空对象 / 曲线
// ───────────────────────────────────────────────────────────────────────────

public enum RigElementType { Bone, Control, Null, Curve }

public class RigElement
{
    public string Name = "";
    public RigElementType Type = RigElementType.Null;
    /// <summary>在元素空间中的父级索引（控制/空对象可挂到骨骼或另一控制下）。</summary>
    public int ParentIndex = -1;
    public RigTransform InitialTransform = RigTransform.Identity;
}

/// <summary>可被外部驱动的可编辑控制点（对标 UE5 ControlRig Control）。</summary>
public sealed class ControlRigControl : RigElement
{
    /// <summary>运行时当前值（由动画/图内 SetControlValue / 外部 SetControl 更新）。</summary>
    public RigTransform Current = RigTransform.Identity;

    public ControlRigControl() { Type = RigElementType.Control; }

    public void Reset() => Current = InitialTransform;
}

/// <summary>标量曲线，按时间采样（对标 UE5 Rig Curve，用于程序化驱动控制）。</summary>
public sealed class RigCurve
{
    public string Name = "";
    public float[] Times = Array.Empty<float>();
    public float[] Values = Array.Empty<float>();

    public float Sample(float t)
    {
        if (Times.Length == 0) return 0f;
        if (t <= Times[0]) return Values[0];
        if (t >= Times[^1]) return Values[^1];
        for (int i = 0; i < Times.Length - 1; i++)
        {
            if (t >= Times[i] && t <= Times[i + 1])
            {
                float span = Times[i + 1] - Times[i];
                float a = span > 1e-8f ? (t - Times[i]) / span : 0f;
                return Values[i] + (Values[i + 1] - Values[i]) * a;
            }
        }
        return Values[^1];
    }
}

// ───────────────────────────────────────────────────────────────────────────
// 图节点数据（可序列化）
// ───────────────────────────────────────────────────────────────────────────

public sealed class RigPortSpec
{
    public string Name = "";
    public RigValueType Type = RigValueType.Transform;
    /// <summary>未连接时的默认值。</summary>
    public RigValue? Default;
    /// <summary>数据连接来源（上游节点 id 与输出端口名）。</summary>
    public string? ConnectedFromNode;
    public string? ConnectedFromPort;
}

public sealed class RigNodeData
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    /// <summary>节点类型标识（工厂据此实例化对应运行时节点）。</summary>
    public string TypeName = "";
    public string Title = "";
    /// <summary>节点配置参数（骨骼名、权重、轴等），以 RigValue 承载保证可序列化。</summary>
    public Dictionary<string, RigValue> Params = new();
    public List<RigPortSpec> Inputs = new();
    public List<RigPortSpec> Outputs = new();
    /// <summary>执行链后继（顺序节点用；纯数据节点为 null，由数据依赖按需拉取）。</summary>
    public string? ExecuteNext;

    public string ParamStr(string n, string d = "") => Params.TryGetValue(n, out var v) ? v.AsString() : d;
    public float ParamF(string n, float d = 0) => Params.TryGetValue(n, out var v) ? v.AsFloat() : d;
    public int ParamI(string n, int d = 0) => Params.TryGetValue(n, out var v) ? v.AsInt() : d;
    public bool ParamB(string n, bool d = false) => Params.TryGetValue(n, out var v) ? v.AsBool() : d;

    public RigPortSpec In(string name, RigValueType type, RigValue? def = null)
        => new() { Name = name, Type = type, Default = def };
    public RigPortSpec Out(string name, RigValueType type)
        => new() { Name = name, Type = type };
}

// ───────────────────────────────────────────────────────────────────────────
// Control Rig 资产（可持久化、可编辑）
// ───────────────────────────────────────────────────────────────────────────

public sealed class ControlRigAsset
{
    public string Name = "ControlRig";
    public List<RigElement> Elements = new();
    public List<RigCurve> Curves = new();
    public List<RigNodeData> Nodes = new();
    public string EntryNodeId = "";

    public ControlRigControl? FindControl(string name)
    {
        foreach (var e in Elements)
            if (e is ControlRigControl c && c.Name == name) return c;
        return null;
    }

    public RigCurve? FindCurve(string name)
    {
        foreach (var c in Curves) if (c.Name == name) return c;
        return null;
    }

    // ── 序列化 ──────────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new RigValueConverter() }
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static ControlRigAsset Load(string path)
    {
        var asset = JsonSerializer.Deserialize<ControlRigAsset>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException($"无法解析 Control Rig 资产: {path}");
        if (string.IsNullOrEmpty(asset.EntryNodeId) && asset.Nodes.Count > 0)
            asset.EntryNodeId = asset.Nodes[0].Id;
        return asset;
    }
}

// ───────────────────────────────────────────────────────────────────────────
// RigValue 自定义 JSON 转换器（支持值类型往返）
// ───────────────────────────────────────────────────────────────────────────

internal sealed class RigValueConverter : JsonConverter<RigValue>
{
    public override RigValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.GetProperty("t").GetString();
        var v = new RigValue();
        switch (type)
        {
            case "Transform":
                v.Type = RigValueType.Transform;
                v.Data = JsonSerializer.Deserialize<RigTransform>(root.GetProperty("v").GetRawText(), options);
                break;
            case "Vector3":
                v.Type = RigValueType.Vector3;
                v.Data = JsonSerializer.Deserialize<Vector3>(root.GetProperty("v").GetRawText(), options);
                break;
            case "Quaternion":
                v.Type = RigValueType.Quaternion;
                v.Data = JsonSerializer.Deserialize<Quaternion>(root.GetProperty("v").GetRawText(), options);
                break;
            case "Float": v.Type = RigValueType.Float; v.Data = root.GetProperty("v").GetSingle(); break;
            case "Int": v.Type = RigValueType.Int; v.Data = root.GetProperty("v").GetInt32(); break;
            case "Bool": v.Type = RigValueType.Bool; v.Data = root.GetProperty("v").GetBoolean(); break;
            case "String": v.Type = RigValueType.String; v.Data = root.GetProperty("v").GetString(); break;
            default: v.Type = RigValueType.Float; v.Data = 0f; break;
        }
        return v;
    }

    public override void Write(Utf8JsonWriter writer, RigValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("t", value.Type.ToString());
        writer.WritePropertyName("v");
        switch (value.Type)
        {
            case RigValueType.Transform: JsonSerializer.Serialize(writer, value.AsTransform(), options); break;
            case RigValueType.Vector3: JsonSerializer.Serialize(writer, value.AsVector3(), options); break;
            case RigValueType.Quaternion: JsonSerializer.Serialize(writer, value.AsQuaternion(), options); break;
            default: JsonSerializer.Serialize(writer, value.Data, options); break;
        }
        writer.WriteEndObject();
    }
}

// ───────────────────────────────────────────────────────────────────────────
// 骨骼/姿态数学（自包含，避免对内部辅助类的耦合）
// ───────────────────────────────────────────────────────────────────────────

internal static class RigMath
{
    public static Matrix4x4 LocalMatrix(Skeleton sk, SkeletonPose pose, int idx)
    {
        var b = sk.Bones[idx];
        var scale = b.LocalScale * pose.GetScaleOffset(idx);
        var rot = pose.GetRotation(idx);
        var trans = b.LocalPosition + pose.GetTranslationOffset(idx);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(trans);
    }

    public static Matrix4x4 WorldMatrix(Skeleton sk, SkeletonPose pose, int idx)
    {
        var m = LocalMatrix(sk, pose, idx);
        var p = sk.Bones[idx].ParentIndex;
        while (p >= 0) { m *= LocalMatrix(sk, pose, p); p = sk.Bones[p].ParentIndex; }
        return m;
    }

    public static RigTransform WorldTransform(Skeleton sk, SkeletonPose pose, int idx)
        => RigTransform.FromMatrix(WorldMatrix(sk, pose, idx));

    /// <summary>将一个骨骼的世界变换设为指定值（推导局部变换写回姿态）。</summary>
    public static void SetWorldTransform(Skeleton sk, SkeletonPose pose, int idx, RigTransform world)
    {
        int p = sk.Bones[idx].ParentIndex;
        Matrix4x4 parentWorld = p >= 0 ? WorldMatrix(sk, pose, p) : Matrix4x4.Identity;
        if (!Matrix4x4.Invert(parentWorld, out var invParent)) invParent = Matrix4x4.Identity;
        Matrix4x4 desiredLocal = invParent * world.ToMatrix();
        var local = RigTransform.FromMatrix(desiredLocal);

        var b = sk.Bones[idx];
        pose.SetRotation(idx, local.Rotation);
        pose.SetTranslationOffset(idx, local.Translation - b.LocalPosition);
        Vector3 baseScale = b.LocalScale;
        var scaleOff = new Vector3(
            Math.Abs(baseScale.X) < 1e-6f ? local.Scale.X : local.Scale.X / baseScale.X,
            Math.Abs(baseScale.Y) < 1e-6f ? local.Scale.Y : local.Scale.Y / baseScale.Y,
            Math.Abs(baseScale.Z) < 1e-6f ? local.Scale.Z : local.Scale.Z / baseScale.Z);
        pose.SetScaleOffset(idx, scaleOff);
    }

    /// <summary>仅设置骨骼世界旋转，保持世界位置与缩放不变。</summary>
    public static void SetWorldRotation(Skeleton sk, SkeletonPose pose, int idx, Quaternion worldRot)
    {
        var wt = WorldTransform(sk, pose, idx);
        SetWorldTransform(sk, pose, idx, new RigTransform(wt.Translation, worldRot, wt.Scale));
    }

    /// <summary>最短弧旋转：将方向 from 对齐到 to。</summary>
    public static Quaternion RotationFromTo(Vector3 from, Vector3 to)
    {
        var f = from.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(from);
        var t = to.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(to);
        var dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
        if (dot >= 0.999999f) return Quaternion.Identity;
        if (dot <= -0.999999f)
        {
            var axis = MathF.Abs(f.X) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(f, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }
        var c = Vector3.Cross(f, t);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + dot));
    }
}
