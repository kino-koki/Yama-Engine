using System;
using System.Numerics;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - PlayerStart (GameFramework)
    //
    // 类似 UE PlayerStart:
    //   * 位置/旋转
    //   * 玩家标签 (分队伍/分角色)
    //   * 占用标记
    //   * GetSpawnTransform() 返回世界变换
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 玩家出生点。在关卡编辑器中放置, GameMode 使用它决定新玩家/Pawn 的生成位置。
    /// </summary>
    public class PlayerStart : Actor
    {
        // ── 标签 ──────────────────────────────────────────────────────────

        /// <summary>玩家标签 (用于按队伍/角色分配出生点, 如 "TeamA"/"TeamB")。</summary>
        public string PlayerTag { get; set; } = string.Empty;

        /// <summary>角色标签 (用于特定角色类的出生点)。</summary>
        public string CharacterClassTag { get; set; } = string.Empty;

        // ── 占用 ──────────────────────────────────────────────────────────

        /// <summary>是否已被玩家占用。</summary>
        public bool IsOccupied { get; set; }

        /// <summary>占用此出生点的玩家 ID。</summary>
        public string OccupyingPlayerId { get; set; } = string.Empty;

        /// <summary>占用此出生点的 Actor (Pawn 或 Controller)。</summary>
        public Actor OccupyingActor { get; set; }

        // ── 优先级 ────────────────────────────────────────────────────────

        /// <summary>出生优先级 (数值越高, 越优先被选择)。</summary>
        public int Priority { get; set; }

        /// <summary>是否启用 (禁用时 GameMode 不会选择此出生点)。</summary>
        public bool IsEnabled { get; set; } = true;

        // ── 变换 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 获取生成变换 (世界矩阵)。
        /// PlayerStart 的 Transform 直接定义了 Pawn 生成时的位置/朝向。
        /// </summary>
        public Matrix4x4 GetSpawnTransform()
        {
            return GetWorldMatrix();
        }

        /// <summary>获取生成位置。</summary>
        public Vector3 GetSpawnLocation() => Position;

        /// <summary>获取生成旋转 (仅偏航, 通常 Pawn 不需要俯仰/翻滚)。</summary>
        public Quaternion GetSpawnRotation() => Rotation;

        // ── 占用/释放 ──────────────────────────────────────────────────────

        /// <summary>标记此出生点被指定玩家占用。</summary>
        public void Claim(string playerId, Actor actor)
        {
            IsOccupied = true;
            OccupyingPlayerId = playerId;
            OccupyingActor = actor;
        }

        /// <summary>释放此出生点。</summary>
        public void Release()
        {
            IsOccupied = false;
            OccupyingPlayerId = string.Empty;
            OccupyingActor = null;
        }

        /// <summary>判断是否可与指定标签匹配。</summary>
        public bool MatchesTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return true;
            return PlayerTag == tag || CharacterClassTag == tag;
        }

        // ── 初始化 ────────────────────────────────────────────────────────

        protected override void OnBeginPlay()
        {
            base.OnBeginPlay();
            Name = string.IsNullOrEmpty(PlayerTag)
                ? "PlayerStart"
                : $"PlayerStart_{PlayerTag}";
        }

        // ── 编辑器 ────────────────────────────────────────────────────────

        public override void OnEditorSelected()
        {
            base.OnEditorSelected();
        }

        public override string ToString()
        {
            return $"PlayerStart '{PlayerTag}' @ {Position} (Occupied={IsOccupied})";
        }
    }
}
