using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ShanyuEngine.Assets;
using ShanyuEngine.Core;

namespace ShanyuEditor.Pages
{
    /// <summary>
    /// 场景层级面板（Outliner）—— TreeView 显示 Actor 层级树。
    /// 支持搜索过滤、右键菜单、拖放重排序、多选、双击聚焦。
    /// </summary>
    public sealed partial class OutlinerPage : Page
    {
        private readonly ObservableCollection<ActorViewModel> _rootNodes = new();
        private readonly List<ActorViewModel> _allNodes = new();
        private string _searchFilter = string.Empty;

        public OutlinerPage()
        {
            this.InitializeComponent();
            HierarchyTree.ItemsSource = _rootNodes;
            PopulateHierarchy();
            SubscribeToEngineEvents();
        }

        // ══════════════════════════════════════════════════════════
        //  从引擎场景构建层级树
        // ══════════════════════════════════════════════════════════
        private void PopulateHierarchy()
        {
            _rootNodes.Clear();
            _allNodes.Clear();

            var systems = App.Systems;
            if (systems?.Assets == null) return;

            foreach (var obj in systems.Assets.SceneObjects)
            {
                var vm = new ActorViewModel(obj);
                _allNodes.Add(vm);

                if (obj.Parent == null)
                    _rootNodes.Add(vm);
            }

            ApplySearchFilter();
        }

        private void SubscribeToEngineEvents()
        {
            var systems = App.Systems;
            if (systems?.Assets == null) return;
            // 监听选择变更以同步高亮
            systems.Selection.OnSelectionChanged += OnEngineSelectionChanged;
        }

        private void OnEngineSelectionChanged(Assets.SceneObject? selected)
        {
            // 在下次 Dispatcher 回调中同步 TreeView 选中状态
            DispatcherQueue.TryEnqueue(() =>
            {
                if (selected == null)
                {
                    HierarchyTree.SelectedItems.Clear();
                    return;
                }

                var target = _allNodes.FirstOrDefault(n => n.Actor == selected);
                if (target != null)
                {
                    HierarchyTree.SelectedItems.Clear();
                    HierarchyTree.SelectedItems.Add(target);
                }
            });
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
            foreach (var node in _allNodes)
            {
                bool visible = string.IsNullOrEmpty(_searchFilter) ||
                    node.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
                node.IsVisible = visible;
            }

            _rootNodes.Clear();
            foreach (var node in _allNodes)
            {
                if (node.Actor?.Parent == null && (node.IsVisible || HasVisibleChildren(node)))
                    _rootNodes.Add(node);
            }
        }

        private static bool HasVisibleChildren(ActorViewModel node)
        {
            foreach (var child in node.Children)
            {
                if (child.IsVisible || HasVisibleChildren(child))
                    return true;
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════
        //  TreeView 交互
        // ══════════════════════════════════════════════════════════
        private void OnTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (HierarchyTree.SelectedItem is not ActorViewModel vm) return;
            FocusOnActor(vm);
            e.Handled = true;
        }

        private void FocusOnActor(ActorViewModel vm)
        {
            var systems = App.Systems;
            if (systems == null || vm.Actor == null) return;

            systems.Selection.Select(vm.Actor);
            // 相机聚焦到 Actor 位置
            systems.ViewportCamera.Target = vm.Actor.Position;
            systems.ViewportCamera.Distance = Math.Max(2f, vm.Actor.Bounds.Max - vm.Actor.Bounds.Min).Length() * 1.5f;
        }

        // ══════════════════════════════════════════════════════════
        //  可见性切换
        // ══════════════════════════════════════════════════════════
        private void OnVisibilityTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not ActorViewModel vm) return;

            vm.Actor!.IsVisible = !vm.Actor.IsVisible;
            vm.IsVisibleInScene = vm.Actor.IsVisible;
            Debug.WriteLine($"[Outliner] {vm.Name} visibility: {vm.Actor.IsVisible}");
            e.Handled = true;
        }

