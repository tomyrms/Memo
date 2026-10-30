using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

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
    private bool _initialized;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private DispatcherQueueTimer? _timer;
    private DateTimeOffset _photoStarted;
    private TimeSpan _photoElapsed;
    private TimeSpan _photoDuration = TimeSpan.FromSeconds(4);

    public event EventHandler? Closed;

    public StoryView()
    {
        InitializeComponent();
        Unloaded += (_, _) => DisposePlayer();
    }

    public void Start(IReadOnlyList<MemoryCard> items, int index)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
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
            var seg = new Grid { Height = 3, Margin = new Thickness(0, 0, 4, 0) };
            seg.Children.Add(new Border
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
            seg.Children.Add(fill);
            seg.SizeChanged += (_, _) => UpdateProgress();
            Grid.SetColumn(seg, i);
            ProgressPanel.Children.Add(seg);
            _segments.Add(seg);
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
        OverlayButton.IsEnabled = AppState.Current.OverlayFor(card.Id) is not null;

        StopVideo();
        _photoElapsed = TimeSpan.Zero;
        _photoStarted = DateTimeOffset.Now;
        _photoDuration = TimeSpan.FromSeconds(Math.Max(1, AppState.Current.Settings.PhotoDurationSeconds));

        if (card.Kind == MediaKind.Video && AppState.Current.MediaFor(card.Id) is { } videoPath)
        {
            PlayVideo(videoPath);
        }
        else
        {
            ShowPhoto(card);
        }

        UpdateOverlay(card);
        UpdatePlayIcon();
        EnsureTimer();
    }

    private static string BuildSubtitle(MemoryCard card)
    {
        var parts = new List<string>();
        parts.Add(card.Kind == MediaKind.Video ? "Vidéo" : "Photo");
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

    private void ShowPhoto(MemoryCard card)
    {
        Video.Visibility = Visibility.Collapsed;
        Photo.Visibility = Visibility.Visible;
        var path = AppState.Current.MediaFor(card.Id) ?? AppState.Current.ThumbnailFor(card.Id);
        if (path is null)
        {
            Photo.Source = null;
            return;
        }
        Photo.Source = LoadImage(path, 0);
    }

    private void PlayVideo(string path)
    {
        Photo.Visibility = Visibility.Collapsed;
        Video.Visibility = Visibility.Visible;
        EnsurePlayer();
        if (_player is not null)
        {
            Video.MediaPlayer = _player;
        }

        var resolved = ResolvePath(path);
        if (resolved is null || _libVlc is null || _player is null)
        {
            return;
        }

        _media?.Dispose();
        _media = new Media(_libVlc, new Uri(resolved));
        _player.Play(_media);
        _player.Mute = _muted;
        _player.EndReached -= OnEndReached;
        _player.EndReached += OnEndReached;
    }

    private void EnsurePlayer()
    {
        if (_initialized)
        {
            return;
        }

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet");
        _player = new MediaPlayer(_libVlc);
        Video.MediaPlayer = _player;
        _initialized = true;
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
        if (card.Kind == MediaKind.Video && _player is not null)
        {
            var length = Math.Max(1, _player.Length);
            UpdateProgress(Math.Clamp((double)_player.Time / length, 0, 1));
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
        if (_segments.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _fills.Count; i++)
        {
            var width = _segments[i].ActualWidth;
            if (width <= 0)
            {
                continue;
            }
            if (i < _index)
            {
                _fills[i].Width = width;
            }
            else if (i == _index)
            {
                _fills[i].Width = width * (current ?? 0);
            }
            else
            {
                _fills[i].Width = 0;
            }
        }
    }

    private void UpdateOverlay(MemoryCard card)
    {
        var path = AppState.Current.OverlayFor(card.Id);
        if (path is null || !_overlayVisible)
        {
            OverlayImage.Visibility = Visibility.Collapsed;
            return;
        }

        OverlayImage.Source = LoadImage(path, 0);
        OverlayImage.Visibility = Visibility.Visible;
        LayoutOverlay(card);
    }

    private void LayoutOverlay(MemoryCard card)
    {
        if (OverlayImage.Visibility != Visibility.Visible || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
        {
            return;
        }

        double mediaW = card.Width is > 0 ? card.Width.Value : 3;
        double mediaH = card.Height is > 0 ? card.Height.Value : 4;
        if (card.Status == MemoryStatus.OverlayOnly)
        {
            mediaW = 3;
            mediaH = 4;
        }

        var scale = Math.Min(Stage.ActualWidth / mediaW, Stage.ActualHeight / mediaH);
        var w = mediaW * scale;
        var h = mediaH * scale;
        var x = (Stage.ActualWidth - w) / 2;
        var y = (Stage.ActualHeight - h) / 2;
        System.Diagnostics.Debug.WriteLine($"stage {Stage.ActualWidth}x{Stage.ActualHeight} media {mediaW}x{mediaH} overlay {w}x{h} at {x},{y}");
        Canvas.SetLeft(OverlayImage, x);
        Canvas.SetTop(OverlayImage, y);
        OverlayImage.Width = w;
        OverlayImage.Height = h;
    }

    private static BitmapImage? LoadImage(string path, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolvePath(string path)
    {
        if (path.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path["ms-appx:///".Length..].Replace('/', '\\');
            return System.IO.Path.Combine(AppContext.BaseDirectory, relative);
        }
        return File.Exists(path) ? path : null;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_items.Count == 0)
        {
            return;
        }
        LayoutOverlay(_items[_index]);
        UpdateProgress();
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
        if (_items.Count > 0 && _items[_index].Kind == MediaKind.Video && _player is not null)
        {
            if (_paused)
            {
                _player.Pause();
            }
            else
            {
                _player.Play();
            }
        }
        else if (_items.Count > 0 && _items[_index].Kind != MediaKind.Video)
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
        if (_items.Count > 0)
        {
            UpdateOverlay(_items[_index]);
        }
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        if (_player is not null)
        {
            _player.Mute = _muted;
        }
        MuteButton.Opacity = _muted ? 0.55 : 1;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        StopVideo();
        _timer?.Stop();
        Closed?.Invoke(this, EventArgs.Empty);
        Focus(FocusState.Programmatic);
    }

    private void StopVideo()
    {
        if (_player is not null)
        {
            _player.EndReached -= OnEndReached;
            try
            {
                _player.Stop();
            }
            catch
            {
                // LibVLC peut déjà avoir libéré la sortie vidéo.
            }
        }
        _media?.Dispose();
        _media = null;
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(Next);
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

    private void DisposePlayer()
    {
        StopVideo();
        _timer?.Stop();
        _timer = null;
        _player?.Dispose();
        _libVlc?.Dispose();
        _player = null;
        _libVlc = null;
        _initialized = false;
    }
}



