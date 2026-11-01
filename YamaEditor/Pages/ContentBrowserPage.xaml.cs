using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using YamaEngine.Assets;

namespace YamaEditor.Pages
{
    /// <summary>
    /// 内容浏览器面板 —— 目录树 + 资产网格视图 + 预览区域。
    /// 支持资产浏览、搜索过滤、导入/删除、双击打开编辑器。
    /// </summary>
    public sealed partial class ContentBrowserPage : Page
    {
        private readonly ObservableCollection<DirectoryViewModel> _directories = new();
        private readonly ObservableCollection<AssetItemViewModel> _assetItems = new();
        private readonly List<AssetItemViewModel> _allAssetItems = new();
        private string _currentDirectory = string.Empty;
        private string _searchFilter = string.Empty;

        public ContentBrowserPage()
        {
            this.InitializeComponent();
            DirectoryTree.ItemsSource = _directories;
            AssetGrid.ItemsSource = _assetItems;
            PopulateDirectories();
            PopulateAssets();
        }

        // ══════════════════════════════════════════════════════════
        //  目录树构建
        // ══════════════════════════════════════════════════════════
        private void PopulateDirectories()
        {
            _directories.Clear();

            var projectPath = App.State?.CurrentProjectPath;
            if (string.IsNullOrEmpty(projectPath))
            {
                _directories.Add(new DirectoryViewModel("Root", "", null));
                return;
            }

            var root = BuildDirectoryTree(projectPath, null);
            _directories.Add(root);
            _currentDirectory = projectPath;
        }

        private static DirectoryViewModel BuildDirectoryTree(string path, DirectoryViewModel? parent)
        {
            var name = Path.GetFileName(path);
            if (parent == null) name = "Content";

            var vm = new DirectoryViewModel(name, path, parent);

            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var dir in Directory.GetDirectories(path))
                    {
                        var dirName = Path.GetFileName(dir);
                        // 跳过隐藏和系统目录
                        if (dirName.StartsWith(".") || dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                        vm.Children.Add(BuildDirectoryTree(dir, vm));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContentBrowser] Directory scan error: {ex.Message}");
            }

            return vm;
        }

        private void OnDirectorySelected(object sender, SelectionChangedEventArgs e)
        {
            if (DirectoryTree.SelectedItem is not DirectoryViewModel dir) return;
            _currentDirectory = dir.FullPath;
            BreadcrumbText.Text = dir.Name;
            PopulateAssets();
        }

        // ══════════════════════════════════════════════════════════
        //  资产列表
        // ══════════════════════════════════════════════════════════
        private void PopulateAssets()
        {
            _assetItems.Clear();
            _allAssetItems.Clear();

            var systems = App.Systems;
            if (systems?.Assets == null) return;

            foreach (var asset in systems.Assets.GetAllAssets())
            {
                var vm = CreateAssetViewModel(asset);
                _allAssetItems.Add(vm);

                // 如果设置了目录过滤，只显示当前目录的资产
                bool matchesDir = string.IsNullOrEmpty(_currentDirectory) ||
                    asset.AssetPath.StartsWith(_currentDirectory, StringComparison.OrdinalIgnoreCase);

                if (matchesDir)
                    _assetItems.Add(vm);
            }

            ApplySearchFilter();
        }

        private static AssetItemViewModel CreateAssetViewModel(AssetMetadata asset)
        {
            return new AssetItemViewModel
            {
                Name = asset.Name,
                AssetPath = asset.AssetPath,
                Type = asset.Type,
                FileSize = asset.FileSize,
                LastModified = asset.LastModified
            };
        }

