using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ShanyuEngine.Assets;
using ShanyuEngine.Editor;

namespace ShanyuEngine.Editor.Panels
{
    // ════════════════════════════════════════════════════════════════════════
    // 内容浏览器面板 (ContentBrowserPanel)
    //
    // 浏览与管理项目资产:
    //   * 左侧文件系统树 (Assets 目录)
    //   * 右侧缩略图网格视图 (图标 + 文件名)
    //   * 双击打开资产 (场景加载 / 资产选择)
    //   * 右键菜单 (导入 / 新建文件夹 / 新建材质 / 新建蓝图)
    //   * 搜索栏 + 文件类型过滤器
    //   * 拖拽到视口实例化 (CONTENT_ASSET 负载)
    //   * 收藏夹 / 最近访问
    //   * 选中时底部显示资产预览信息
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ContentBrowserPanel : EditorPanel
    {
        private string _root;
        private string _current;
        private string _search = string.Empty;
        private int _typeFilter; // 0=全部
        private string _selectedPath;
        private AssetMetadata _selectedAsset;

        // 收藏 / 最近
        private readonly List<string> _favorites = new();
        private readonly List<string> _recent = new();
        private const int MaxRecent = 10;

        // 拖放中的资产路径 (供视口/检查器读取)
        public string DraggingAssetPath { get; private set; }

        // 导入弹窗
        private bool _importOpen;
        private string _importSource = string.Empty;

        private static readonly (string Label, string[] Ext, AssetType Type)[] _typeFilters =
        {
            ("全部", Array.Empty<string>(), AssetType.Unknown),
            ("网格", new[] { ".mesh", ".obj", ".fbx", ".gltf", ".glb" }, AssetType.Mesh),
            ("材质", new[] { ".mat", ".material" }, AssetType.Material),
            ("纹理", new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".hdr", ".dds" }, AssetType.Texture2D),
            ("场景", new[] { ".scene" }, AssetType.Scene),
            ("蓝图", new[] { ".bp", ".nodescript" }, AssetType.NodeScript),
            ("音频", new[] { ".wav", ".mp3", ".ogg", ".flac" }, AssetType.Audio),
            ("着色器", new[] { ".shader", ".hlsl", ".glsl" }, AssetType.Shader),
        };

        public ContentBrowserPanel() : base("内容浏览器", "ContentBrowser")
        {
            CanClose = false;
            MinSize = new Vector2(360, 220);
        }

        public override void Initialize(EditorContext ctx)
        {
            _root = Path.Combine(ctx.Assets.RootPath ?? string.Empty, "Assets");
            if (!Directory.Exists(_root)) _root = ctx.Assets.RootPath ?? Environment.CurrentDirectory;
            try { Directory.CreateDirectory(_root); } catch { }
            _current = _root;
        }

        protected override void OnDraw(EditorContext ctx)
        {
            DrawTopBar(ctx);
            ImGui.Separator();

            // 左侧树 + 收藏
            float treeW = Math.Max(160, ImGui.GetWindowWidth() * 0.25f);
            ImGui.BeginChild("##CBTree", new Vector2(treeW, 0), true);
            DrawTree(ctx);
            ImGui.EndChild();

            ImGui.SameLine();

            // 右侧网格
            ImGui.BeginChild("##CBGrid", Vector2.Zero, false);
            DrawBreadcrumb(ctx);
            DrawGrid(ctx);
            ImGui.EndChild();

            DrawImportPopup(ctx);
        }

        // ─────────────────────── 顶栏 ───────────────────────

