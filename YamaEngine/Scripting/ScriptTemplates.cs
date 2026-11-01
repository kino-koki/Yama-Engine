// ==================================================================================================
//  ScriptTemplates.cs — yama引擎预置脚本模板
//
//  命名空间: YamaEngine.Scripting
//
//  功能:
//    提供预置的 C# 脚本模板, 每个模板是完整的 C# 源码字符串, 可直接由 ScriptCompiler 编译。
//    模板包括:
//      1. PlayerController  — WASD 移动 + 空格跳跃的角色控制器
//      2. RotatingObject    — 绕指定轴持续旋转的物体
//      3. TriggerZone       — 进入触发区域时播放音效的触发器
//      4. FollowTarget      — 跟随目标实体的跟随脚本
//
//  使用方式:
//    string code = ScriptTemplates.GetTemplate("PlayerController");
//    var compiler = new ScriptCompiler();
//    var assembly = compiler.CompileSource(code, "PlayerController");
// ==================================================================================================

using System.Collections.Generic;

namespace YamaEngine.Scripting
{
    /// <summary>
    /// 预置脚本模板集合。每个模板是完整的 C# 源码字符串, 可直接编译。
    /// </summary>
    public static class ScriptTemplates
    {
        // ─────────────────────────────────────────────────────────────────────────
        //  模板字典
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>所有预置模板的名称 → 源码映射。</summary>
        private static readonly Dictionary<string, string> s_templates = new()
        {
            ["PlayerController"] = PlayerControllerSource,
            ["RotatingObject"]   = RotatingObjectSource,
            ["TriggerZone"]      = TriggerZoneSource,
            ["FollowTarget"]     = FollowTargetSource,
        };

        // ─────────────────────────────────────────────────────────────────────────
        //  API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>获取所有可用模板的名称。</summary>
        public static IEnumerable<string> GetTemplateNames() => s_templates.Keys;

        /// <summary>
        /// 按名称获取模板源码。
        /// </summary>
        /// <param name="name">模板名称。</param>
        /// <returns>模板源码, 未找到返回 null。</returns>
        public static string? GetTemplate(string name)
        {
            return s_templates.TryGetValue(name, out var source) ? source : null;
        }

        /// <summary>
        /// 获取所有模板源码的只读字典。
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetAllTemplates() => s_templates;

        // ════════════════════════════════════════════════════════════════════════
        //  1. PlayerController — WASD 角色控制器
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 玩家角色控制器模板。
        /// 功能: WASD 移动, 空格跳跃, 鼠标旋转, Shift 加速。
        /// 使用 ScriptAPI.Input 读取输入, ScriptAPI.Transform 修改位置。
        /// </summary>
        public const string PlayerControllerSource = @"using System;
using System.Numerics;
using YamaEngine.Scripting;

/// <summary>玩家角色控制器。WASD 移动, 空格跳跃, Shift 加速。</summary>
";

        // ════════════════════════════════════════════════════════════════════════
        //  2. RotatingObject — 旋转物体
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 持续旋转物体模板。
        /// 功能: 绕指定轴以指定速度持续旋转。
        /// </summary>
        public const string RotatingObjectSource = @"using System;
using System.Numerics;
using YamaEngine.Scripting;

/// <summary>持续旋转的物体。可配置旋转轴和速度。</summary>
public class RotatingObject
{
    // ── 公共字段 ──────────────────────────────────────────────────────────────

    /// <summary>旋转轴 (会被自动归一化)。</summary>
    public Vector3 Axis = new Vector3(0, 1, 0);

    /// <summary>旋转速度 (度/秒)。</summary>
    public float Speed = 45.0f;

    /// <summary>是否在 OnStart 时随机化旋转轴。</summary>
    public bool RandomAxisOnStart = false;

    // ── 内部状态 ──────────────────────────────────────────────────────────────

    /// <summary>绑定的实体 ID。</summary>
    public int EntityId;

