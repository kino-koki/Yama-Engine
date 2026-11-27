namespace YamaEngine.Scripting.Abi
{
    /// <summary>ABI 边界上允许传递的值类型。跨程序集/脚本域只暴露这些种类。</summary>
    public enum ScriptVariantKind
    {
        Null = 0,
        Bool,
        Int32,
        Int64,
        Single,
        Double,
        String,
        Vector2,
        Vector3,
        Vector4,
        NodeRef,
        ResourceRef,
        Array,
        Dictionary,
        Object
    }
}
