namespace YamaEngine.GameFramework
{
    /// <summary>
    /// 节点处理模式（对应 Godot 的 Node.process_mode）。
    /// 控制节点在场景树暂停/运行时是否参与 Update。
    /// </summary>
    public enum NodeProcessMode
    {
        /// <summary>继承父节点模式（无父则等同 Pausable）。</summary>
        Inherit = 0,

        /// <summary>正常：运行时处理，暂停时不处理（默认）。</summary>
        Pausable = 1,

        /// <summary>仅暂停时处理（用于暂停菜单动画等）。</summary>
        WhenPaused = 2,

        /// <summary>始终处理（无论暂停与否）。</summary>
        Always = 3,

        /// <summary>禁用：永不处理。</summary>
        Disabled = 4,
    }
}