    // ── 生命周期 ──────────────────────────────────────────────────────────────

    public void OnStart()
    {
        ScriptAPI.Debug.Log(""RotatingObject: OnStart"");

        if (RandomAxisOnStart)
        {
            var random = new Random();
            Axis = Vector3.Normalize(new Vector3(
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1),
                (float)(random.NextDouble() * 2 - 1)));
        }

        // 确保旋转轴已归一化
        if (Axis.LengthSquared() > 0.0001f)
            Axis = Vector3.Normalize(Axis);
        else
            Axis = Vector3.UnitY;
    }

    public void OnUpdate(float dt)
    {
        // 计算旋转增量
        float angleRad = Speed * dt * (MathF.PI / 180f);
        var deltaRot = Quaternion.CreateFromAxisAngle(Axis, angleRad);

        // 应用旋转 (当前旋转 * 增量)
        var currentRot = ScriptAPI.Transform.GetRotation(EntityId);
        ScriptAPI.Transform.SetRotation(EntityId, deltaRot * currentRot);
    }

    public void OnDestroy()
    {
        ScriptAPI.Debug.Log(""RotatingObject: OnDestroy"");
    }
}
";

        // ════════════════════════════════════════════════════════════════════════
        //  3. TriggerZone — 触发区域
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 触发区域模板。
        /// 功能: 当其他实体进入触发范围时, 播放音效并记录日志。
        /// 使用 ScriptAPI.Physics.Raycast 检测前方实体。
        /// </summary>
        public const string TriggerZoneSource = @"using System;
using System.Numerics;
using YamaEngine.Scripting;

/// <summary>触发区域。当其他实体进入范围时触发事件。</summary>
public class TriggerZone
{
    // ── 公共字段 ──────────────────────────────────────────────────────────────

    /// <summary>触发半径。</summary>
    public float TriggerRadius = 3.0f;

    /// <summary>进入触发时播放的音效名称。</summary>
    public string EnterSound = ""TriggerEnter"";

    /// <summary>离开触发时播放的音效名称。</summary>
    public string ExitSound = ""TriggerExit"";

    /// <summary>检测间隔 (秒)。避免每帧检测。</summary>
    public float CheckInterval = 0.1f;

    // ── 内部状态 ──────────────────────────────────────────────────────────────

    /// <summary>绑定的实体 ID。</summary>
    public int EntityId;

    /// <summary>触发器位置 (在 OnStart 时从实体读取)。</summary>
    private Vector3 _position;

    /// <summary>上次检测时间。</summary>
    private float _checkTimer;

    /// <summary>上次检测时是否在触发范围内。</summary>
    private bool _wasTriggered;

    // ── 生命周期 ──────────────────────────────────────────────────────────────

    public void OnStart()
    {
        _position = ScriptAPI.Transform.GetPosition(EntityId);
        _checkTimer = 0f;
        _wasTriggered = false;

        ScriptAPI.Debug.Log(""TriggerZone: OnStart — 半径 "" + TriggerRadius);
    }

    public void OnUpdate(float dt)
    {
        _checkTimer += dt;

        // 按间隔检测
        if (_checkTimer < CheckInterval) return;
        _checkTimer = 0f;

        // 更新触发器位置 (如果实体移动了)
        _position = ScriptAPI.Transform.GetPosition(EntityId);

        // 向 8 个方向发射射线, 检测是否有实体在范围内
        bool anyHit = false;
        Vector3[] directions = new Vector3[]
        {
            Vector3.UnitX, -Vector3.UnitX,
            Vector3.UnitZ, -Vector3.UnitZ,
            Vector3.Normalize(new Vector3(1, 0, 1)),
            Vector3.Normalize(new Vector3(-1, 0, 1)),
            Vector3.Normalize(new Vector3(1, 0, -1)),
            Vector3.Normalize(new Vector3(-1, 0, -1)),
        };

        foreach (var dir in directions)
        {
            var hit = ScriptAPI.Physics.Raycast(_position, dir, TriggerRadius);
            if (hit.HasHit && hit.EntityId != EntityId && hit.EntityId != 0)
            {
                anyHit = true;
                break;
            }
        }

        // 触发状态变化
        if (anyHit && !_wasTriggered)
        {
            // 进入触发
            ScriptAPI.Debug.Log(""TriggerZone: 实体进入触发区域!"");
            ScriptAPI.Audio.Play(EnterSound, _position);
            _wasTriggered = true;
        }
        else if (!anyHit && _wasTriggered)
        {
            // 离开触发
            ScriptAPI.Debug.Log(""TriggerZone: 实体离开触发区域"");
            ScriptAPI.Audio.Play(ExitSound, _position);
            _wasTriggered = false;
        }
    }

