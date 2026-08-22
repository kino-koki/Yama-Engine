// ======================================================================
// 本文件已整合到 DigitalHumanCreator.cs 中。
// 所有类型（HairStrand, ScalpRegion, WindParameters, HairGroomingSystem）
// 已在 DigitalHumanCreator.cs 中定义。
//
// 本文件仅保留向后兼容的类型别名。
// ======================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Character
{
    /// <summary>
    /// 向后兼容标记：旧版 HairGroomingSystem 功能已迁移到 DigitalHumanCreator.cs。
    /// </summary>
    [Obsolete("Use HairGroomingSystem from DigitalHumanCreator.cs")]
    public sealed class HairGroomingSystemLegacy
    {
        public static readonly string MigrationNote =
            "All hair grooming types have been consolidated into DigitalHumanCreator.cs. " +
            "Use ShanyuEngine.Character.HairGroomingSystem directly.";
    }
}
