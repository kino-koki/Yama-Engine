using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YamaEditor.Dialogs
{
    /// <summary>
    /// Settings dialog for YamaEditor.
    /// Provides rendering, editor, and keyboard shortcut configuration.
    /// </summary>
    public sealed partial class SettingsDialog : ContentDialog
    {
        // ── Shortcut ViewModel ──────────────────────────────────────────────

        public class ShortcutEntry
        {
            public string Action { get; set; } = string.Empty;
            public string Shortcut { get; set; } = string.Empty;
        }

        // ── Properties ────────────────────────────────────────────────────

        // Rendering
        public int ResolutionWidth => (int)ResWidth.Value;
        public int ResolutionHeight => (int)ResHeight.Value;
        public string QualityPresetName => (QualityPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "High";
        public string AAModeName => (AAMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TAA";
        public bool ShadowsEnabled => EnableShadows.IsChecked == true;
        public string ShadowQualityName => (ShadowQuality.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "High (2048)";
        public double ShadowDistanceValue => ShadowDistance.Value;
        public int ShadowCascadeCountValue => (int)ShadowCascadeCount.Value;
        public bool BloomEnabled => EnableBloom.IsChecked == true;
        public bool ToneMappingEnabled => EnableToneMapping.IsChecked == true;
        public bool SSREnabled => EnableSSR.IsChecked == true;
        public bool SSAOEnabled => EnableSSAO.IsChecked == true;
        public bool DOFEnabled => EnableDOF.IsChecked == true;
        public bool MotionBlurEnabled => EnableMotionBlur.IsChecked == true;
        public bool VignetteEnabled => EnableVignette.IsChecked == true;
        public bool FilmGrainEnabled => EnableFilmGrain.IsChecked == true;
        public bool RTReflectionsEnabled => EnableRTReflections.IsChecked == true;
        public bool RTShadowsEnabled => EnableRTShadows.IsChecked == true;
        public bool RTGIEnabled => EnableRTGI.IsChecked == true;

        // Editor
        public bool AutoSaveEnabled => EnableAutoSave.IsChecked == true;
        public int AutoSaveIntervalSeconds => (int)AutoSaveInterval.Value;
        public int UndoHistoryLimitValue => (int)UndoHistoryLimit.Value;
        public bool GridVisible => ShowGrid.IsChecked == true;
        public double GridSizeValue => GridSize.Value;
        public double GridSpacingValue => GridSpacing.Value;

        // ── Constructor ─────────────────────────────────────────────────────

        public SettingsDialog()
        {
            this.InitializeComponent();
            PopulateShortcuts();
            this.XamlRoot = App.Window?.Content?.XamlRoot;
        }

        public SettingsDialog(XamlRoot xamlRoot)
        {
            this.InitializeComponent();
            PopulateShortcuts();
            this.XamlRoot = xamlRoot;
        }

        private void PopulateShortcuts()
        {
            var shortcuts = new ObservableCollection<ShortcutEntry>
            {
                new() { Action = "New Scene", Shortcut = "Ctrl+N" },
                new() { Action = "Open Scene", Shortcut = "Ctrl+O" },
                new() { Action = "Save Scene", Shortcut = "Ctrl+S" },
                new() { Action = "Save As...", Shortcut = "Ctrl+Shift+S" },
                new() { Action = "Undo", Shortcut = "Ctrl+Z" },
                new() { Action = "Redo", Shortcut = "Ctrl+Y" },
                new() { Action = "Delete", Shortcut = "Delete" },
                new() { Action = "Duplicate", Shortcut = "Ctrl+D" },
                new() { Action = "Select All", Shortcut = "Ctrl+A" },
                new() { Action = "Deselect All", Shortcut = "Ctrl+Shift+A" },
                new() { Action = "Translate Mode", Shortcut = "W" },
                new() { Action = "Rotate Mode", Shortcut = "E" },
                new() { Action = "Scale Mode", Shortcut = "R" },
                new() { Action = "Play", Shortcut = "F5" },
                new() { Action = "Pause", Shortcut = "F6" },
                new() { Action = "Stop", Shortcut = "F7" },
                new() { Action = "Toggle Console", Shortcut = "Ctrl+`" },
                new() { Action = "Toggle Inspector", Shortcut = "Ctrl+I" },
                new() { Action = "Toggle Hierarchy", Shortcut = "Ctrl+H" },
                new() { Action = "Focus Selected", Shortcut = "F" },
                new() { Action = "Frame All", Shortcut = "Shift+F" },
                new() { Action = "Snap to Grid", Shortcut = "Ctrl+G" },
                new() { Action = "Toggle Grid", Shortcut = "G" },
                new() { Action = "Quick Save", Shortcut = "Ctrl+Shift+S" },
                new() { Action = "New Project", Shortcut = "Ctrl+Shift+N" },
                new() { Action = "Open Material Editor", Shortcut = "Ctrl+M" },
                new() { Action = "Open Sequence Editor", Shortcut = "Ctrl+L" },
                new() { Action = "Settings", Shortcut = "Ctrl+," },
                new() { Action = "Search Commands", Shortcut = "Ctrl+P" },
            };
            ShortcutsListView.ItemsSource = shortcuts;
        }

        // ── Primary Button (Apply) ──────────────────────────────────────────

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            SettingsApplied?.Invoke(this, new SettingsEventArgs(this));
        }

        // ── Events ─────────────────────────────────────────────────────────

        public event EventHandler<SettingsEventArgs>? SettingsApplied;
    }

    // ── Event Args ─────────────────────────────────────────────────────────

    public class SettingsEventArgs : EventArgs
    {
        public SettingsDialog Settings { get; }
        public SettingsEventArgs(SettingsDialog settings) => Settings = settings;
    }
}
