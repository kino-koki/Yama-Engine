using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShanyuEngine.Assets;
using ShanyuEngine.Core;

namespace ShanyuEditor.Pages
{
    /// <summary>
    /// Property Inspector page for ShanyuEditor.
    /// Displays transform, components, rendering, and scene properties for selected objects.
    /// Integrates with engine SelectionSystem and ECS.
    /// </summary>
    public sealed partial class InspectorPage : Page
    {
        // ── ViewModels ──────────────────────────────────────────────────────

        public class ComponentViewModel : INotifyPropertyChanged
        {
            private string _componentName = "Component";
            private string _componentType = string.Empty;
            private ObservableCollection<PropertyViewModel> _properties = new();

            public string ComponentName { get => _componentName; set { _componentName = value; OnPropertyChanged(); } }
            public string ComponentType { get => _componentType; set { _componentType = value; OnPropertyChanged(); } }
            public ObservableCollection<PropertyViewModel> Properties { get => _properties; set { _properties = value; OnPropertyChanged(); } }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public class PropertyViewModel : INotifyPropertyChanged
        {
            private string _propertyName = "Property";
            private string _displayValue = string.Empty;

            public string PropertyName { get => _propertyName; set { _propertyName = value; OnPropertyChanged(); } }
            public string DisplayValue { get => _displayValue; set { _displayValue = value; OnPropertyChanged(); } }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ── Data ────────────────────────────────────────────────────────────

        public ObservableCollection<ComponentViewModel> Components { get; } = new();

        private Assets.SceneObject? _inspectedObject;
        private bool _updatingUI;

        private bool _hasSelection = true;

        public bool HasSelection
        {
            get => _hasSelection;
            set { _hasSelection = value; UpdateVisibility(); }
        }

        private bool _isMultiSelect;

        public bool IsMultiSelect
        {
            get => _isMultiSelect;
            set
            {
                _isMultiSelect = value;
                MultiSelectHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ── Constructor ─────────────────────────────────────────────────────

        public InspectorPage()
        {
            this.InitializeComponent();
            ComponentList.ItemsSource = Components;
            UpdateVisibility();
            SubscribeToSelection();
        }

        // ── Selection System Integration ─────────────────────────────────────

        private void SubscribeToSelection()
        {
            var systems = App.Systems;
            if (systems == null) return;
            systems.Selection.OnSelectionChanged += OnSelectionChanged;
        }

        private void OnSelectionChanged(Assets.SceneObject? selected)
        {
            _ = DispatcherQueue.TryEnqueue(() => InspectObject(selected));
        }

        // ── Inspect Object ──────────────────────────────────────────────────

        private void InspectObject(Assets.SceneObject? obj)
        {
            _inspectedObject = obj;

            if (obj == null)
            {
                HasSelection = false;
                return;
            }

            HasSelection = true;
            _updatingUI = true;

            // Name & Type
            ActorNameBox.Text = obj.Name;
            ActorTypeLabel.Text = "SceneObject";

            // Transform - Position
            PosX.Value = Math.Round(obj.Position.X, 3);
            PosY.Value = Math.Round(obj.Position.Y, 3);
            PosZ.Value = Math.Round(obj.Position.Z, 3);

            // Transform - Rotation (euler degrees)
            var euler = QuaternionToEuler(obj.Rotation);
            RotX.Value = Math.Round(euler.X, 1);
            RotY.Value = Math.Round(euler.Y, 1);
            RotZ.Value = Math.Round(euler.Z, 1);

            // Transform - Scale
            ScaleX.Value = Math.Round(obj.Scale.X, 3);
            ScaleY.Value = Math.Round(obj.Scale.Y, 3);
            ScaleZ.Value = Math.Round(obj.Scale.Z, 3);

            // Rendering
            CastShadow.IsChecked = obj.CastShadow;
            ReceiveShadow.IsChecked = obj.ReceiveShadow;
            IsStatic.IsChecked = obj.IsStatic;

            // Material
            MetallicSlider.Value = obj.Metallic;
            RoughnessSlider.Value = obj.Roughness;
            TextureAssetPathBox.Text = obj.TextureAssetPath ?? string.Empty;

            _updatingUI = false;
        }

        // ── Transform Value Changed ────────────────────────────────────────

        private void Transform_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_updatingUI || _inspectedObject == null) return;

            // Position
            _inspectedObject.Position = new Vector3((float)PosX.Value, (float)PosY.Value, (float)PosZ.Value);

            // Rotation (convert degrees to radians then to quaternion)
            float rx = (float)RotX.Value * MathF.PI / 180f;
            float ry = (float)RotY.Value * MathF.PI / 180f;
            float rz = (float)RotZ.Value * MathF.PI / 180f;
            _inspectedObject.Rotation = Quaternion.CreateFromYawPitchRoll(ry, rx, rz);

            // Scale
            _inspectedObject.Scale = new Vector3((float)ScaleX.Value, (float)ScaleY.Value, (float)ScaleZ.Value);

            App.State!.IsModified = true;
            TransformChanged?.Invoke(this, new TransformChangedEventArgs
            {
                Position = _inspectedObject.Position,
                Rotation = new Vector3((float)RotX.Value, (float)RotY.Value, (float)RotZ.Value),
                Scale = _inspectedObject.Scale
            });
        }

        // ── Name Changed ─────────────────────────────────────────────────────

        private void OnObjectNameChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingUI || _inspectedObject == null) return;
            _inspectedObject.Name = ActorNameBox.Text;
        }

