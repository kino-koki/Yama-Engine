using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.NPC.AiNpc;

/// <summary>
/// Kimodo 动作生成请求。字段与 nv-tlabs/kimodo 的 CLI 语义一致：
/// text prompt + 运动学约束（root2d / fullbody / end-effector 等）。
/// </summary>
public sealed class KimodoGenerateRequest
{
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("duration_seconds")] public float DurationSeconds { get; set; } = 3f;
    [JsonPropertyName("fps")] public float Fps { get; set; } = 30f;
    [JsonPropertyName("model")] public string Model { get; set; } = "kimodo-soma-rp";
    [JsonPropertyName("skeleton")] public string Skeleton { get; set; } = "somaskel77";
    [JsonPropertyName("seed")] public int Seed { get; set; } = -1;
    [JsonPropertyName("num_samples")] public int NumSamples { get; set; } = 1;
    [JsonPropertyName("constraints")] public List<KimodoConstraint> Constraints { get; set; } = new();
    /// <summary>调用方透传的上下文（NPC 名称/情绪/位置），sidecar 可用于日志或提示词增强。</summary>
    [JsonPropertyName("metadata")] public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// 单条 Kimodo 约束。坐标统一为 Kimodo 规范：Y 轴向上、XZ 为地面、单位米，
/// 且相对第 0 帧的规范原点。
/// </summary>
public sealed class KimodoConstraint
{
    [JsonPropertyName("type")] public string Type { get; set; } = "root2d";
    [JsonPropertyName("frame_indices")] public int[] FrameIndices { get; set; } = Array.Empty<int>();
    /// <summary>root2d：平滑根位置 [x, z]（米，相对规范原点）。</summary>
    [JsonPropertyName("smooth_root_2d")] public float[][]? SmoothRoot2D { get; set; }
    /// <summary>root2d：朝向 [cos, sin]。</summary>
    [JsonPropertyName("global_root_heading")] public float[][]? GlobalRootHeading { get; set; }
    /// <summary>fullbody / end-effector：根位置 [x, y, z]（米，Y 为髋部离地高度）。</summary>
    [JsonPropertyName("root_positions")] public float[][]? RootPositions { get; set; }
    /// <summary>fullbody / end-effector：局部旋转 axis-angle [T, J, 3]（弧度）。</summary>
    [JsonPropertyName("local_joints_rot")] public float[][][]? LocalJointsRot { get; set; }
    /// <summary>end-effector：受约束的关节名。</summary>
    [JsonPropertyName("joint_names")] public string[]? JointNames { get; set; }
}

public sealed class KimodoHealthResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("skeleton")] public string Skeleton { get; set; } = "";
    [JsonPropertyName("gpu")] public string? Gpu { get; set; }
}

/// <summary>
/// Kimodo sidecar 返回的已解码动作。为避免在 C# 侧解析 NPZ，sidecar 负责
/// 把 posed_joints / global_rot_mats / local_rot_mats 转成 JSON：
/// 局部旋转用四元数，顺序固定 [x, y, z, w]。
/// </summary>
public sealed class KimodoMotionResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("fps")] public float Fps { get; set; } = 30f;
    [JsonPropertyName("duration_seconds")] public float DurationSeconds { get; set; }
    [JsonPropertyName("skeleton")] public KimodoSkeletonDef Skeleton { get; set; } = new();
    /// <summary>可选：Kimodo 骨骼名 → 引擎骨骼名映射。</summary>
    [JsonPropertyName("bone_map")] public Dictionary<string, string>? BoneMap { get; set; }
    /// <summary>[T, J, 4] 局部旋转四元数（相对 Kimodo 静息姿态）。</summary>
    [JsonPropertyName("rotations")] public float[][][] Rotations { get; set; } = Array.Empty<float[][]>();
    /// <summary>[T, 3] 根关节世界位置（米，Y-up）。</summary>
    [JsonPropertyName("root_positions")] public float[][] RootPositions { get; set; } = Array.Empty<float[]>();
    /// <summary>[T, 4] 足部接触标签（左跟/左掌/右跟/右掌）。</summary>
    [JsonPropertyName("foot_contacts")] public float[][]? FootContacts { get; set; }
    [JsonPropertyName("warning")] public string? Warning { get; set; }

    [JsonIgnore] public int FrameCount => Rotations.Length;
    public bool IsUsable => FrameCount > 0 && Skeleton.Joints.Count > 0;
}

public sealed class KimodoSkeletonDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "SOMA";
    [JsonPropertyName("joints")] public List<KimodoJointDef> Joints { get; set; } = new();
}

public sealed class KimodoJointDef
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("parent_index")] public int ParentIndex { get; set; } = -1;
    /// <summary>静息姿态下的局部偏移（米）。</summary>
    [JsonPropertyName("rest_position")] public float[] RestPosition { get; set; } = new float[3];
    [JsonPropertyName("rest_rotation")] public float[]? RestRotation { get; set; }
}

internal static class KimodoJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
