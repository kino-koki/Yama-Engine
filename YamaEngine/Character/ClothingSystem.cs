// ======================================================================
// 本文件已整合到 DigitalHumanCreator.cs 中。
// 所有类型（ClothingAsset, ClothingColorScheme, ClothSimulationParams,
// ClothingSystem）已在 DigitalHumanCreator.cs 中重新定义。
//
// 本文件仅保留向后兼容的类型别名。
// ======================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Character
{
    // ClothingSystem 已在 DigitalHumanCreator.cs 中定义，无需重复。
    // 本文件仅用于向后兼容 —— 如有外部引用此文件中的独有类型，
    // 请改用 DigitalHumanCreator.cs 中的对应类型。

    /// <summary>
    /// 向后兼容标记：旧版 ClothingSystem 功能已迁移到 DigitalHumanCreator.cs。
    /// </summary>
    [Obsolete("Use ClothingSystem from DigitalHumanCreator.cs")]
    public sealed class ClothingSystemLegacy
    {
        public static readonly string MigrationNote =
            "All clothing system types have been consolidated into DigitalHumanCreator.cs. " +
            "Use YamaEngine.Character.ClothingSystem directly.";
    }
}
