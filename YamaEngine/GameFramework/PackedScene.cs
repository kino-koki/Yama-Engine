using System;
using System.Collections.Generic;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 打包场景 (PackedScene)
    //
    //   对应 Godot 的 PackedScene：把一个 GameObject 层级保存为可复用模板，
    //   Instantiate() 每次生成一棵全新的克隆树（独立于原型与其他实例）。
    //   与 GameObjectPool 的区别：PackedScene 是"模板"（无复用回收语义），
    //   Pool 是"对象池"（Get/Release 循环复用）。两者常组合：Pool 用 PackedScene 当原型。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 打包场景：保存一个 GameObject 模板，可反复实例化出独立副本。
    /// 对应 Godot 的 PackedScene（.tscn）。
    /// </summary>
    public sealed class PackedScene
    {
        private readonly GameObjectReal _prototype;

        /// <summary>模板场景名称（取原型名）。</summary>
        public string Name => _prototype.Name;

        /// <summary>模板根节点的直接子节点数。</summary>
        public int ChildCount => _prototype.ChildCount;

        /// <summary>模板根节点的组件数。</summary>
        public int ComponentCount => _prototype.Components.Count;

        /// <summary>
        /// 从一个 GameObject 层级创建打包场景。原型本身不会被修改，
        /// 也不会被直接租出（每次 Instantiate 都是克隆）。
        /// </summary>
        public PackedScene(GameObjectReal prototype)
        {
            _prototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
        }

        /// <summary>
        /// 实例化一棵全新的对象树（深拷贝，独立 Id/组件实例）。
        /// 对应 Godot 的 PackedScene.Instantiate()。
        /// </summary>
        public GameObjectReal Instantiate()
            => _prototype.Instantiate();

        /// <summary>
        /// 批量实例化 count 棵独立的对象树。
        /// </summary>
        public List<GameObjectReal> InstantiateMultiple(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            var list = new List<GameObjectReal>(count);
            for (int i = 0; i < count; i++)
                list.Add(_prototype.Instantiate());
            return list;
        }
    }
}
