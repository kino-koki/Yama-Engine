using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ShanyuEditor.Pages
{
    /// <summary>
    /// Material Editor page for ShanyuEditor.
    /// Provides PBR material parameter editing and a simplified node graph visualization.
    /// </summary>
    public sealed partial class MaterialEditorPage : Page
    {
        // ── Constructor ─────────────────────────────────────────────────────

        public MaterialEditorPage()
        {
            this.InitializeComponent();
        }

        // ── Toolbar Actions ──────────────────────────────────────────────────

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            MaterialSaved?.Invoke(this, EventArgs.Empty);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            MaterialApplied?.Invoke(this, EventArgs.Empty);
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            PreviewRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            // Reset all values to defaults
            ShaderModelCombo.SelectedIndex = 0;
            BlendModeCombo.SelectedIndex = 0;
            ShadingCombo.SelectedIndex = 0;
            TwoSidedCheck.IsChecked = false;
            MetallicValue.Value = 0;
            RoughnessValue.Value = 0.5;
            OpacityValue.Value = 1;
            EmissiveIntensity.Value = 0;
            TilingX.Value = 1;
            TilingY.Value = 1;
            OffsetX.Value = 0;
            OffsetY.Value = 0;
            SRGBCheck.IsChecked = true;
            AlphaMaskCheck.IsChecked = false;

            MaterialReset?.Invoke(this, EventArgs.Empty);
        }

        // ── Texture Browse ────────────────────────────────────────────────────

        private void BrowseTexture_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string channel)
            {
                TextureBrowseRequested?.Invoke(this, new TextureBrowseEventArgs(channel));
            }
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Set a texture slot display name.</summary>
        public void SetTextureSlot(string channel, string textureName)
        {
            switch (channel)
            {
                case "BaseColor": break; // color preview
                case "Metallic": MetallicTex.Text = textureName; break;
                case "Roughness": RoughnessTex.Text = textureName; break;
                case "Normal": NormalTex.Text = textureName; break;
                case "AO": AOTex.Text = textureName; break;
            }
        }

        /// <summary>Set texture info details.</summary>
        public void SetTextureInfo(string resolution, string format, string memory)
        {
            TexInfoSize.Text = $"Resolution: {resolution}";
            TexInfoFormat.Text = $"Format: {format}";
            TexInfoMemory.Text = $"Memory: {memory}";
        }

        // ── Events ────────────────────────────────────────────────────────────

        public event EventHandler? MaterialSaved;
        public event EventHandler? MaterialApplied;
        public event EventHandler? PreviewRequested;
        public event EventHandler? MaterialReset;
        public event EventHandler<TextureBrowseEventArgs>? TextureBrowseRequested;
    }

    public class TextureBrowseEventArgs : EventArgs
    {
        public string Channel { get; }
        public TextureBrowseEventArgs(string channel) => Channel = channel;
    }
}
