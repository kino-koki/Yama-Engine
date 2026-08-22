using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Scenes
{
    // ====================================================================
    // SceneManager — 场景管理器
    // ====================================================================
    // 职责：
    //   - 当前场景管理（加载 / 卸载 / 切换 / 保存）
    //   - 自动保存（定时序列化到临时目录）
    //   - 场景栈（子场景叠加 / 弹出返回）
    //   - 场景实例化（从 SceneData 创建 ECS 实体）
    //
    // 与 EntityComponentSystem 协作：场景实体直接创建于传入的 ECS 实例中。
    // ====================================================================

    /// <summary>
    /// 场景管理器。管理当前场景的生命周期、自动保存与场景栈，
    /// 通过 <see cref="EntityComponentSystem"/> 实例化场景实体。
    /// </summary>
    public sealed class SceneManager : IDisposable
    {
        private readonly EntityComponentSystem _ecs;
        private readonly ShanyuEngine.Scripting.ScriptRegistry? _scriptRegistry;
        private SceneData? _currentScene;
        private string? _currentScenePath;
        private readonly Stack<SceneStackEntry> _sceneStack = new();

        // 自动保存
        private Timer? _autoSaveTimer;
        private float _autoSaveIntervalSec = 60f;
        private readonly string _autoSaveDir;
        private int _autoSaveKeepCount = 10;

        private bool _disposed;

        /// <summary>当前场景数据（可能为 null 表示无场景）。</summary>
        public SceneData? CurrentScene => _currentScene;

        /// <summary>当前场景文件路径（加载或保存时记录）。</summary>
        public string? CurrentScenePath => _currentScenePath;

        /// <summary>场景栈深度（叠加的子场景数量）。</summary>
        public int SceneStackDepth => _sceneStack.Count;

        /// <summary>场景是否有未保存更改。</summary>
        public bool IsDirty { get; set; }

        /// <summary>自动保存间隔（秒）。</summary>
        public float AutoSaveInterval
        {
            get => _autoSaveIntervalSec;
            set
            {
                _autoSaveIntervalSec = value;
                if (_autoSaveTimer != null) StartAutoSave(value); // 重新调度
            }
        }

        /// <summary>自动保存保留的文件数量（旧的自动清理）。</summary>
        public int AutoSaveKeepCount
        {
            get => _autoSaveKeepCount;
            set => _autoSaveKeepCount = Math.Max(1, value);
        }

        /// <summary>
        /// 创建场景管理器。
        /// </summary>
        /// <param name="ecs">关联的实体组件系统，场景实体将创建于此。</param>
        /// <param name="scriptRegistry">可选的脚本注册表，用于序列化/反序列化脚本实例。</param>
        /// <param name="autoSaveDir">自动保存临时目录；null 时使用系统临时目录。</param>
        public SceneManager(EntityComponentSystem ecs, ShanyuEngine.Scripting.ScriptRegistry? scriptRegistry = null, string? autoSaveDir = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _scriptRegistry = scriptRegistry;
            _autoSaveDir = autoSaveDir ?? Path.Combine(Path.GetTempPath(), "ShanyuEngine", "Autosave");
        }

        // ──────────────────────────────────────────────────────────────
        // 新建 / 加载 / 卸载
        // ──────────────────────────────────────────────────────────────

        /// <summary>创建一个新的空场景，并清空当前 ECS。</summary>
        public SceneData NewScene(string name = "Untitled")
        {
            UnloadCurrentScene();
            _currentScene = new SceneData { Name = name, Version = SceneJsonSerializer.CurrentVersion };
            _currentScenePath = null;
            IsDirty = false;
            return _currentScene;
        }

        /// <summary>从文件加载场景，清空当前 ECS 并实例化新场景实体。返回 GUID→实体ID 映射。</summary>
        public Dictionary<Guid, int> LoadScene(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Scene path is required.", nameof(path));

            UnloadCurrentScene();
            SceneData data = SceneJsonSerializer.LoadFromFile(path);
            _currentScene = data;
            _currentScenePath = path;
            IsDirty = false;
            return SceneJsonSerializer.RestoreToECS(data, _ecs, _scriptRegistry);
        }

        /// <summary>从 SceneData 加载场景，清空当前 ECS 并实例化。返回 GUID→实体ID 映射。</summary>
        public Dictionary<Guid, int> LoadScene(SceneData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            UnloadCurrentScene();
            _currentScene = data;
            IsDirty = false;
            return SceneJsonSerializer.RestoreToECS(data, _ecs, _scriptRegistry);
        }

        /// <summary>卸载当前场景：清空 ECS 中所有实体并重置场景引用。</summary>
        public void UnloadCurrentScene()
        {
            if (_currentScene != null || _ecs.EntityCount > 0)
            {
                _ecs.Clear();
            }
            _currentScene = null;
            _currentScenePath = null;
            IsDirty = false;
        }

        // ──────────────────────────────────────────────────────────────
        // 保存
        // ──────────────────────────────────────────────────────────────

        /// <summary>保存当前场景到文件。先从 ECS 重建最新数据再写入。</summary>
        public void SaveCurrentScene(string? path = null)
        {
            if (_currentScene == null)
                throw new InvalidOperationException("No active scene to save.");

            // 从 ECS 重建以确保数据最新
            string name = _currentScene.Name;
            _currentScene = SceneJsonSerializer.BuildFromECS(_ecs, name, _scriptRegistry);

            string savePath = path ?? _currentScenePath;
            if (string.IsNullOrWhiteSpace(savePath))
                throw new InvalidOperationException("No file path specified and no current scene path recorded.");

            SceneJsonSerializer.SaveToFile(_currentScene, savePath);
            _currentScenePath = savePath;
            IsDirty = false;
        }

        /// <summary>尝试保存当前场景，返回是否成功（不抛异常）。</summary>
        public bool TrySaveCurrentScene(string? path, out string? error)
        {
            try
            {
                SaveCurrentScene(path);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ──────────────────────────────────────────────────────────────
        // 场景切换与栈
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 切换场景：将当前场景压入栈并清空 ECS，然后加载新场景。
        /// 可通过 <see cref="PopScene"/> 返回上一个场景。
        /// </summary>
        public Dictionary<Guid, int> SwitchScene(string path)
        {
            // 保存当前场景到栈
            if (_currentScene != null)
            {
                var snapshot = SceneJsonSerializer.BuildFromECS(_ecs, _currentScene.Name, _scriptRegistry);
                _sceneStack.Push(new SceneStackEntry
                {
                    Scene = snapshot,
                    Path = _currentScenePath
                });
            }

            _ecs.Clear();
            _currentScene = null;
            return LoadScene(path);
        }

        /// <summary>
        /// 从场景栈弹出并恢复上一个场景。返回 GUID→实体ID 映射；栈空时返回 null。
        /// </summary>
        public Dictionary<Guid, int>? PopScene()
        {
            if (_sceneStack.Count == 0) return null;

            var entry = _sceneStack.Pop();
            _ecs.Clear();
            _currentScene = entry.Scene;
            _currentScenePath = entry.Path;
            IsDirty = false;
            return SceneJsonSerializer.RestoreToECS(entry.Scene, _ecs);
        }

        /// <summary>
        /// 叠加子场景：不清空当前场景，将子场景的实体追加到 ECS 中。
        /// 适用于 UI 覆盖层、小关卡叠加等场景。
        /// </summary>
        public Dictionary<Guid, int> PushSubScene(string path)
        {
            SceneData data = SceneJsonSerializer.LoadFromFile(path);
            return SceneJsonSerializer.RestoreToECS(data, _ecs, _scriptRegistry);
        }

        /// <summary>从 SceneData 叠加子场景到当前 ECS（不清空）。</summary>
        public Dictionary<Guid, int> PushSubScene(SceneData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return SceneJsonSerializer.RestoreToECS(data, _ecs, _scriptRegistry);
        }

        /// <summary>清空场景栈（不影响当前场景）。</summary>
        public void ClearSceneStack()
        {
            _sceneStack.Clear();
        }

        // ──────────────────────────────────────────────────────────────
        // 场景实例化
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 从 SceneData 实例化实体到当前 ECS（不清空已有实体）。
        /// 返回 GUID→实体ID 映射。
        /// </summary>
        public Dictionary<Guid, int> InstantiateScene(SceneData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            IsDirty = true;
            return SceneJsonSerializer.RestoreToECS(data, _ecs, _scriptRegistry);
        }

        /// <summary>从文件实例化场景实体到当前 ECS（不清空）。</summary>
        public Dictionary<Guid, int> InstantiateSceneFromFile(string path)
        {
            return InstantiateScene(SceneJsonSerializer.LoadFromFile(path));
        }

        // ──────────────────────────────────────────────────────────────
        // 自动保存
        // ──────────────────────────────────────────────────────────────

        /// <summary>启动定时自动保存。每隔 intervalSeconds 秒将当前场景序列化到临时目录。</summary>
        public void StartAutoSave(float intervalSeconds)
        {
            _autoSaveIntervalSec = Math.Max(1f, intervalSeconds);
            StopAutoSave();

            int periodMs = (int)(_autoSaveIntervalSec * 1000);
            _autoSaveTimer = new Timer(
                callback: _ => DoAutoSave(),
                state: null,
                dueTime: periodMs,
                period: periodMs);
        }

        /// <summary>停止自动保存。</summary>
        public void StopAutoSave()
        {
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Dispose();
                _autoSaveTimer = null;
            }
        }

        /// <summary>立即执行一次自动保存（不影响定时器调度）。</summary>
        public void AutoSaveNow()
        {
            DoAutoSave();
        }

        private void DoAutoSave()
        {
            if (_currentScene == null || _ecs == null) return;

            try
            {
                Directory.CreateDirectory(_autoSaveDir);
                var data = SceneJsonSerializer.BuildFromECS(_ecs, _currentScene.Name, _scriptRegistry);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                string tempPath = Path.Combine(_autoSaveDir, $"{SanitizeFileName(_currentScene.Name)}_{stamp}.shanyu");
                SceneJsonSerializer.SaveToFile(data, tempPath);

                // 清理过旧的自动保存文件
                CleanupOldAutoSaves();
            }
            catch
            {
                // 自动保存失败不应导致引擎崩溃
            }
        }

        private void CleanupOldAutoSaves()
        {
            try
            {
                string prefix = _currentScene != null ? SanitizeFileName(_currentScene.Name) + "_" : "";
                var files = new List<FileInfo>();
                foreach (string f in Directory.EnumerateFiles(_autoSaveDir, prefix + "*.shanyu"))
                    files.Add(new FileInfo(f));

                if (files.Count <= _autoSaveKeepCount) return;

                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = _autoSaveKeepCount; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { /* 忽略删除失败 */ }
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.Length == 0 ? "Untitled" : sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────
        // 同步当前场景数据
        // ──────────────────────────────────────────────────────────────

        /// <summary>从 ECS 同步最新状态到 CurrentScene（不写文件）。</summary>
        public void SyncFromECS()
        {
            if (_currentScene == null) return;
            string name = _currentScene.Name;
            _currentScene = SceneJsonSerializer.BuildFromECS(_ecs, name, _scriptRegistry);
        }

        // ──────────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAutoSave();
            _sceneStack.Clear();
        }

        // ──────────────────────────────────────────────────────────────
        // 内部结构
        // ──────────────────────────────────────────────────────────────

        private struct SceneStackEntry
        {
            public SceneData Scene;
            public string? Path;
        }
    }
}