    public void OnDestroy()
    {
        ScriptAPI.Debug.Log(""TriggerZone: OnDestroy"");
    }
}
";

        // ════════════════════════════════════════════════════════════════════════
        //  4. FollowTarget — 跟随目标
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 跟随目标模板。
        /// 功能: 平滑跟随指定目标实体的位置, 保持一定距离。
        /// </summary>
        public const string FollowTargetSource = @"using System;
using System.Numerics;
using YamaEngine.Scripting;

/// <summary>跟随目标。平滑追踪指定实体的位置。</summary>
public class FollowTarget
{
    // ── 公共字段 ──────────────────────────────────────────────────────────────

    /// <summary>目标实体 ID (-1 表示未设置)。</summary>
    public int TargetEntityId = -1;

    /// <summary>跟随速度 (越大越快)。</summary>
    public float FollowSpeed = 3.0f;

    /// <summary>与目标保持的距离。</summary>
    public float KeepDistance = 2.0f;

    /// <summary>跟随高度偏移。</summary>
    public float HeightOffset = 0.0f;

    /// <summary>是否面向目标。</summary>
    public bool LookAtTarget = true;

    // ── 内部状态 ──────────────────────────────────────────────────────────────

    /// <summary>自身的实体 ID。</summary>
    public int EntityId;

    // ── 生命周期 ──────────────────────────────────────────────────────────────

    public void OnStart()
    {
        ScriptAPI.Debug.Log(""FollowTarget: OnStart — 目标 "" + TargetEntityId);
    }

    public void OnUpdate(float dt)
    {
        if (TargetEntityId < 0) return;

        // 获取自身和目标位置
        Vector3 myPos = ScriptAPI.Transform.GetPosition(EntityId);
        Vector3 targetPos = ScriptAPI.Transform.GetPosition(TargetEntityId);
        targetPos.Y += HeightOffset;

        // 计算方向和距离
        Vector3 toTarget = targetPos - myPos;
        float distance = toTarget.Length();

        if (distance > KeepDistance)
        {
            // 平滑移动向目标
            Vector3 dir = Vector3.Normalize(toTarget);
            float moveDist = (distance - KeepDistance);
            float actualMove = System.Math.Min(moveDist, FollowSpeed * dt);

            Vector3 newPos = myPos + dir * actualMove;
            ScriptAPI.Transform.SetPosition(EntityId, newPos);

            // 面向目标
            if (LookAtTarget)
            {
                Vector3 lookDir = targetPos - newPos;
                if (lookDir.LengthSquared() > 0.0001f)
                {
                    lookDir = Vector3.Normalize(lookDir);
                    // 计算朝向四元数 (面向 XZ 平面上的目标)
                    float angle = MathF.Atan2(lookDir.X, lookDir.Z);
                    var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
                    ScriptAPI.Transform.SetRotation(EntityId, rot);
                }
            }
        }
    }

    public void OnDestroy()
    {
        ScriptAPI.Debug.Log(""FollowTarget: OnDestroy"");
    }
}
";
    }
}
