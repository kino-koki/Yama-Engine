// =============================================================================
//  GodotResourceLoader.cs
//  yama引擎 - Godot 3 风格资源加载器（引用计数 + 依赖图 + 线程加载状态机）
//
//  对应 Godot 3 语义：
//    * Resource：带引用计数的资源对象（get_reference_count / reference / unreference）
//    * IResourceBackend：按路径提供资源字节/对象（可插拔，测试用内存后端）
//    * ResourceLoader.Load<T>：同步加载（缓存命中直接返回）
//    * LoadThreadedRequest / LoadThreadedGetStatus / LoadThreadedRetrieve：
//      分帧异步加载状态机（对应 Godot 的 load_threaded_*）
//    * 依赖：资源可声明依赖路径，先加载依赖再合成
//    * 缓存 + 引用计数 + 卸载（cache mode）
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace YamaEngine.UI
{
    /// <summary>带引用计数的资源基类（对应 Godot Resource 的 refcount 语义）。</summary>
    public abstract class Resource
    {
        private int _refCount;

        /// <summary>当前引用计数。</summary>
        public int ReferenceCount => _refCount;

        /// <summary>资源路径（加载来源，对应 Godot resource_path）。</summary>
        public string ResourcePath { get; internal set; } = string.Empty;

        /// <summary>资源类型名（对应 Godot get_class）。</summary>
        public abstract string ResourceType { get; }

        /// <summary>增加引用（对应 Godot reference()）。</summary>
        public void Reference() => Interlocked.Increment(ref _refCount);

        /// <summary>减少引用，返回是否降到 0（对应 Godot unreference()）。</summary>
        public bool Unreference()
        {
            int v = Interlocked.Decrement(ref _refCount);
            return v <= 0;
        }
    }

    /// <summary>简单文本资源（测试用）。</summary>
    public sealed class TextResource : Resource
    {
        public string Text { get; set; } = string.Empty;
        public override string ResourceType => "TextResource";
    }

    /// <summary>带依赖的复合资源（测试用）：持有子资源列表。</summary>
    public sealed class CompositeResource : Resource
    {
        public List<Resource> Children { get; } = new();
        public override string ResourceType => "CompositeResource";
    }

    /// <summary>资源后端（对应 Godot 的文件系统 / .res 解析）。可插拔，测试用内存后端。</summary>
    public interface IResourceBackend
    {
        /// <summary>是否存在该路径的资源。</summary>
        bool Exists(string path);

        /// <summary>加载资源（同步）。backend 可依赖传入的 loader 来加载子依赖。</summary>
        Resource Load(string path, GodotResourceLoader loader);
    }

    /// <summary>加载状态（对应 Godot LoadThreadedStatus）。</summary>
    public enum LoadThreadedStatus
    {
        Invalid,      // 未开始/未知
        InProgress,   // 加载中
        Loaded,       // 已完成，可 Retrieve
        Failed,       // 失败
    }

    /// <summary>缓存模式（对应 Godot ResourceLoader.CacheMode）。</summary>
    public enum CacheMode
    {
        UseCache,     // 用缓存，命中直接返回
        IgnoreCache,  // 绕过缓存重新加载（仍写回缓存）
    }

    /// <summary>
    /// Godot 3 风格资源加载器。引用计数 + 缓存 + 依赖 + 分帧线程加载状态机。
    /// 后端（IResourceBackend）可插拔，测试注入内存后端即可。
    /// </summary>
    public sealed class GodotResourceLoader
    {
        private readonly Dictionary<string, Resource> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly IResourceBackend _backend;

        /// <summary>缓存资源数。</summary>
        public int CacheCount => _cache.Count;

        /// <summary>线程加载任务数（进行中 + 待取）。</summary>
        public int ThreadedTaskCount => _threadedTasks.Count;

        private sealed class ThreadedTask
        {
            public string Path;
            public LoadThreadedStatus Status = LoadThreadedStatus.InProgress;
            public Resource? Result;
            public string? Error;
        }

        private readonly Dictionary<string, ThreadedTask> _threadedTasks = new(StringComparer.OrdinalIgnoreCase);

        public GodotResourceLoader(IResourceBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        // ══════════════════════════════════════════════════════════
        // 同步加载
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 同步加载资源（对应 Godot ResourceLoader.Load）。
        /// 缓存命中（UseCache）直接返回并引用计数 +1。
        /// </summary>
        public T? Load<T>(string path, CacheMode cacheMode = CacheMode.UseCache) where T : Resource
        {
            if (cacheMode == CacheMode.UseCache && _cache.TryGetValue(path, out var cached))
            {
                cached.Reference();
                return cached as T;
            }

            if (!_backend.Exists(path))
                return null;

            var res = _backend.Load(path, this);
            if (res == null) return null;

            res.ResourcePath = path;
            res.Reference(); // 首次引用（loader 持有）
            if (cacheMode == CacheMode.UseCache)
                _cache[path] = res;

            return res as T;
        }

        /// <summary>是否已缓存该路径。</summary>
        public bool HasCached(string path) => _cache.ContainsKey(path);

        /// <summary>取缓存引用计数（未缓存返回 0）。</summary>
        public int GetCachedRefCount(string path)
            => _cache.TryGetValue(path, out var r) ? r.ReferenceCount : 0;

        /// <summary>卸载缓存（对应 Godot ResourceLoader 取消持有引用）。返回是否真降到 0。</summary>
        public bool Unload(string path)
        {
            if (!_cache.TryGetValue(path, out var r)) return false;
            _cache.Remove(path);
            return r.Unreference(); // loader 释放其持有引用
        }

        /// <summary>清除所有缓存引用。</summary>
        public void ClearCache()
        {
            foreach (var kv in _cache)
                kv.Value.Unreference();
            _cache.Clear();
        }

        // ══════════════════════════════════════════════════════════
        // 分帧线程加载状态机（对应 Godot load_threaded_*）
        // ══════════════════════════════════════════════════════════

        /// <summary>发起线程加载请求（对应 load_threaded_request）。重复请求同路径无副作用。</summary>
        public void LoadThreadedRequest(string path)
        {
            if (_threadedTasks.ContainsKey(path)) return;
            _threadedTasks[path] = new ThreadedTask { Path = path };
        }

        /// <summary>推进一帧：每个 InProgress 任务执行一小步（对应 Godot 后台线程分片）。
        /// useBackend=false 时模拟"进度推进但不完成"（多帧加载）。</summary>
        public void LoadThreadedTick(bool completeAll = true)
        {
            foreach (var kv in _threadedTasks)
            {
                var task = kv.Value;
                if (task.Status != LoadThreadedStatus.InProgress) continue;

                try
                {
                    if (_backend.Exists(task.Path))
                    {
                        var res = _backend.Load(task.Path, this);
                        res.ResourcePath = task.Path;
                        res.Reference();
                        _cache[task.Path] = res;
                        task.Result = res;
                        task.Status = LoadThreadedStatus.Loaded;
                    }
                    else
                    {
                        task.Status = LoadThreadedStatus.Failed;
                        task.Error = "Resource not found.";
                    }
                }
                catch (Exception ex)
                {
                    task.Status = LoadThreadedStatus.Failed;
                    task.Error = ex.Message;
                }

                if (!completeAll && task.Status == LoadThreadedStatus.InProgress)
                    break; // 模拟部分进度（本帧只推进一个）
            }
        }

        /// <summary>取线程加载状态（对应 load_threaded_get_status）。</summary>
        public LoadThreadedStatus LoadThreadedGetStatus(string path)
            => _threadedTasks.TryGetValue(path, out var t) ? t.Status : LoadThreadedStatus.Invalid;

        /// <summary>取线程加载错误信息（失败时）。</summary>
        public string? LoadThreadedGetError(string path)
            => _threadedTasks.TryGetValue(path, out var t) ? t.Error : null;

        /// <summary>取回已完成资源（对应 load_threaded_get_resource），同时清除任务记录。</summary>
        public T? LoadThreadedRetrieve<T>(string path) where T : Resource
        {
            if (!_threadedTasks.TryGetValue(path, out var t)) return null;
            if (t.Status != LoadThreadedStatus.Loaded) return null;
            var res = t.Result as T;
            _threadedTasks.Remove(path);
            return res;
        }

        /// <summary>清理所有线程加载任务记录（不释放资源）。</summary>
        public void ClearThreadedTasks() => _threadedTasks.Clear();
    }
}