        private void DrawTopBar(EditorContext ctx)
        {
            ImGui.SetNextItemWidth(120);
            string[] filterLabels = _typeFilters.Select(f => f.Label).ToArray();
            ImGui.Combo("##Filter", ref _typeFilter, filterLabels, filterLabels.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
            ImGui.InputTextWithHint("##CBSearch", "搜索资产...", ref _search, 128);
            ImGui.SameLine();
            if (ImGui.Button("导入")) { _importSource = string.Empty; _importOpen = true; ImGui.OpenPopup("##ImportPopup"); }
        }

        // ─────────────────────── 树 ───────────────────────

        private void DrawTree(EditorContext ctx)
        {
            ImGui.TextDisabled("收藏夹");
            if (_favorites.Count == 0) ImGui.TextDisabled("  (空)");
            foreach (var fav in _favorites.ToArray())
            {
                ImGui.PushID(fav);
                ImGui.TextUnformatted("* ");
                ImGui.SameLine(0, 0);
                if (ImGui.Selectable(Path.GetFileName(fav) + "##fav", _current == fav)) NavigateTo(fav);
                if (ImGui.BeginPopupContextItem("##favCtx"))
                {
                    if (ImGui.MenuItem("移除收藏")) _favorites.Remove(fav);
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }

            ImGui.Separator();
            ImGui.TextDisabled("目录");
            DrawDirectoryTree(ctx, _root);
        }

        private void DrawDirectoryTree(EditorContext ctx, string dir)
        {
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (var sd in subdirs)
            {
                string name = Path.GetFileName(sd);
                ImGui.PushID(sd);
                bool open = ImGui.TreeNodeEx(name + "##dir", ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) NavigateTo(sd);
                if (ImGui.BeginPopupContextItem("##dirCtx"))
                {
                    if (ImGui.MenuItem("收藏")) ToggleFavorite(sd);
                    ImGui.EndPopup();
                }
                if (open) { DrawDirectoryTree(ctx, sd); ImGui.TreePop(); }
                ImGui.PopID();
            }
        }

        // ─────────────────────── 面包屑 ───────────────────────

        private void DrawBreadcrumb(EditorContext ctx)
        {
            var rel = _current.StartsWith(_root) ? _current.Substring(_root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : _current;
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.7f, 1f, 1f));
            ImGui.TextUnformatted("Assets");
            if (!string.IsNullOrEmpty(rel)) ImGui.TextUnformatted("/ " + rel);
            ImGui.PopStyleColor();

            ImGui.SameLine(ImGui.GetWindowWidth() - 120);
            if (ImGui.Button("上一级")) { var p = Directory.GetParent(_current); if (p != null && p.FullName.StartsWith(_root)) NavigateTo(p.FullName); }
            ImGui.SameLine();
            if (ImGui.Button("刷新")) { /* 强制刷新 */ }
        }

        // ─────────────────────── 网格 ───────────────────────

        private void DrawGrid(EditorContext ctx)
        {
            ImGui.BeginChild("##CBGridScroll", new Vector2(0, ImGui.GetContentRegionAvail().Y - 96), true, ImGuiWindowFlags.AlwaysVerticalScrollbar);

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(_current); }
            catch { entries = Array.Empty<string>(); }

            // 文件夹优先
            var dirs = entries.Where(Directory.Exists).OrderBy(e => Path.GetFileName(e)).ToArray();
            var files = entries.Where(File.Exists).OrderBy(e => Path.GetFileName(e)).ToArray();

            float thumb = 72f;
            float spacing = 8f;
            float avail = ImGui.GetContentRegionAvail().X;
            int perRow = Math.Max(1, (int)((avail + spacing) / (thumb + spacing)));

            int col = 0;
            foreach (var d in dirs)
            {
                if (!PassFilter(d, true)) continue;
                DrawGridItem(ctx, d, true, thumb);
                if (++col % perRow != 0) ImGui.SameLine(0, spacing);
            }

            foreach (var f in files)
            {
                if (!PassFilter(f, false)) continue;
                DrawGridItem(ctx, f, false, thumb);
                if (++col % perRow != 0) ImGui.SameLine(0, spacing);
            }

            // 空白处右键菜单
            if (ImGui.BeginPopupContextWindow("##CBBg", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
            {
                DrawContextMenu(ctx);
                ImGui.EndPopup();
            }

            ImGui.EndChild();

            // 底部预览
            DrawPreview(ctx);
        }

        private void DrawGridItem(EditorContext ctx, string path, bool isDir, float size)
        {
            string name = Path.GetFileName(path);
            ImGui.PushID(path);
            ImGui.BeginGroup();

            var type = isDir ? AssetType.Unknown : InferType(path);
            var color = ThumbnailCache.GetColor(type);
            Vector4 border = (_selectedPath == path) ? new Vector4(0.35f, 0.66f, 1f, 1f) : new Vector4(0.25f, 0.25f, 0.28f, 0.6f);

            ImGui.PushStyleColor(ImGuiCol.Button, color);
            ImGui.PushStyleColor(ImGuiCol.Border, border);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, _selectedPath == path ? 2f : 1f);
            ImGui.Button(isDir ? "DIR" : ThumbnailCache.GetTag(type), new Vector2(size, size));
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);

            // 单击选中 / 双击打开
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                _selectedPath = path;
                _selectedAsset = ctx.Assets.GetAsset(path);
            }
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                if (isDir) NavigateTo(path);
                else OpenAsset(ctx, path, type);
            }
            // 右键菜单
            if (ImGui.BeginPopupContextItem("##itemCtx")) { DrawContextMenu(ctx); ImGui.EndPopup(); }

            // 拖放源 (仅文件)
            if (!isDir)
            {
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
                {
                    DraggingAssetPath = path;
                    ImGui.SetDragDropPayload("CONTENT_ASSET", IntPtr.Zero, 0);
                    ImGui.Text(name);
                    ImGui.EndDragDropSource();
                }
                else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    // 拖放结束后清除 (保守处理)
                }
            }

            // 文件名 (可换行截断)
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + size);
            ImGui.TextUnformatted(name);
            ImGui.PopTextWrapPos();

            ImGui.EndGroup();
            ImGui.PopID();
        }

        // ─────────────────────── 预览 ───────────────────────

        private void DrawPreview(EditorContext ctx)
        {
            ImGui.Separator();
            if (string.IsNullOrEmpty(_selectedPath))
            {
                ImGui.TextDisabled("未选中资产");
                return;
            }
            ImGui.Text(Path.GetFileName(_selectedPath));
            if (_selectedAsset != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"  [{_selectedAsset.Type}]");
                ImGui.TextDisabled($"GUID: {_selectedAsset.Guid}");
                ImGui.TextDisabled($"大小: {FormatSize(_selectedAsset.FileSize)}    导入: {_selectedAsset.ImportedAt:yyyy-MM-dd HH:mm}");
            }
            else
            {
                var fi = new FileInfo(_selectedPath);
                ImGui.TextDisabled($"类型: {InferType(_selectedPath)}    大小: {FormatSize(fi.Exists ? fi.Length : 0)}");
            }
        }

        // ─────────────────────── 右键菜单 ───────────────────────

        private void DrawContextMenu(EditorContext ctx)
        {
            if (ImGui.MenuItem("导入...", "Ctrl+I")) { _importOpen = true; ImGui.OpenPopup("##ImportPopup"); }
            ImGui.Separator();
            if (ImGui.MenuItem("新建文件夹")) CreateFolder();
            if (ImGui.MenuItem("新建材质")) CreateAsset(ctx, ".mat", "NewMaterial");
            if (ImGui.MenuItem("新建蓝图")) CreateAsset(ctx, ".bp", "NewNodeScript");
            if (ImGui.MenuItem("新建场景")) CreateAsset(ctx, ".scene", "NewScene");
            ImGui.Separator();
            if (ImGui.MenuItem("收藏当前目录", null, false, Directory.Exists(_selectedPath) || true)) ToggleFavorite(_current);
        }

        private void DrawImportPopup(EditorContext ctx)
        {
            if (!_importOpen) return;
            ImGui.SetNextWindowSize(new Vector2(420, 120));
            if (ImGui.Begin("导入资产##ImportPopup", ref _importOpen, ImGuiWindowFlags.NoResize))
            {
                ImGui.Text("源文件路径:");
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                ImGui.InputText("##ImportSrc", ref _importSource, 512);
                ImGui.SameLine();
                if (ImGui.Button("导入"))
                {
                    if (File.Exists(_importSource))
                    {
                        try
                        {
                            ctx.Assets.ImportAsset(_importSource);
                            ctx.Console?.AddLog(LogLevel.Info, $"已导入: {_importSource}", "ContentBrowser");
                            _importOpen = false;
                        }
                        catch (Exception ex) { ctx.Console?.AddLog(LogLevel.Error, $"导入失败: {ex.Message}", "ContentBrowser"); }
                    }
                    else ctx.Console?.AddLog(LogLevel.Warning, "源文件不存在", "ContentBrowser");
                }
                ImGui.SameLine();
                if (ImGui.Button("取消")) _importOpen = false;
                ImGui.End();
            }
        }

        // ─────────────────────── 操作 ───────────────────────

        private void NavigateTo(string dir)
        {
            _current = dir;
            _selectedPath = null;
            _selectedAsset = null;
            AddRecent(dir);
        }

        private void AddRecent(string dir)
        {
            _recent.Remove(dir);
            _recent.Insert(0, dir);
            if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
        }

        private void ToggleFavorite(string dir)
        {
            if (_favorites.Contains(dir)) _favorites.Remove(dir);
            else _favorites.Add(dir);
        }

        private void CreateFolder()
        {
            string name = "NewFolder";
            string path = Path.Combine(_current, name);
            int i = 1;
            while (Directory.Exists(path)) { path = Path.Combine(_current, name + i++); }
            try { Directory.CreateDirectory(path); } catch { }
        }

        private void CreateAsset(EditorContext ctx, string ext, string baseName)
        {
            string name = baseName + ext;
            string path = Path.Combine(_current, name);
            int i = 1;
            while (File.Exists(path)) { path = Path.Combine(_current, baseName + i++ + ext); }
            try
            {
                if (ext == ".mat")
                    File.WriteAllText(path, "{\n  \"type\": \"Material\",\n  \"baseColor\": [1,1,1],\n  \"metallic\": 0,\n  \"roughness\": 0.6\n}\n");
                else if (ext == ".bp")
                    File.WriteAllText(path, "{\n  \"type\": \"NodeScript\",\n  \"nodes\": []\n}\n");
                else if (ext == ".scene")
                    File.WriteAllText(path, "{\n  \"type\": \"Scene\",\n  \"objects\": []\n}\n");
                else File.WriteAllText(path, string.Empty);

                ctx.Assets.RegisterAsset(Path.GetFileName(path), Guid.NewGuid(), InferType(path), Path.GetFileNameWithoutExtension(path));
                ctx.Console?.AddLog(LogLevel.Info, $"已创建资产: {Path.GetFileName(path)}", "ContentBrowser");
            }
            catch (Exception ex) { ctx.Console?.AddLog(LogLevel.Error, $"创建失败: {ex.Message}", "ContentBrowser"); }
        }

        private void OpenAsset(EditorContext ctx, string path, AssetType type)
        {
            AddRecent(Path.GetDirectoryName(path) ?? _current);
            switch (type)
            {
                case AssetType.Scene:
                    try { ctx.Assets.LoadScene(path); ctx.Console?.AddLog(LogLevel.Info, $"已加载场景: {path}", "ContentBrowser"); }
                    catch (Exception ex) { ctx.Console?.AddLog(LogLevel.Error, $"加载场景失败: {ex.Message}", "ContentBrowser"); }
                    break;
                default:
                    ctx.Console?.AddLog(LogLevel.Info, $"打开资产编辑器: {path} (占位)", "ContentBrowser");
                    break;
            }
        }

        // ─────────────────────── 过滤 ───────────────────────

        private bool PassFilter(string path, bool isDir)
        {
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(_search) && !name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                return false;
            if (_typeFilter == 0) return true;
            if (isDir) return true; // 文件夹始终显示
            var ext = Path.GetExtension(path);
            var exts = _typeFilters[_typeFilter].Ext;
            return exts.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        private AssetType InferType(string path)
        {
            var ext = Path.GetExtension(path)?.ToLowerInvariant();
            for (int i = 1; i < _typeFilters.Length; i++)
                if (_typeFilters[i].Ext.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                    return _typeFilters[i].Type;
            return AssetType.Unknown;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }
    }

    // ─────────────────────── 缩略图缓存 ───────────────────────

    internal static class ThumbnailCache
    {
        public static Vector4 GetColor(AssetType type)
        {
            return type switch
            {
                AssetType.Mesh => new Vector4(0.45f, 0.8f, 0.5f, 1f),
                AssetType.Material => new Vector4(0.85f, 0.55f, 0.25f, 1f),
                AssetType.Texture2D or AssetType.TextureCube => new Vector4(0.55f, 0.7f, 1f, 1f),
                AssetType.Scene => new Vector4(0.75f, 0.6f, 1f, 1f),
                AssetType.NodeScript => new Vector4(0.95f, 0.78f, 0.25f, 1f),
                AssetType.Audio => new Vector4(0.4f, 0.85f, 0.95f, 1f),
                AssetType.Shader => new Vector4(0.95f, 0.4f, 0.7f, 1f),
                _ => new Vector4(0.4f, 0.4f, 0.42f, 1f),
            };
        }

        public static string GetTag(AssetType type)
        {
            return type switch
            {
                AssetType.Mesh => "MESH",
                AssetType.Material => "MAT",
                AssetType.Texture2D => "TEX",
                AssetType.TextureCube => "CUB",
                AssetType.Scene => "SCN",
                AssetType.NodeScript => "BP",
                AssetType.Audio => "AUD",
                AssetType.Shader => "SHD",
                AssetType.Animation => "ANM",
                AssetType.Prefab => "PFB",
                AssetType.Font => "FNT",
                _ => "FILE",
            };
        }
    }
}