        // ══════════════════════════════════════════════════════════
        //  搜索过滤
        // ══════════════════════════════════════════════════════════
        private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _searchFilter = sender.Text?.Trim() ?? string.Empty;
                ApplySearchFilter();
            }
        }

        private void ApplySearchFilter()
        {
            _assetItems.Clear();

            foreach (var item in _allAssetItems)
            {
                bool matchesDir = string.IsNullOrEmpty(_currentDirectory) ||
                    item.AssetPath.StartsWith(_currentDirectory, StringComparison.OrdinalIgnoreCase);

                bool matchesSearch = string.IsNullOrEmpty(_searchFilter) ||
                    item.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);

                if (matchesDir && matchesSearch)
                    _assetItems.Add(item);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  资产选中 —— 更新预览区域
        // ══════════════════════════════════════════════════════════
        private void UpdatePreview(AssetItemViewModel? item)
        {
            if (item == null)
            {
                PreviewName.Text = "No asset selected";
                PreviewType.Text = string.Empty;
                PreviewInfo.Text = string.Empty;
                PreviewIcon.Glyph = "\uE8B7";
                return;
            }

            PreviewName.Text = item.Name;
            PreviewType.Text = $"Type: {item.Type}";
            PreviewIcon.Glyph = item.TypeGlyph;

            string sizeStr = item.FileSize > 0 ? FormatFileSize(item.FileSize) : "N/A";
            string dateStr = item.LastModified > DateTime.MinValue ? item.LastModified.ToString("yyyy-MM-dd HH:mm") : "N/A";
            PreviewInfo.Text = $"Size: {sizeStr} | Modified: {dateStr}";
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }

        // ══════════════════════════════════════════════════════════
        //  双击打开
        // ══════════════════════════════════════════════════════════
        private void OnAssetDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (AssetGrid.SelectedItem is not AssetItemViewModel item) return;

            switch (item.Type)
            {
                case AssetType.Material:
                    Debug.WriteLine($"[ContentBrowser] Open Material Editor: {item.Name}");
                    break;
                case AssetType.NodeScript:
                    Debug.WriteLine($"[ContentBrowser] Open NodeScript Editor: {item.Name}");
                    break;
                case AssetType.Shader:
                    Debug.WriteLine($"[ContentBrowser] Open Shader Editor: {item.Name}");
                    break;
                case AssetType.Texture2D:
                    Debug.WriteLine($"[ContentBrowser] Open Texture Viewer: {item.Name}");
                    break;
                case AssetType.Audio:
                    Debug.WriteLine($"[ContentBrowser] Open Audio Preview: {item.Name}");
                    break;
                case AssetType.Animation:
                    Debug.WriteLine($"[ContentBrowser] Open Animation Editor: {item.Name}");
                    break;
                case AssetType.Scene:
                    Debug.WriteLine($"[ContentBrowser] Open Scene: {item.Name}");
                    App.Commands?.Execute("File.Open");
                    break;
                case AssetType.Script:
                    Debug.WriteLine($"[ContentBrowser] Open Script: {item.Name}");
                    // 创建临时 SceneObject 并在 Inspector 中显示脚本内容
                    OpenScriptInInspector(item.AssetPath);
                    break;
                default:
                    Debug.WriteLine($"[ContentBrowser] Open: {item.Name} ({item.Type})");
                    break;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  脚本编辑（双击 .cs 文件）
        // ══════════════════════════════════════════════════════════

        private void OpenScriptInInspector(string scriptPath)
        {
            try
            {
                var scriptText = File.ReadAllText(scriptPath);
                var tempObj = new YamaEngine.Assets.SceneObject("Script: " + Path.GetFileName(scriptPath))
                {
                    IsVisible = false
                };
                var scriptComp = new YamaEngine.Assets.ScriptComponent
                {
                    ScriptAssetPath = scriptPath,
                    ScriptText = scriptText
                };
                tempObj.Components.Add(scriptComp);
                App.Systems?.Inspector.InspectObject(tempObj);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContentBrowser] 打开脚本失败: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  右键菜单
        // ══════════════════════════════════════════════════════════
        private void OnContextImport(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[ContentBrowser] Import dialog requested");
        }

        private void OnContextRename(object sender, RoutedEventArgs e)
        {
            if (AssetGrid.SelectedItem is not AssetItemViewModel item) return;

            var dialog = new ContentDialog
            {
                Title = "Rename Asset",
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var textBox = new TextBox { Text = item.Name };
            dialog.Content = textBox;

            async void Handler(ContentDialog d, ContentDialogResult result)
            {
                if (result == ContentDialogResult.Primary)
                    item.Name = textBox.Text;
                d.PrimaryButtonClick -= Handler;
            }

            dialog.PrimaryButtonClick += Handler;
            _ = dialog.ShowAsync();
        }

        private void OnContextDuplicate(object sender, RoutedEventArgs e)
        {
            if (AssetGrid.SelectedItem is not AssetItemViewModel item)
                Debug.WriteLine($"[ContentBrowser] Duplicate: {item.Name}");
        }

        private void OnContextDelete(object sender, RoutedEventArgs e)
        {
            if (AssetGrid.SelectedItem is not AssetItemViewModel item)
            {
                Debug.WriteLine($"[ContentBrowser] Delete: {item.Name}");
                return;
            }

            _allAssetItems.Remove(item);
            _assetItems.Remove(item);
            Debug.WriteLine($"[ContentBrowser] Deleted: {item.Name}");
        }

        private void OnContextOpenExplorer(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = string.IsNullOrEmpty(_currentDirectory)
                    ? App.State?.CurrentProjectPath
                    : _currentDirectory;

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContentBrowser] Explorer error: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  公共接口
        // ══════════════════════════════════════════════════════════
        public void Refresh()
        {
            PopulateDirectories();
            PopulateAssets();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  目录视图模型
    // ══════════════════════════════════════════════════════════════
    public sealed class DirectoryViewModel : INotifyPropertyChanged
    {
        public string Name { get; }
        public string FullPath { get; }
        public DirectoryViewModel? Parent { get; }
        public ObservableCollection<DirectoryViewModel> Children { get; } = new();

        public DirectoryViewModel(string name, string fullPath, DirectoryViewModel? parent)
        {
            Name = name;
            FullPath = fullPath;
            Parent = parent;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ══════════════════════════════════════════════════════════════
    //  资产项视图模型
    // ══════════════════════════════════════════════════════════════
    public sealed class AssetItemViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private AssetType _type;
        private string _assetPath = string.Empty;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public AssetType Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); OnPropertyChanged(nameof(TypeGlyph)); OnPropertyChanged(nameof(TypeColor)); }
        }

        public string AssetPath
        {
            get => _assetPath;
            set { _assetPath = value; OnPropertyChanged(); }
        }

        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }

        /// <summary>根据资产类型返回 Segoe MDL2 图标 Glyph</summary>
        public string TypeGlyph => Type switch
        {
            AssetType.Mesh => "\uE7C3",           // Cube
            AssetType.Texture2D => "\uEB9F",       // Image
            AssetType.TextureCube => "\uEB9F",
            AssetType.Material => "\uE7A6",       // Palette
            AssetType.Shader => "\uEC7A",          // Code
            AssetType.Audio => "\uEC4F",           // Music
            AssetType.Animation => "\uE915",       // Animation
            AssetType.NodeScript => "\uE8F2",       // Flow
            AssetType.Prefab => "\uE80A",          // Package
            AssetType.Scene => "\uE8B7",           // Folder
            AssetType.Script => "\uE768",         // FileCode
            AssetType.ParticleEffect => "\uE7AC",  // Sparkle
            AssetType.Skeleton => "\uE7B3",        // Bones
            _ => "\uE7C8"                           // File
        };

        /// <summary>根据资产类型返回图标颜色</summary>
        public SolidColorBrush TypeColor => Type switch
        {
            AssetType.Mesh => new SolidColorBrush(Colors.SkyBlue),
            AssetType.Texture2D => new SolidColorBrush(Colors.LightGreen),
            AssetType.Material => new SolidColorBrush(Colors.Violet),
            AssetType.Shader => new SolidColorBrush(Colors.DeepSkyBlue),
            AssetType.Audio => new SolidColorBrush(Colors.Orange),
            AssetType.Animation => new SolidColorBrush(Colors.LightSalmon),
            AssetType.NodeScript => new SolidColorBrush(Colors.CornflowerBlue),
            AssetType.Scene => new SolidColorBrush(Colors.Goldenrod),
            AssetType.Script => new SolidColorBrush(Colors.MediumSeaGreen),
            AssetType.ParticleEffect => new SolidColorBrush(Colors.Magenta),
            _ => new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x96))
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
