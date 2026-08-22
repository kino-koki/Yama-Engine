using System.Collections.Concurrent;

namespace ShanyuEngine.Core;

/// <summary>
/// Strongly-typed asset handle using GUID-based reference.
/// Enables asset referencing without direct object references,
/// supporting hot-reload, streaming, and serialization.
/// </summary>
public readonly struct AssetHandle : IEquatable<AssetHandle>
{
    /// <summary>Zero handle (null reference).</summary>
    public static readonly AssetHandle Zero = new(Guid.Empty, string.Empty);

    /// <summary>Globally unique identifier for the asset.</summary>
    public Guid Guid { get; }

    /// <summary>Asset type identifier (e.g. "Texture2D", "StaticMesh", "Material").</summary>
    public string TypeName { get; }

    /// <summary>Human-readable asset name (for debugging, not serialized).</summary>
    public string? DebugName { get; }

    /// <summary>Whether this handle points to a valid asset.</summary>
    public bool IsValid => Guid != Guid.Empty;

    public AssetHandle(Guid guid, string typeName, string? debugName = null)
    {
        Guid = guid;
        TypeName = typeName;
        DebugName = debugName;
    }

    /// <summary>Create from a string representation.</summary>
    public static AssetHandle Parse(string guidStr, string typeName, string? debugName = null)
    {
        if (Guid.TryParse(guidStr, out var guid))
            return new AssetHandle(guid, typeName, debugName);
        return Zero;
    }

    /// <summary>Serialize to a compact string.</summary>
    public override string ToString() => $"{Guid:N}:{TypeName}";

    public bool Equals(AssetHandle other) => Guid.Equals(other.Guid) && TypeName == other.TypeName;
    public override bool Equals(object? obj) => obj is AssetHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Guid, TypeName);
    public static bool operator ==(AssetHandle left, AssetHandle right) => left.Equals(right);
    public static bool operator !=(AssetHandle left, AssetHandle right) => !left.Equals(right);

    /// <summary>Create a handle for a new asset.</summary>
    public static AssetHandle New(string typeName, string? debugName = null) =>
        new(Guid.NewGuid(), typeName, debugName);
}

/// <summary>
/// Central registry for asset handles. Maps GUIDs to runtime objects.
/// </summary>
public static class AssetHandleRegistry
{
    private static readonly ConcurrentDictionary<Guid, WeakReference<object>> _objects = new();

    public static void Register(AssetHandle handle, object obj)
    {
        _objects[handle.Guid] = new WeakReference<object>(obj);
    }

    public static void Unregister(AssetHandle handle)
    {
        _objects.TryRemove(handle.Guid, out _);
    }

    public static T? Resolve<T>(AssetHandle handle) where T : class
    {
        if (!handle.IsValid) return null;
        if (_objects.TryGetValue(handle.Guid, out var weakRef) && weakRef.TryGetTarget(out var obj))
            return obj as T;
        return null;
    }

    public static bool IsLoaded(AssetHandle handle)
    {
        return _objects.TryGetValue(handle.Guid, out var weakRef) && weakRef.TryGetTarget(out _);
    }

    public static void Clear() => _objects.Clear();
}

/// <summary>
/// Soft reference to an asset that can be used in serialization.
/// Loads the asset on demand when dereferenced.
/// </summary>
public sealed class SoftAssetPtr<T> where T : class
{
    private AssetHandle _handle;
    private T? _cached;

    public AssetHandle Handle => _handle;
    public bool IsLoaded => _cached != null || AssetHandleRegistry.IsLoaded(_handle);
    public T? Get()
    {
        if (_cached != null) return _cached;
        _cached = AssetHandleRegistry.Resolve<T>(_handle);
        return _cached;
    }

    public SoftAssetPtr(AssetHandle handle) { _handle = handle; }
    public SoftAssetPtr() { _handle = AssetHandle.Zero; }

    public void SetHandle(AssetHandle handle)
    {
        if (_handle != handle)
        {
            _handle = handle;
            _cached = null;
        }
    }

    public static implicit operator T?(SoftAssetPtr<T> ptr) => ptr.Get();
}
