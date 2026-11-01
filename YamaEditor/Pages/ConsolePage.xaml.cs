using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace YamaEditor.Pages
{
    /// <summary>
    /// Console page for YamaEditor.
    /// Displays log messages with filtering, search, and context menu support.
    /// </summary>
    public sealed partial class ConsolePage : Page
    {
        // ── ViewModel ──────────────────────────────────────────────────────

        public enum LogLevel
        {
            Log,
            Warning,
            Error,
            Info
        }

        public class LogEntry : INotifyPropertyChanged
        {
            private LogLevel _level;
            private string _timestamp = string.Empty;
            private string _category = string.Empty;
            private string _message = string.Empty;

            public LogLevel Level
            {
                get => _level;
                set
                {
                    _level = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IconColor));
                }
            }

            public string Timestamp { get => _timestamp; set { _timestamp = value; OnPropertyChanged(); } }
            public string Category { get => _category; set { _category = value; OnPropertyChanged(); } }
            public string Message { get => _message; set { _message = value; OnPropertyChanged(); } }

            public Windows.UI.Color IconColor => Level switch
            {
                LogLevel.Log => Windows.UI.Color.FromArgb(0xFF, 0x66, 0xCC, 0x66),
                LogLevel.Warning => Windows.UI.Color.FromArgb(0xFF, 0xCC, 0xCC, 0x44),
                LogLevel.Error => Windows.UI.Color.FromArgb(0xFF, 0xCC, 0x66, 0x66),
                LogLevel.Info => Windows.UI.Color.FromArgb(0xFF, 0x66, 0x99, 0xCC),
                _ => Windows.UI.Color.FromArgb(0xFF, 0xB0, 0xB0, 0xB8)
            };

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ── Data ────────────────────────────────────────────────────────────

        private readonly ObservableCollection<LogEntry> _allEntries = new();
        private readonly ObservableCollection<LogEntry> _filteredEntries = new();

        private bool _filterLog = true;
        private bool _filterWarning = true;
        private bool _filterError = true;
        private bool _filterInfo = true;
        private string _searchText = string.Empty;

        private int _totalLog;
        private int _totalWarning;
        private int _totalError;
        private int _totalInfo;

        private LogEntry? _lastRightTappedEntry;

        // ── Constructor ─────────────────────────────────────────────────────

        public ConsolePage()
        {
            this.InitializeComponent();
            LogListView.ItemsSource = _filteredEntries;
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void LogMessage(LogLevel level, string category, string message)
        {
            var entry = new LogEntry
            {
                Level = level,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                Category = category,
                Message = message
            };

            _allEntries.Add(entry);

            switch (level)
            {
                case LogLevel.Log: _totalLog++; break;
                case LogLevel.Warning: _totalWarning++; break;
                case LogLevel.Error: _totalError++; break;
                case LogLevel.Info: _totalInfo++; break;
            }

            if (IsEntryVisible(entry))
            {
                _filteredEntries.Add(entry);
            }

            UpdateCounts();
            AutoScrollToLatest();
        }

        public void ClearAll()
        {
            _allEntries.Clear();
            _filteredEntries.Clear();
            _totalLog = 0;
            _totalWarning = 0;
            _totalError = 0;
            _totalInfo = 0;
            UpdateCounts();
        }

        // ── Filtering ────────────────────────────────────────────────────────

        private void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb)
            {
                switch (tb.Name)
                {
                    case "FilterLog": _filterLog = tb.IsChecked == true; break;
                    case "FilterWarning": _filterWarning = tb.IsChecked == true; break;
                    case "FilterError": _filterError = tb.IsChecked == true; break;
                    case "FilterInfo": _filterInfo = tb.IsChecked == true; break;
                }
                RebuildFilteredEntries();
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = SearchBox.Text.Trim();
            RebuildFilteredEntries();
        }

        private void RebuildFilteredEntries()
        {
            _filteredEntries.Clear();
            foreach (var entry in _allEntries)
            {
                if (IsEntryVisible(entry))
                    _filteredEntries.Add(entry);
            }
        }

        private bool IsEntryVisible(LogEntry entry)
        {
            bool levelVisible = entry.Level switch
            {
                LogLevel.Log => _filterLog,
                LogLevel.Warning => _filterWarning,
                LogLevel.Error => _filterError,
                LogLevel.Info => _filterInfo,
                _ => true
            };

            if (!levelVisible) return false;
            if (!string.IsNullOrEmpty(_searchText))
            {
                return entry.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                       entry.Category.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        private void UpdateCounts()
        {
            LogCountValue.Text = _totalLog.ToString();
            WarningCountValue.Text = _totalWarning.ToString();
            ErrorCountValue.Text = _totalError.ToString();
            InfoCountValue.Text = _totalInfo.ToString();
        }

        private void AutoScrollToLatest()
        {
            if (_filteredEntries.Count > 0)
            {
                LogListView.ScrollIntoView(_filteredEntries[^1]);
            }
        }

        // ── Clear Button ───────────────────────────────────────────────────

        private void Clear_Click(object sender, RoutedEventArgs e) => ClearAll();

        // ── Double Click: Open File ──────────────────────────────────────────

        private void LogListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (LogListView.SelectedItem is LogEntry entry)
            {
                var filePath = ExtractFilePath(entry.Message);
                if (!string.IsNullOrEmpty(filePath))
                {
                    FileOpenRequested?.Invoke(this, new FileOpenEventArgs(filePath));
                }
            }
        }

        private static string? ExtractFilePath(string message)
        {
            var patterns = new[]
            {
                @"at (.+?):(\d+)",
                @"in (.+?):(\d+)",
            };
            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(message, pattern);
                if (match.Success) return match.Groups[1].Value.Trim();
            }
            return null;
        }

        // ── Right Click Context Menu ─────────────────────────────────────────

        private void LogListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (LogListView.SelectedItem is LogEntry entry)
            {
                _lastRightTappedEntry = entry;
            }

            var menuFlyout = new MenuFlyout();
            menuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Copy Message",
                Command = new ConsoleRelayCommand(_ => CopySelectedMessage())
            });
            menuFlyout.Items.Add(new MenuFlyoutSeparator());
            menuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = $"Filter by \"{_lastRightTappedEntry?.Category ?? "Unknown"}\"",
                Command = new ConsoleRelayCommand(_ => FilterByCategory())
            });
            menuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Clear Same Type",
                Command = new ConsoleRelayCommand(_ => ClearSameLevel())
            });
            menuFlyout.Items.Add(new MenuFlyoutSeparator());
            menuFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Clear All",
                Command = new ConsoleRelayCommand(_ => ClearAll())
            });

            var transform = e.OriginalSource as UIElement;
            menuFlyout.ShowAt(transform ?? LogListView, e.GetPosition(transform ?? LogListView));
        }

        private void CopySelectedMessage()
        {
            if (_lastRightTappedEntry != null)
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(_lastRightTappedEntry.Message);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            }
        }

        private void FilterByCategory()
        {
            if (_lastRightTappedEntry != null)
            {
                SearchBox.Text = _lastRightTappedEntry.Category;
            }
        }

        private void ClearSameLevel()
        {
            if (_lastRightTappedEntry != null)
            {
                var targetLevel = _lastRightTappedEntry.Level;
                var toRemove = new System.Collections.Generic.List<LogEntry>();
                foreach (var entry in _allEntries)
                {
                    if (entry.Level == targetLevel) toRemove.Add(entry);
                }
                foreach (var entry in toRemove)
                {
                    _allEntries.Remove(entry);
                    _filteredEntries.Remove(entry);
                }

                switch (targetLevel)
                {
                    case LogLevel.Log: _totalLog = 0; break;
                    case LogLevel.Warning: _totalWarning = 0; break;
                    case LogLevel.Error: _totalError = 0; break;
                    case LogLevel.Info: _totalInfo = 0; break;
                }
                UpdateCounts();
            }
        }

        // ── Events ─────────────────────────────────────────────────────────

        public event EventHandler<FileOpenEventArgs>? FileOpenRequested;
    }

    internal class ConsoleRelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action<object?> _execute;
        public ConsoleRelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged;
    }

    public class FileOpenEventArgs : EventArgs
    {
        public string FilePath { get; }
        public FileOpenEventArgs(string filePath) => FilePath = filePath;
    }
}
