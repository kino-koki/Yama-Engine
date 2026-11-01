using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace YamaEngine.Scripting
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  LoadedAssemblyInfo
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Metadata describing a successfully loaded script assembly.
    /// </summary>
    public sealed class LoadedAssemblyInfo
    {
        /// <summary>The friendly name used for the assembly and its load context.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>The loaded <see cref="Assembly"/> object.</summary>
        public Assembly Assembly { get; init; } = null!;

        /// <summary>All concrete, non-abstract <see cref="ScriptComponent"/> types found in this assembly.</summary>
        public IReadOnlyList<Type> ScriptTypes { get; init; } = Array.Empty<Type>();

        /// <summary>UTC timestamp when the assembly was loaded.</summary>
        public DateTime LoadedAt { get; init; }

        /// <summary>Number of script types discovered.</summary>
        public int ScriptTypeCount => ScriptTypes.Count;

        /// <summary>Human-readable summary.</summary>
        public string Summary =>
            $"{Name}: {ScriptTypes.Count} script type(s), loaded at {LoadedAt:HH:mm:ss.fff}";

        internal LoadedAssemblyInfo() { }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptLoadContext — collectible AssemblyLoadContext
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A collectible (unloadable) <see cref="AssemblyLoadContext"/> that hosts a single
    /// script assembly and its transitive dependencies.  Framework and engine assemblies
    /// are resolved from the default context so that type identity is preserved.
    /// </summary>
    internal sealed class ScriptLoadContext : AssemblyLoadContext
    {
        /// <summary>
        /// Names of assemblies that must always resolve from the default (shared) context
        /// so that the engine and script code share the same type identities.
        /// </summary>
        private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
        {
            "YamaEngine",
            "System.Runtime",
            "System.Private.CoreLib",
            "System.Collections",
            "System.Linq",
            "System.Numerics",
            "System.Numerics.Vectors",
            "System.Console",
            "System.Threading",
            "System.Reflection",
            "System.IO.FileSystem",
            "System.Text.RegularExpressions",
            "System.Runtime.InteropServices",
            "netstandard",
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.CSharp",
            "Vortice.Direct3D12",
            "Vortice.DXGI",
            "Vortice.Mathematics",
            "Vortice.DirectX",
            "Silk.NET.Vulkan",
            "SixLabors.ImageSharp",
            "MiniAudio.NET",
        };

        private readonly AssemblyDependencyResolver? _resolver;

        /// <summary>
        /// Creates a new collectible context.  If <paramref name="mainAssemblyPath"/> is
        /// provided, an <see cref="AssemblyDependencyResolver"/> is used to locate
        /// satellite assemblies from a .deps.json file.
        /// </summary>
        public ScriptLoadContext(string name, string? mainAssemblyPath = null)
            : base(name, isCollectible: true)
        {
            if (!string.IsNullOrEmpty(mainAssemblyPath))
                _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Always let shared/engine/framework assemblies fall through to the
            // default context so that type identity is shared.
            if (SharedAssemblies.Contains(assemblyName.Name ?? string.Empty))
                return null;

            // For non-shared assemblies, try the dependency resolver (if available).
            // This handles cases where scripts reference additional NuGet packages.
            if (_resolver != null)
            {
                var path = _resolver.ResolveAssemblyToPath(assemblyName);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return LoadFromAssemblyPath(path);
            }

            // Return null → fall back to the default context.
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // Let the default context resolve native libraries.
            return IntPtr.Zero;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptAssemblyLoader
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Manages loading and unloading of compiled script assemblies using collectible
    /// <see cref="AssemblyLoadContext"/> instances.  Each load creates a new context;
    /// unloading a context releases the associated assembly for garbage collection.
    /// </summary>
    public sealed class ScriptAssemblyLoader : IDisposable
    {
        private readonly List<LoadedEntry> _entries = new();
        private readonly object _lock = new();
        private bool _disposed;

        /// <summary>Number of currently loaded script assemblies.</summary>
        public int LoadedCount
        {
            get
            {
                lock (_lock) return _entries.Count;
            }
        }

        /// <summary>All currently loaded assembly infos.</summary>
        public IReadOnlyList<LoadedAssemblyInfo> LoadedAssemblies
        {
            get
            {
                lock (_lock) return _entries.Select(e => e.Info).ToList();
            }
        }

        /// <summary>
        /// Loads an assembly from an in-memory PE stream (with optional PDB stream)
        /// into a new collectible <see cref="AssemblyLoadContext"/>.
        /// </summary>
        /// <param name="peStream">Stream containing the compiled PE image.</param>
        /// <param name="pdbStream">Optional stream containing portable PDB symbols.</param>
        /// <param name="name">Friendly name for the assembly and its load context.</param>
        /// <returns>Metadata about the loaded assembly.</returns>
        public LoadedAssemblyInfo LoadFromStream(Stream peStream, Stream? pdbStream, string name)
        {
            if (peStream == null) throw new ArgumentNullException(nameof(peStream));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
            ThrowIfDisposed();

            lock (_lock)
            {
                // Ensure stream positions are at the start.
                if (peStream.CanSeek) peStream.Position = 0;
                if (pdbStream != null && pdbStream.CanSeek) pdbStream.Position = 0;

                var context = new ScriptLoadContext(name);
                Assembly assembly;
                try
                {
                    assembly = pdbStream != null
                        ? context.LoadFromStream(peStream, pdbStream)
                        : context.LoadFromStream(peStream);
                }
                catch (Exception ex)
                {
                    // If loading fails, unload the empty context to avoid leaks.
                    try { context.Unload(); } catch { /* best effort */ }
                    throw new InvalidOperationException(
                        $"Failed to load script assembly '{name}': {ex.Message}", ex);
                }

                var scriptTypes = DiscoverScriptTypes(assembly);
                var info = new LoadedAssemblyInfo
                {
                    Name = name,
                    Assembly = assembly,
                    ScriptTypes = scriptTypes,
                    LoadedAt = DateTime.UtcNow,
                };

                var entry = new LoadedEntry
                {
                    Context = context,
                    Info = info,
                    WeakRef = new WeakReference(context),
                };
                _entries.Add(entry);

                return info;
            }
        }

        /// <summary>
        /// Loads an assembly from a file path into a new collectible context.
        /// The <see cref="AssemblyDependencyResolver"/> is used to locate dependencies
        /// from the .deps.json file next to the assembly.
        /// </summary>
        /// <param name="assemblyPath">Path to the compiled .dll file.</param>
        /// <param name="pdbPath">Optional path to the .pdb file.</param>
        /// <param name="name">Friendly name; defaults to the file name without extension.</param>
        public LoadedAssemblyInfo LoadFromFile(string assemblyPath, string? pdbPath = null, string? name = null)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new ArgumentException("Assembly path is required.", nameof(assemblyPath));
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException($"Script assembly not found: {assemblyPath}", assemblyPath);
            ThrowIfDisposed();

            var friendlyName = name ?? Path.GetFileNameWithoutExtension(assemblyPath);
            var context = new ScriptLoadContext(friendlyName, assemblyPath);

            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            }
            catch (Exception ex)
            {
                try { context.Unload(); } catch { /* best effort */ }
                throw new InvalidOperationException(
                    $"Failed to load script assembly from '{assemblyPath}': {ex.Message}", ex);
            }

            var scriptTypes = DiscoverScriptTypes(assembly);
            var info = new LoadedAssemblyInfo
            {
                Name = friendlyName,
                Assembly = assembly,
                ScriptTypes = scriptTypes,
                LoadedAt = DateTime.UtcNow,
            };

            lock (_lock)
            {
                _entries.Add(new LoadedEntry
                {
                    Context = context,
                    Info = info,
                    WeakRef = new WeakReference(context),
                });
            }

            return info;
        }

        /// <summary>
        /// Unloads the specified assembly's load context.  The assembly becomes eligible
        /// for garbage collection once all references are released.
        /// </summary>
        /// <param name="info">The assembly info returned from a prior load call.</param>
        /// <returns><c>true</c> if the assembly was found and unloaded; <c>false</c> otherwise.</returns>
        public bool Unload(LoadedAssemblyInfo info)
        {
            if (info == null) return false;
            ThrowIfDisposed();

            lock (_lock)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (ReferenceEquals(_entries[i].Info, info))
                    {
                        var entry = _entries[i];
                        try { entry.Context.Unload(); }
                        catch (Exception) { /* context may already be unloading */ }
                        _entries.RemoveAt(i);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Unloads all script assemblies managed by this loader.
        /// </summary>
        public void UnloadAll()
        {
            lock (_lock)
            {
                foreach (var entry in _entries)
                {
                    try { entry.Context.Unload(); }
                    catch (Exception) { /* best effort */ }
                }
                _entries.Clear();
            }
        }

        /// <summary>
        /// Forces a full garbage collection and waits for pending finalizers.
        /// Call this after <see cref="Unload"/> or <see cref="UnloadAll"/> to
        /// release the unloaded assemblies promptly.
        /// </summary>
        public void CollectGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// Checks whether a previously unloaded context has actually been collected
        /// by the GC.  Useful for diagnostics.
        /// </summary>
        /// <param name="info">The assembly info that was unloaded.</param>
        /// <returns><c>true</c> if the context is still alive (not yet collected); <c>false</c> if collected.</returns>
        public bool IsContextAlive(LoadedAssemblyInfo info)
        {
            if (info == null) return false;
            lock (_lock)
            {
                foreach (var entry in _entries)
                {
                    if (ReferenceEquals(entry.Info, info))
                        return entry.WeakRef.IsAlive;
                }
            }
            // Not found — already removed, assume collected.
            return false;
        }

        /// <summary>
        /// Returns all <see cref="ScriptComponent"/>-derived types across all loaded
        /// script assemblies.
        /// </summary>
        public IReadOnlyList<Type> GetAllScriptTypes()
        {
            lock (_lock)
            {
                var types = new List<Type>();
                foreach (var entry in _entries)
                    types.AddRange(entry.Info.ScriptTypes);
                return types;
            }
        }

        /// <summary>
        /// Attempts to find a loaded script type by its full name.
        /// </summary>
        public Type? FindScriptType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            lock (_lock)
            {
                foreach (var entry in _entries)
                {
                    foreach (var type in entry.Info.ScriptTypes)
                    {
                        if (type.FullName == fullName || type.Name == fullName)
                            return type;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Scans an assembly for all concrete, non-abstract types that derive from
        /// <see cref="ScriptComponent"/> and have a parameterless constructor.
        /// </summary>
        private static List<Type> DiscoverScriptTypes(Assembly assembly)
        {
            var result = new List<Type>();
            Type[] allTypes;

            try
            {
                allTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Some types may fail to load due to missing dependencies;
                // collect the ones that did load.
                allTypes = rtle.Types
                    .Where(t => t != null)
                    .ToArray()!;
            }

            foreach (var type in allTypes)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(ScriptComponent).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                result.Add(type);
            }

            return result;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ScriptAssemblyLoader));
        }

        /// <summary>Unloads all assemblies and releases resources.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            UnloadAll();
            _disposed = true;
        }

        // ── Internal entry ──────────────────────────────────────────────────────────

        private sealed class LoadedEntry
        {
            public ScriptLoadContext Context { get; init; } = null!;
            public LoadedAssemblyInfo Info { get; init; } = null!;
            public WeakReference WeakRef { get; init; } = null!;
        }
    }
}
