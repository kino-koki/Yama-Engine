// ======================================================================
// 本文件已整合到 DigitalHumanCreator.cs 中。
// 所有类型（BodyRig, BoneData, IKTarget）已在 DigitalHumanCreator.cs 中定义。
//
// 本文件仅保留向后兼容的类型别名。
// ======================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Character
{
    /// <summary>
    /// 向后兼容标记：旧版 BodyRigSystem 功能已迁移到 DigitalHumanCreator.cs。
    /// 新类名为 BodyRig（不再叫 BodyRigSystem）。
    /// </summary>
    [Obsolete("Use BodyRig from DigitalHumanCreator.cs")]
    public sealed class BodyRigSystem
    {
        public static readonly string MigrationNote =
            "BodyRigSystem has been consolidated into DigitalHumanCreator.cs as BodyRig. " +
            "Use YamaEngine.Character.BodyRig directly.";
    }
}
