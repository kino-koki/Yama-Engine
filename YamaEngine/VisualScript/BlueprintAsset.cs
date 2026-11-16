// BlueprintAsset.cs — 可视化蓝图资产（Blueprint Asset）
// ==================================================================================================
//  命名空间: YamaEngine.VisualScript
//
//  功能:
//    1. BlueprintAsset 包装 BlueprintClass 为可序列化资产（供 AssetDatabase / 内容浏览器使用）
//    2. JSON 序列化往返：变量列表 + 事件图（NodeGraph SerializeToJson / DeserializeFromJson）
//    3. 资产元数据（Id / Name / DisplayName / AssetPath / ParentClass）
// ==================================================================================================
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.VisualScript;

/// <summary>可视化蓝图资产：包装 <see cref="BlueprintClass"/> 为可持久化、可被内容浏览器管理的资产。</summary>
public sealed class BlueprintAsset
{
    /// <summary>资产唯一标识。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>资产名（如 "BP_Player"）。</summary>
    public string Name { get; set; } = "BP_NewBlueprint";

    /// <summary>显示名。</summary>
    public string DisplayName { get; set; } = "New Blueprint";

    /// <summary>资产路径（相对项目根，如 "Blueprints/BP_Player.ybp"）。</summary>
    public string AssetPath { get; set; } = "";

    /// <summary>底层蓝图类（运行时/编辑数据）。</summary>
    [JsonIgnore]
    public BlueprintClass Class { get; set; } = new();

    /// <summary>创建蓝图资产。</summary>
    public static BlueprintAsset Create(string name, string assetPath = "")
    {
        return new BlueprintAsset
        {
            Name = name,
            DisplayName = name.Replace("BP_", "", StringComparison.Ordinal),
            AssetPath = assetPath,
            Class = new BlueprintClass { Name = name, DisplayName = name }
        };
    }

    /// <summary>序列化资产为 JSON（变量 + 事件图 + 元数据）。</summary>
    public string SerializeToJson()
    {
        var dto = new BlueprintAssetDto
        {
            Id = Id,
            Name = Name,
            DisplayName = DisplayName,
            AssetPath = AssetPath,
            ParentClass = Class.ParentClass,
            Variables = Class.Variables,
            EventGraphJson = Class.EventGraphJson,
        };
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>从 JSON 反序列化资产（恢复事件图 OwnerGraph 挂接）。</summary>
    public static BlueprintAsset? DeserializeFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<BlueprintAssetDto>(json);
        if (dto == null) return null;

        var asset = new BlueprintAsset
        {
            Id = dto.Id,
            Name = dto.Name,
            DisplayName = dto.DisplayName,
            AssetPath = dto.AssetPath,
        };
        asset.Class.ParentClass = dto.ParentClass;
        asset.Class.Variables = dto.Variables ?? new List<BlueprintVariable>();
        if (!string.IsNullOrEmpty(dto.EventGraphJson))
            asset.Class.LoadEventGraphJson(dto.EventGraphJson);
        return asset;
    }

    /// <summary>JSON DTO（手动组装，规避 EventGraph 的 [JsonIgnore]）。</summary>
    private sealed class BlueprintAssetDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AssetPath { get; set; } = "";
        public string ParentClass { get; set; } = "";
        public List<BlueprintVariable>? Variables { get; set; }
        public string EventGraphJson { get; set; } = "";
    }
}