        // ══════════════════════════════════════════════════════════
        //  右键菜单
        // ══════════════════════════════════════════════════════════
        private void OnContextRename(object sender, RoutedEventArgs e)
        {
            if (HierarchyTree.SelectedItem is not ActorViewModel vm) return;

            var dialog = new ContentDialog
            {
                Title = "Rename Actor",
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var textBox = new TextBox { Text = vm.Name };
            dialog.Content = textBox;

            async void Handler(ContentDialog contentDialog, ContentDialogResult result)
            {
                if (result == ContentDialogResult.Primary && vm.Actor != null)
                {
                    vm.Actor.Name = textBox.Text;
                    vm.Name = textBox.Text;
                }
                contentDialog.PrimaryButtonClick -= Handler;
            }

            dialog.PrimaryButtonClick += Handler;
            _ = dialog.ShowAsync();
        }

        private void OnContextDuplicate(object sender, RoutedEventArgs e)
        {
            if (HierarchyTree.SelectedItem is not ActorViewModel vm || vm.Actor == null) return;

            var systems = App.Systems;
            if (systems?.Assets == null) return;

            var copy = new Assets.SceneObject
            {
                Name = vm.Actor.Name + " (Copy)",
                Position = vm.Actor.Position + new System.Numerics.Vector3(1, 0, 0),
                Rotation = vm.Actor.Rotation,
                Scale = vm.Actor.Scale,
                IsVisible = vm.Actor.IsVisible
            };

            systems.Assets.SceneObjects.Add(copy);
            PopulateHierarchy();
            Debug.WriteLine($"[Outliner] Duplicated: {vm.Name}");
        }

        private void OnContextSetParent(object sender, RoutedEventArgs e)
        {
            if (HierarchyTree.SelectedItem is not ActorViewModel vm || vm.Actor == null) return;

            var systems = App.Systems;
            if (systems?.Assets == null) return;

            var parent = systems.Selection.Active;
            if (parent != null && parent != vm.Actor && parent.Children != null)
            {
                parent.AttachChild(vm.Actor);
                PopulateHierarchy();
                Debug.WriteLine($"[Outliner] Set parent: {vm.Name} -> {parent.Name}");
            }
        }

        private void OnContextDelete(object sender, RoutedEventArgs e)
        {
            if (HierarchyTree.SelectedItem is not ActorViewModel vm || vm.Actor == null) return;

            var systems = App.Systems;
            if (systems?.Assets == null) return;

            // 从父级移除
            if (vm.Actor.Parent != null)
                vm.Actor.Parent.Children.Remove(vm.Actor);
            systems.Assets.SceneObjects.Remove(vm.Actor);
            systems.Selection.Clear();
            PopulateHierarchy();
            Debug.WriteLine($"[Outliner] Deleted: {vm.Name}");
        }

        // ══════════════════════════════════════════════════════════
        //  拖放
        // ══════════════════════════════════════════════════════════
        private void OnTreeDragStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.FirstOrDefault() is ActorViewModel vm)
            {
                e.Data.Properties["ActorId"] = vm.Actor?.Id.ToString();
            }
        }

        private void OnTreeDragCompleted(object sender, DragItemsCompletedEventArgs e)
        {
            // 拖放完成后刷新层级
            PopulateHierarchy();
        }

        // ══════════════════════════════════════════════════════════
        //  公共接口 —— 供 MainWindow 调用
        // ══════════════════════════════════════════════════════════
        public void Refresh() => PopulateHierarchy();

        public void SelectActor(Assets.SceneObject? actor)
        {
            var vm = _allNodes.FirstOrDefault(n => n.Actor == actor);
            if (vm != null)
            {
                HierarchyTree.SelectedItems.Clear();
                HierarchyTree.SelectedItems.Add(vm);
            }
        }
    }

    /// <summary>
    /// Actor 视图模型 —— 用于 TreeView 数据绑定
    /// </summary>
    public sealed class ActorViewModel : INotifyPropertyChanged
    {
        public Assets.SceneObject? Actor { get; }
        public ObservableCollection<ActorViewModel> Children { get; } = new();

        private string _name;
        private bool _isVisible = true;
        private bool _isVisibleInScene = true;
        private bool _isSelected;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        public bool IsVisibleInScene
        {
            get => _isVisibleInScene;
            set
            {
                _isVisibleInScene = value;
                OnPropertyChanged();
                // 同步更新图标
                OnPropertyChanged(nameof(VisibilityGlyph));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public string VisibilityGlyph => _isVisibleInScene ? "\uE7B3" : "\uE7B4";

        public ActorViewModel(Assets.SceneObject actor)
        {
            Actor = actor;
            _name = actor.Name;
            _isVisibleInScene = actor.IsVisible;

            if (actor.Children != null)
            {
                foreach (var child in actor.Children)
                    Children.Add(new ActorViewModel(child));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
