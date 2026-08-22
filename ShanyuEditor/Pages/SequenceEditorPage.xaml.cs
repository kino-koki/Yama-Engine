using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace ShanyuEditor.Pages
{
    /// <summary>
    /// Sequence Editor page for ShanyuEditor.
    /// Provides timeline-based animation sequencing with tracks and keyframes.
    /// </summary>
    public sealed partial class SequenceEditorPage : Page
    {
        // ── ViewModel ──────────────────────────────────────────────────────

        public enum TrackType
        {
            Transform,
            Animation,
            Audio,
            Camera,
            Event,
            Material,
            Light
        }

        public class TrackViewModel : INotifyPropertyChanged
        {
            private string _trackName = "New Track";
            private TrackType _trackType;
            private bool _isVisible = true;
            private bool _isMuted;
            private bool _isLocked;
            private ObservableCollection<KeyframeViewModel> _keyframes = new();

            public string TrackName { get => _trackName; set { _trackName = value; OnPropertyChanged(); } }
            public TrackType TrackType { get => _trackType; set { _trackType = value; OnPropertyChanged(); OnPropertyChanged(nameof(TrackColor)); } }
            public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(); } }
            public bool IsMuted { get => _isMuted; set { _isMuted = value; OnPropertyChanged(); } }
            public bool IsLocked { get => _isLocked; set { _isLocked = value; OnPropertyChanged(); } }
            public ObservableCollection<KeyframeViewModel> Keyframes { get => _keyframes; set { _keyframes = value; OnPropertyChanged(); } }

            public Color TrackColor => TrackType switch
            {
                TrackType.Transform => Color.FromArgb(0xFF, 0x4A, 0x90, 0xE2),
                TrackType.Animation => Color.FromArgb(0xFF, 0x66, 0xCC, 0x66),
                TrackType.Audio => Color.FromArgb(0xFF, 0xCC, 0xCC, 0x44),
                TrackType.Camera => Color.FromArgb(0xFF, 0xCC, 0x66, 0x66),
                TrackType.Event => Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC),
                TrackType.Material => Color.FromArgb(0xFF, 0x66, 0xCC, 0xCC),
                TrackType.Light => Color.FromArgb(0xFF, 0xFF, 0xCC, 0x44),
                _ => Color.FromArgb(0xFF, 0x4A, 0x90, 0xE2)
            };

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public class KeyframeViewModel : INotifyPropertyChanged
        {
            private int _frame = 0;
            private string _value = "0";
            private bool _isSelected;

            public int Frame { get => _frame; set { _frame = value; OnPropertyChanged(); } }
            public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
            public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ── Data ────────────────────────────────────────────────────────────

        public ObservableCollection<TrackViewModel> Tracks { get; } = new();

        private double _pixelsPerFrame = 10.0;
        private int _totalFrames = 300;
        private int _currentFrame;
        private bool _isPlaying;

        private const double TrackHeight = 36;
        private const double TrackPadding = 2;

        // ── Constructor ─────────────────────────────────────────────────────

        public SequenceEditorPage()
        {
            this.InitializeComponent();
            TrackListView.ItemsSource = Tracks;
            AddDefaultTracks();
            DrawTimeRuler();
            DrawTimeline();
            UpdateTimecode();
            Tracks.CollectionChanged += (_, _) => DrawTimeline();
        }

        private void AddDefaultTracks()
        {
            // Add some default tracks for demonstration
            var transformTrack = new TrackViewModel { TrackName = "Actor Transform", TrackType = TrackType.Transform };
            transformTrack.Keyframes.Add(new KeyframeViewModel { Frame = 0, Value = "0,0,0" });
            transformTrack.Keyframes.Add(new KeyframeViewModel { Frame = 30, Value = "100,0,0" });
            transformTrack.Keyframes.Add(new KeyframeViewModel { Frame = 60, Value = "100,50,0" });
            transformTrack.Keyframes.Add(new KeyframeViewModel { Frame = 90, Value = "0,50,0" });
            transformTrack.Keyframes.Add(new KeyframeViewModel { Frame = 120, Value = "0,0,0" });
            Tracks.Add(transformTrack);

            var animTrack = new TrackViewModel { TrackName = "Run Animation", TrackType = TrackType.Animation };
            animTrack.Keyframes.Add(new KeyframeViewModel { Frame = 0, Value = "Start" });
            animTrack.Keyframes.Add(new KeyframeViewModel { Frame = 120, Value = "End" });
            Tracks.Add(animTrack);

            var cameraTrack = new TrackViewModel { TrackName = "Camera Move", TrackType = TrackType.Camera };
            cameraTrack.Keyframes.Add(new KeyframeViewModel { Frame = 0, Value = "0,5,10" });
            cameraTrack.Keyframes.Add(new KeyframeViewModel { Frame = 60, Value = "5,3,8" });
            cameraTrack.Keyframes.Add(new KeyframeViewModel { Frame = 120, Value = "10,5,6" });
            Tracks.Add(cameraTrack);

            var audioTrack = new TrackViewModel { TrackName = "Background Music", TrackType = TrackType.Audio };
            audioTrack.Keyframes.Add(new KeyframeViewModel { Frame = 0, Value = "Play" });
            audioTrack.Keyframes.Add(new KeyframeViewModel { Frame = 240, Value = "FadeOut" });
            Tracks.Add(audioTrack);

            var eventTrack = new TrackViewModel { TrackName = "Game Events", TrackType = TrackType.Event };
            eventTrack.Keyframes.Add(new KeyframeViewModel { Frame = 45, Value = "TriggerDoor" });
            eventTrack.Keyframes.Add(new KeyframeViewModel { Frame = 90, Value = "SpawnEnemy" });
            eventTrack.Keyframes.Add(new KeyframeViewModel { Frame = 150, Value = "PlaySound" });
            Tracks.Add(eventTrack);
        }

        // ── Drawing ────────────────────────────────────────────────────────

        private void DrawTimeRuler()
        {
            TimeRulerCanvas.Children.Clear();
            var totalWidth = _totalFrames * _pixelsPerFrame;

            // Draw tick marks every 10 frames, labels every 30 frames
            for (var f = 0; f <= _totalFrames; f++)
            {
                var x = f * _pixelsPerFrame;

                if (f % 10 == 0)
                {
                    var line = new Line
                    {
                        X1 = x, Y1 = f % 30 == 0 ? 6 : 14,
                        X2 = x, Y2 = 24,
                        Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0x5A, 0x60)),
                        StrokeThickness = f % 30 == 0 ? 1.5 : 0.75
                    };
                    TimeRulerCanvas.Children.Add(line);
                }

                if (f % 30 == 0)
                {
                    var text = new TextBlock
                    {
                        Text = f.ToString(),
                        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x8A, 0x96)),
                        FontSize = 9,
                        Margin = new Thickness(x + 2, 2, 0, 0)
                    };
                    TimeRulerCanvas.Children.Add(text);
                }
            }

            TimeRulerCanvas.Width = totalWidth;
        }

        private void DrawTimeline()
        {
            TimelineCanvas.Children.Clear();
            var totalWidth = _totalFrames * _pixelsPerFrame;
            var totalHeight = Tracks.Count * (TrackHeight + TrackPadding);

            // Draw track row backgrounds and keyframes
            for (int i = 0; i < Tracks.Count; i++)
            {
                var track = Tracks[i];
                var y = i * (TrackHeight + TrackPadding);

                // Row background
                var rowBg = new Rectangle
                {
                    Width = totalWidth,
                    Height = TrackHeight,
                    Fill = new SolidColorBrush(i % 2 == 0
                        ? Color.FromArgb(0xFF, 0x12, 0x12, 0x16)
                        : Color.FromArgb(0xFF, 0x15, 0x15, 0x1A)),
                    Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x30)),
                    StrokeThickness = 0.5
                };
                Canvas.SetTop(rowBg, y);
                TimelineCanvas.Children.Add(rowBg);

                // Draw keyframes for this track
                foreach (var keyframe in track.Keyframes)
                {
                    var kfX = keyframe.Frame * _pixelsPerFrame;
                    var kfY = y + TrackHeight / 2;

                    // Keyframe diamond
                    var diamond = new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(kfX, kfY - 6),
                            new Point(kfX + 6, kfY),
                            new Point(kfX, kfY + 6),
                            new Point(kfX - 6, kfY)
                        },
                        Fill = new SolidColorBrush(track.TrackColor),
                        Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE8)),
                        StrokeThickness = 1
                    };
                    TimelineCanvas.Children.Add(diamond);

                    // Keyframe connecting line (simplified visual)
                    if (kfX + _pixelsPerFrame * 10 < totalWidth)
                    {
                        var rangeRect = new Rectangle
                        {
                            Width = _pixelsPerFrame * 10,
                            Height = 4,
                            Fill = new SolidColorBrush(track.TrackColor)
                        };
                        rangeRect.Opacity = 0.3;
                        Canvas.SetLeft(rangeRect, kfX);
                        Canvas.SetTop(rangeRect, kfY - 2);
                        TimelineCanvas.Children.Add(rangeRect);
                    }
                }
            }

            // Update playhead range
            PlayheadLine.Y2 = Math.Max(totalHeight, 200);
            TimelineCanvas.Width = totalWidth;
            TimelineCanvas.Height = totalHeight;

            // Sync vertical scrolling
            var scrollViewer = TimelineScrollViewer;
            scrollViewer.Width = totalWidth;
            scrollViewer.Height = totalHeight;
        }

        // ── Timecode ───────────────────────────────────────────────────────

        private void UpdateTimecode()
        {
            var frame = _currentFrame;
            var fps = 30;
            var totalSeconds = frame / fps;
            var hours = totalSeconds / 3600;
            var minutes = (totalSeconds % 3600) / 60;
            var seconds = totalSeconds % 60;
            var frames = frame % fps;
            TimecodeDisplay.Text = $"{hours:D2}:{minutes:D2}:{seconds:D2}:{frames:D2}";
            FrameNumberBox.Value = _currentFrame;
        }

        private void MovePlayhead()
        {
            var x = _currentFrame * _pixelsPerFrame;
            PlayheadLine.X1 = x;
            PlayheadLine.X2 = x;
            PlayheadHead.Points = new PointCollection
            {
                new Point(x - 6, 0),
                new Point(x + 6, 0),
                new Point(x, 8)
            };
        }

        // ── Transport Controls ───────────────────────────────────────────

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            _isPlaying = true;
            PlaybackStateChanged?.Invoke(this, new PlaybackEventArgs(true));
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            PlaybackStateChanged?.Invoke(this, new PlaybackEventArgs(false));
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            _currentFrame = 0;
            MovePlayhead();
            UpdateTimecode();
            PlaybackStateChanged?.Invoke(this, new PlaybackEventArgs(false, 0));
        }

        private void PrevFrame_Click(object sender, RoutedEventArgs e)
        {
            _currentFrame = Math.Max(0, _currentFrame - 1);
            MovePlayhead();
            UpdateTimecode();
            FrameChanged?.Invoke(this, new FrameChangedEventArgs(_currentFrame));
        }

        private void NextFrame_Click(object sender, RoutedEventArgs e)
        {
            _currentFrame = Math.Min(_totalFrames - 1, _currentFrame + 1);
            MovePlayhead();
            UpdateTimecode();
            FrameChanged?.Invoke(this, new FrameChangedEventArgs(_currentFrame));
        }

        private void FrameNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            _currentFrame = Math.Clamp((int)args.NewValue, 0, _totalFrames - 1);
            MovePlayhead();
            UpdateTimecode();
        }

        // ── Track Management ─────────────────────────────────────────────

        private void AddTrack_Click(object sender, RoutedEventArgs e)
        {
            var newTrack = new TrackViewModel
            {
                TrackName = $"Track {Tracks.Count + 1}",
                TrackType = TrackType.Transform
            };
            Tracks.Add(newTrack);
            DrawTimeline();
        }

        // ── Events ─────────────────────────────────────────────────────────

        public event EventHandler<PlaybackEventArgs>? PlaybackStateChanged;
        public event EventHandler<FrameChangedEventArgs>? FrameChanged;
    }

    // ── Event Args ─────────────────────────────────────────────────────────

    public class PlaybackEventArgs : EventArgs
    {
        public bool IsPlaying { get; }
        public int? Frame { get; }
        public PlaybackEventArgs(bool isPlaying, int? frame = null)
        {
            IsPlaying = isPlaying;
            Frame = frame;
        }
    }

    public class FrameChangedEventArgs : EventArgs
    {
        public int Frame { get; }
        public FrameChangedEventArgs(int frame) => Frame = frame;
    }
}
