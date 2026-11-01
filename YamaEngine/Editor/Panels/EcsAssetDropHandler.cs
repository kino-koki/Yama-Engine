// ==================================================================================================
//  EcsAssetDropHandler.cs — yama引擎 ECS 资产拖放处理器
// ==================================================================================================
//  命名空间: YamaEngine.Editor.Panels
//  依赖:     ImGuiNET, YamaEngine.ECS, YamaEngine.Assets
//
//  功能:
//    处理从 ContentBrowser 拖放到 ECS 层级面板或视口的资产：
//      * 网格 (.fbx/.obj/.gltf)    → 创建带 MeshComponent 的实体
//      * 材质 (.mat)               → 附加到选中实体或创建新实体
//      * 纹理 (.png/.jpg/.dds)     → 创建带 Material 引用纹理的实体
//      * 场景 (.yama/.yaml)      → 通知上层加载
//      * 预制体 (.prefab)          → 标记 Tag 并通知上层
//      * 音效 (.wav/.ogg/.mp3)     → 标记 Tag 并通知上层
//
//  拖放协议：ContentBrowser 使用 "CONTENT_ASSET" payload，负载为文件路径字符串。
// ==================================================================================================

using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using YamaEngine.ECS;

namespace YamaEngine.Editor.Panels
{
    /// <summary>
    /// ECS 资产拖放处理器。将 ContentBrowser 的资产拖放转换为 ECS 实体创建。
    /// </summary>
    public sealed class EcsAssetDropHandler
    {
        private readonly EntityComponentSystem _ecs;
        private readonly EcsSelectionState _selection;
        private readonly EcsCommandStackAdapter? _commands;

        public event Action<int>? OnEntityCreated;
        public event Action? OnSceneModified;

        /// <summary>当需要加载场景文件时触发。上层应调用 SceneManager.LoadScene。</summary>
        public event Action<string>? OnRequestLoadScene;

        public EcsAssetDropHandler(
            EntityComponentSystem ecs,
            EcsSelectionState selection,
            EcsCommandStackAdapter? commands = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));
            _commands = commands;
        }

        /// <summary>
        /// 在指定 ImGui 区域接受资产拖放。
        /// 如果检测到拖放，处理并返回 true。
        /// </summary>
        public bool AcceptDrop()
        {
            if (!ImGui.BeginDragDropTarget()) return false;

            bool handled = false;
            unsafe
            {
                var payload = ImGui.AcceptDragDropPayload("CONTENT_ASSET");
                if (payload.NativePtr != null && payload.Data != null)
                {
                    string path = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(
                        (IntPtr)payload.Data, payload.DataSize) ?? "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        HandleAssetDrop(path);
                        handled = true;
                    }
                }
            }
            ImGui.EndDragDropTarget();
            return handled;
        }

        /// <summary>
        /// 直接处理资产路径（非拖放触发，如双击或脚本调用）。
        /// </summary>
        public int HandleAssetDrop(string path)
        {
            if (!File.Exists(path)) return -1;
            string ext = Path.GetExtension(path).ToLowerInvariant();

            return ext switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" => CreateMeshEntity(path),
                ".mat" => ApplyMaterialToSelection(path),
                ".png" or ".jpg" or ".jpeg" or ".dds" or ".tga" => CreateTextureEntity(path),
                ".yama" or ".yaml" or ".yml" => LoadSceneAsSubscene(path),
                ".prefab" => InstantiatePrefab(path),
                ".wav" or ".ogg" or ".mp3" or ".flac" => CreateAudioEntity(path),
                _ => -1,
            };
        }

        private int CreateMeshEntity(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int id = _ecs.CreateEntity(name, position: Vector3.Zero,
                rotation: Quaternion.Identity, scale: Vector3.One);

            var mesh = new MeshComponent
            {
                MeshId = Guid.NewGuid(), // 占位：实际应通过 AssetDatabase 解析为真实 GUID
                MaterialId = Guid.Empty,
                IsVisible = true,
                CastsShadow = true,
                ReceivesShadow = true,
            };

            _ecs.AddComponent(id, mesh);
            _ecs.AddComponent(id, new TagComponent
            {
                Tag = "Mesh",
                Layer = 0,
            });

            _selection.Select(id);
            OnEntityCreated?.Invoke(id);
            OnSceneModified?.Invoke();
            return id;
        }

        private int ApplyMaterialToSelection(string path)
        {
            int active = _selection.Active;
            if (active < 0 || !_ecs.HasEntity(active))
            {
                // 没有选中实体，创建新实体
                string name = Path.GetFileNameWithoutExtension(path);
                int id = _ecs.CreateEntity(name, position: Vector3.Zero,
                    rotation: Quaternion.Identity, scale: Vector3.One);

                _ecs.AddComponent(id, new MeshComponent
                {
                    MeshId = Guid.NewGuid(),
                    MaterialId = Guid.NewGuid(), // 占位
                    IsVisible = true,
                });
                _ecs.AddComponent(id, new TagComponent { Tag = "Material", Layer = 0 });
                _selection.Select(id);
                OnEntityCreated?.Invoke(id);
                OnSceneModified?.Invoke();
                return id;
            }

            // 应用到选中实体
            if (!_ecs.HasComponent<MeshComponent>(active))
                _ecs.AddComponent(active, new MeshComponent { IsVisible = true });

            var mesh = _ecs.GetComponent<MeshComponent>(active);
            mesh.MaterialId = Guid.NewGuid(); // 占位：实际应通过 AssetDatabase 解析
            _ecs.SetComponent(active, mesh);

            OnSceneModified?.Invoke();
            return active;
        }

        private int CreateTextureEntity(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int id = _ecs.CreateEntity(name, position: Vector3.Zero,
                rotation: Quaternion.Identity, scale: Vector3.One);

            _ecs.AddComponent(id, new MeshComponent
            {
                MeshId = Guid.NewGuid(),
                MaterialId = Guid.NewGuid(),
                IsVisible = true,
            });
            _ecs.AddComponent(id, new TagComponent { Tag = "Texture", Layer = 0 });

            _selection.Select(id);
            OnEntityCreated?.Invoke(id);
            OnSceneModified?.Invoke();
            return id;
        }

        private int LoadSceneAsSubscene(string path)
        {
            OnRequestLoadScene?.Invoke(path);
            OnSceneModified?.Invoke();
            return -1;
        }

        private int InstantiatePrefab(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int id = _ecs.CreateEntity(name, position: Vector3.Zero,
                rotation: Quaternion.Identity, scale: Vector3.One);

            _ecs.AddComponent(id, new TagComponent
            {
                Tag = "Prefab",
                Layer = 0,
            });

            _selection.Select(id);
            OnEntityCreated?.Invoke(id);
            OnSceneModified?.Invoke();
            return id;
        }

        private int CreateAudioEntity(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int id = _ecs.CreateEntity(name, position: Vector3.Zero,
                rotation: Quaternion.Identity, scale: Vector3.One);

            _ecs.AddComponent(id, new TagComponent
            {
                Tag = "AudioSource",
                Layer = 0,
            });

            _selection.Select(id);
            OnEntityCreated?.Invoke(id);
            OnSceneModified?.Invoke();
            return id;
        }
    }
}