        // ── Component Management ────────────────────────────────────────────

        public void ClearComponents() => Components.Clear();

        public void AddComponent(ComponentViewModel component) => Components.Add(component);

        public void RemoveComponentAt(int index)
        {
            if (index >= 0 && index < Components.Count)
                Components.RemoveAt(index);
        }

        private void OnAddComponentClick(object sender, RoutedEventArgs e) { }

        private void AddComponentItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && _inspectedObject != null)
            {
                var compName = item.Tag?.ToString() ?? "Unknown";

                // 实际创建组件并附加到 SceneObject
                switch (compName)
                {
                    case "Script":
                        _inspectedObject.Components.Add(new ShanyuEngine.Assets.ScriptComponent());
                        break;
                    case "Animator":
                        _inspectedObject.Components.Add(new ShanyuEngine.Assets.ObjectAnimatorComponent());
                        break;
                    case "Light":
                        _inspectedObject.Components.Add(new LightComponent());
                        break;
                    case "Camera":
                        _inspectedObject.Components.Add(new CameraComponent());
                        break;
                }

                var vm = new ComponentViewModel
                {
                    ComponentName = compName,
                    ComponentType = compName
                };

                vm.Properties.Add(new PropertyViewModel { PropertyName = "Enabled", DisplayValue = "True" });
                Components.Add(vm);
                ComponentAdded?.Invoke(this, new ComponentEventArgs(compName));
                RefreshComponentList();
            }
        }

        private void RemoveComponent_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[Inspector] Remove component requested");
        }

        private void CopyComponent_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[Inspector] Copy component requested");
        }

        private void ResetComponent_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[Inspector] Reset component requested");
        }

        // ── Visibility ─────────────────────────────────────────────────────

        private void UpdateVisibility()
        {
            ScenePropertiesPanel.Visibility = HasSelection ? Visibility.Collapsed : Visibility.Visible;
            ObjectInspectorPanel.Visibility = HasSelection ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Helpers: Quaternion <-> Euler ──────────────────────────────────

        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            float sinX = 2f * (q.W * q.X + q.Y * q.Z);
            float cosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float roll = MathF.Atan2(sinX, cosX) * 180f / MathF.PI;

            float sinY = 2f * (q.W * q.Y - q.Z * q.X);
            float pitch = Math.Abs(sinY) >= 1f
                ? MathF.CopySign(90f, sinY)
                : MathF.Asin(sinY) * 180f / MathF.PI;

            float sinZ = 2f * (q.W * q.Z + q.X * q.Y);
            float cosZ = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(sinZ, cosZ) * 180f / MathF.PI;

            return new Vector3(pitch, yaw, roll);
        }

        // ── Texture Selection ──────────────────────────────────────────────

        private void OnSelectTextureClick(object sender, RoutedEventArgs e)
        {
            if (_inspectedObject == null) return;
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".dds");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            _ = Task.Run(async () =>
            {
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        _inspectedObject.TextureAssetPath = file.Path;
                        TextureAssetPathBox.Text = file.Path;
                    });
                }
            });
        }

        // ── Public API ─────────────────────────────────────────────────────

        /// <summary>Set transform values programmatically.</summary>
        public void SetTransform(Vector3 position, Vector3 rotationEuler, Vector3 scale)
        {
            _updatingUI = true;
            PosX.Value = position.X;
            PosY.Value = position.Y;
            PosZ.Value = position.Z;
            RotX.Value = rotationEuler.X;
            RotY.Value = rotationEuler.Y;
            RotZ.Value = rotationEuler.Z;
            ScaleX.Value = scale.X;
            ScaleY.Value = scale.Y;
            ScaleZ.Value = scale.Z;
            _updatingUI = false;
        }

        /// <summary>Refresh the inspector from the current selection.</summary>
        public void Refresh()
        {
            var systems = App.Systems;
            if (systems != null)
                InspectObject(systems.Selection.Active);
        }

        // ── Events ─────────────────────────────────────────────────────────

        public event EventHandler<TransformChangedEventArgs>? TransformChanged;
        public event EventHandler<ComponentEventArgs>? ComponentAdded;
    }

    // ── Event Args ─────────────────────────────────────────────────────────

    public class TransformChangedEventArgs : EventArgs
    {
        public System.Numerics.Vector3 Position { get; set; }
        public System.Numerics.Vector3 Rotation { get; set; }
        public System.Numerics.Vector3 Scale { get; set; }
    }

    public class ComponentEventArgs : EventArgs
    {
        public string ComponentType { get; }
        public ComponentEventArgs(string componentType) => ComponentType = componentType;
    }
}
