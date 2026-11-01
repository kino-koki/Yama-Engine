using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace YamaEditor.Dialogs
{
    /// <summary>
    /// New Project dialog for YamaEditor.
    /// Allows creating new projects with templates and feature configuration.
    /// </summary>
    public sealed partial class NewProjectDialog : ContentDialog
    {
        // ── Properties ────────────────────────────────────────────────────

        public string ProjectName => ProjectNameBox.Text.Trim();
        public string ProjectPath => ProjectPathBox.Text.Trim();
        public string SelectedTemplate => GetSelectedTemplateName();
        public bool EnablePhysicsFeature => EnablePhysics.IsChecked == true;
        public bool EnableAudioFeature => EnableAudio.IsChecked == true;
        public bool EnableNetworkingFeature => EnableNetworking.IsChecked == true;
        public bool EnableLocalizationFeature => EnableLocalization.IsChecked == true;
        public bool EnableStreamingFeature => EnableStreaming.IsChecked == true;
        public bool EnableFlowVFXFeature => EnableFlowVFX.IsChecked == true;

        // ── Constructor ─────────────────────────────────────────────────────

        public NewProjectDialog()
        {
            this.InitializeComponent();
            this.XamlRoot = App.Window?.Content?.XamlRoot;
        }

        public NewProjectDialog(XamlRoot xamlRoot)
        {
            this.InitializeComponent();
            this.XamlRoot = xamlRoot;
        }

        // ── Browse Path ───────────────────────────────────────────────────

        private async void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            WinRT.InitializeWithWindow.Initialize(folderPicker, hwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                ProjectPathBox.Text = folder.Path;
            }
        }

        // ── Template Selection ─────────────────────────────────────────────

        private string GetSelectedTemplateName()
        {
            return TemplateListView.SelectedItem switch
            {
                _ when TemplateListView.SelectedIndex == 0 => "Blank",
                _ when TemplateListView.SelectedIndex == 1 => "ThirdPerson",
                _ when TemplateListView.SelectedIndex == 2 => "FirstPerson",
                _ when TemplateListView.SelectedIndex == 3 => "2DPlatform",
                _ => "Blank"
            };
        }

        // ── Primary Button (Create) ────────────────────────────────────────

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(ProjectName))
            {
                args.Cancel = true;
                ShowValidationWarning("Project name cannot be empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                args.Cancel = true;
                ShowValidationWarning("Please select a project location.");
                return;
            }

            // Notify project creation
            ProjectCreated?.Invoke(this, new NewProjectEventArgs(
                ProjectName,
                ProjectPath,
                SelectedTemplate,
                EnablePhysicsFeature,
                EnableAudioFeature,
                EnableNetworkingFeature,
                EnableLocalizationFeature,
                EnableStreamingFeature,
                EnableFlowVFXFeature
            ));
        }

        private async void ShowValidationWarning(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "Validation Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }

        // ── Events ─────────────────────────────────────────────────────────

        public event EventHandler<NewProjectEventArgs>? ProjectCreated;
    }

    // ── Event Args ─────────────────────────────────────────────────────────

    public class NewProjectEventArgs : EventArgs
    {
        public string ProjectName { get; }
        public string ProjectPath { get; }
        public string Template { get; }
        public bool Physics { get; }
        public bool Audio { get; }
        public bool Networking { get; }
        public bool Localization { get; }
        public bool Streaming { get; }
        public bool FlowVFX { get; }

        public NewProjectEventArgs(string projectName, string projectPath, string template,
            bool physics, bool audio, bool networking, bool localization, bool streaming, bool flowvfx)
        {
            ProjectName = projectName;
            ProjectPath = projectPath;
            Template = template;
            Physics = physics;
            Audio = audio;
            Networking = networking;
            Localization = localization;
            Streaming = streaming;
            FlowVFX = flowvfx;
        }
    }
}
