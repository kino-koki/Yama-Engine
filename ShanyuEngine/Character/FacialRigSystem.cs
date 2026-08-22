// ======================================================================
// 本文件已整合到 DigitalHumanCreator.cs 中。
// 所有类型（FacialRig, ARKitBlendShapes, ARKitBlendShapeRig, ExpressionPreset）
// 已在 DigitalHumanCreator.cs 中定义。
//
// 本文件仅保留向后兼容的类型别名。
// ======================================================================
using System;
using System.Collections.Generic;

namespace ShanyuEngine.Character
{
    /// <summary>
    /// 向后兼容标记：旧版 FacialRigSystem 功能已迁移到 DigitalHumanCreator.cs。
    /// 新类名为 FacialRig（不再叫 FacialRigSystem）。
    /// </summary>
    [Obsolete("Use FacialRig from DigitalHumanCreator.cs")]
    public sealed class FacialRigSystem
    {
        public static readonly string MigrationNote =
            "FacialRigSystem has been consolidated into DigitalHumanCreator.cs as FacialRig. " +
            "Use ShanyuEngine.Character.FacialRig directly.";
    }
}
