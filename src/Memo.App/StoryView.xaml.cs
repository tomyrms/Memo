using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Memo.App;

public sealed partial class StoryView : UserControl
{
    private readonly List<Border> _fills = new();
    private readonly List<Grid> _segments = new();
    private IReadOnlyList<MemoryCard> _items = Array.Empty<MemoryCard>();
    private int _index;
    private bool _paused;
    private bool _muted;
    private bool _overlayVisible = true;
    private DispatcherQueueTimer? _timer;
    private DateTimeOffset _photoStarted;
    private TimeSpan _photoElapsed;
    private TimeSpan _photoDuration = TimeSpan.FromSeconds(4);

    public event EventHandler? Closed;

    public StoryView()
    {
        InitializeComponent();
        Stage.Ended += (_, _) => Next();
    }

    public void Start(IReadOnlyList<MemoryCard> items, int index)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        _paused = false;
        BuildProgress();
        Focus(FocusState.Programmatic);
        ShowCurrent();
    }

    private void BuildProgress()
    {
        ProgressPanel.Children.Clear();
        ProgressPanel.ColumnDefinitions.Clear();
        _fills.Clear();
        _segments.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            ProgressPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var segment = new Grid { Height = 3, Margin = new Thickness(0, 0, 4, 0) };
            segment.Children.Add(new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(80, 255, 255, 255)),
                CornerRadius = new CornerRadius(2)
            });
            var fill = new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 239, 231)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
                CornerRadius = new CornerRadius(2)
            };
            segment.Children.Add(fill);
            segment.SizeChanged += (_, _) => UpdateProgress();
            Grid.SetColumn(segment, i);
            ProgressPanel.Children.Add(segment);
            _segments.Add(segment);
            _fills.Add(fill);
        }
    }

    private void ShowCurrent()
    {
        if (_index < 0 || _index >= _items.Count)
        {
            Close();
            return;
        }

        var card = _items[_index];
        Counter.Text = $"{_index + 1} / {_items.Count}";
        CaptionTitle.Text = AppState.Current.FormatDate(card);
        CaptionSubtitle.Text = BuildSubtitle(card);
        _overlayVisible = true;

        var overlayPath = AppState.Current.OverlayFor(card.Id);
        Stage.SetOverlay(ResolvePath(overlayPath));
        Stage.SetOverlayVisible(true);
        OverlayButton.IsEnabled = overlayPath is not null;

        _paused = false;
        _photoElapsed = TimeSpan.Zero;
        _photoStarted = DateTimeOffset.Now;
        _photoDuration = TimeSpan.FromSeconds(Math.Max(1, AppState.Current.Settings.PhotoDurationSeconds));
        BigPlay.Visibility = Visibility.Collapsed;
        UpdatePlayIcon();

        if (card.Kind == MediaKind.Video && AppState.Current.MediaFor(card.Id) is { } videoPath && ResolvePath(videoPath) is { } resolvedVideo)
        {
            Stage.PlayVideo(resolvedVideo);
        }
        else
        {
            var photoPath = ResolvePath(AppState.Current.MediaFor(card.Id)) ?? ResolvePath(AppState.Current.ThumbnailFor(card.Id));
            if (photoPath is null)
            {
                Stage.Clear();
            }
            else
            {
                Stage.ShowPhoto(photoPath);
            }
        }

        EnsureTimer();
    }

    private static string BuildSubtitle(MemoryCard card)
    {
        var parts = new List<string> { card.Kind == MediaKind.Video ? "Vidéo" : "Photo" };
        if (card.DurationMs is { } ms && ms > 0)
        {
            parts.Add(TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss"));
        }
        if (!string.IsNullOrWhiteSpace(card.LocationLabel))
        {
            parts.Add(card.LocationLabel);
        }
        if (card.OverlayCount > 0)
        {
            parts.Add("calque disponible");
        }
        if (card.LocalDate is null)
        {
            parts.Add("date inconnue");
        }
        return string.Join(" · ", parts);
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
        {
            _timer.Start();
            return;
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        if (_paused || _items.Count == 0)
        {
            return;
        }

        var card = _items[_index];
        if (card.Kind == MediaKind.Video && Stage.IsVideo)
        {
            UpdateProgress(Stage.Position);
            return;
        }

        var elapsed = (_photoElapsed + (DateTimeOffset.Now - _photoStarted)).TotalSeconds;
        var progress = Math.Clamp(elapsed / _photoDuration.TotalSeconds, 0, 1);
        UpdateProgress(progress);
        if (progress >= 1)
        {
            Next();
        }
    }

    private void UpdateProgress(double? current = null)
    {
        for (var i = 0; i < _fills.Count; i++)
        {
            var width = _segments[i].ActualWidth;
            if (width <= 0)
            {
                continue;
            }
            _fills[i].Width = i < _index ? width : i == _index ? width * (current ?? 0) : 0;
        }
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (path.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path["ms-appx:///".Length..].Replace('/', '\\');
            return System.IO.Path.Combine(AppContext.BaseDirectory, relative);
        }
        return File.Exists(path) ? path : null;
    }

    private void Next()
    {
        if (_index + 1 >= _items.Count)
        {
            Close();
            return;
        }
        _index++;
        ShowCurrent();
    }

    private void Previous()
    {
        if (_index == 0)
        {
            return;
        }
        _index--;
        ShowCurrent();
    }

    private void OnNext(object sender, RoutedEventArgs e) => Next();
    private void OnPrevious(object sender, RoutedEventArgs e) => Previous();
    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        _paused = !_paused;
        if (_items.Count > 0 && _items[_index].Kind == MediaKind.Video && Stage.IsVideo)
        {
            if (_paused)
            {
                Stage.Pause();
            }
            else
            {
                Stage.Resume();
            }
        }
        else if (_items.Count > 0)
        {
            if (_paused)
            {
                _photoElapsed += DateTimeOffset.Now - _photoStarted;
            }
            else
            {
                _photoStarted = DateTimeOffset.Now;
            }
        }

        UpdatePlayIcon();
        BigPlay.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlayIcon()
    {
        PlayIcon.Glyph = _paused ? "\uE768" : "\uE769";
    }

    private void OnToggleOverlay(object sender, RoutedEventArgs e)
    {
        _overlayVisible = !_overlayVisible;
        Stage.SetOverlayVisible(_overlayVisible);
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        Stage.Mute = _muted;
        MuteButton.Opacity = _muted ? 0.55 : 1;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        _timer?.Stop();
        Stage.StopVideo();
        Stage.Clear();
        Closed?.Invoke(this, EventArgs.Empty);
        Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space:
                TogglePause();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                Next();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                Previous();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.O:
                OnToggleOverlay(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.M:
                OnToggleMute(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }
}
