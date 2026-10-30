using System.Globalization;
using Memo.App.Export;
using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace Memo.App;

public sealed partial class StoryView : UserControl
{
    private static readonly SolidColorBrush PreviewTextBrush = new(Windows.UI.Color.FromArgb(255, 244, 239, 231));
    private static readonly SolidColorBrush PreviewSecondaryBrush = new(Windows.UI.Color.FromArgb(255, 176, 167, 154));
    private static readonly SolidColorBrush PreviewMutedBrush = new(Windows.UI.Color.FromArgb(255, 140, 131, 120));
    private static readonly SolidColorBrush PreviewAccentBrush = new(Windows.UI.Color.FromArgb(255, 227, 154, 92));
    private static readonly SolidColorBrush PreviewAudioBrush = new(Windows.UI.Color.FromArgb(255, 127, 183, 155));
    private static readonly SolidColorBrush PreviewWarningBrush = new(Windows.UI.Color.FromArgb(255, 232, 178, 92));
    private static readonly SolidColorBrush PreviewRowBrush = new(Windows.UI.Color.FromArgb(13, 244, 239, 231));
    private static readonly SolidColorBrush PreviewPhotoChipBrush = new(Windows.UI.Color.FromArgb(255, 42, 33, 26));
    private static readonly SolidColorBrush PreviewVideoChipBrush = new(Windows.UI.Color.FromArgb(255, 30, 42, 40));

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

    // Volume conservé pendant toute la session du lecteur. Le mode muet mémorise la
    // dernière valeur audible pour qu'un démutage ne laisse pas le son à zéro.
    private int _volume = 100;
    private int _lastAudibleVolume = 100;
    private bool _updatingVolumeUi;

    // Recherche ponctuelle de l'heure exacte quand la date est connue à la seconde.
    private CancellationTokenSource? _captionTimeCts;
    private Task? _captionTimeTask;
    private string? _exactTimeLabel;
    private int _captionRequestVersion;

    // Enregistrement du média courant : la copie s'exécute hors du rendu de la Story.
    private bool _savingMedia;
    private CancellationTokenSource? _savingMediaCts;
    private bool _closed;

    // Options d'export conservées d'une prévisualisation à l'autre. La sélection elle-même
    // (_items et _index) n'est jamais modifiée par l'export.
    private StoryExportQuality _exportQuality = StoryExportQuality.Hd1080;
    private bool _exportIncludeAudio = true;
    private double _exportPhotoDuration = 4;
    private bool _exportOptionsInitialized;
    private bool _previewOpen;
    private bool _previewUpdating;
    private bool _exporting;
    private bool _playbackSuspended;
    private bool _resumePlaybackAfterSuspend;
    private IReadOnlyList<StoryExportPreviewAnalysis>? _previewAnalyses;
    private StoryExportPreviewPlan? _previewPlan;
    private CancellationTokenSource? _previewAnalysisCts;
    private CancellationTokenSource? _exportCts;

    public event EventHandler? Closed;

    public StoryView()
    {
        InitializeComponent();
        Stage.Ended += (_, _) => Next();

        // Le réglage « Activer le son » donne l'état initial ; les changements faits
        // dans le lecteur restent ensuite valables pendant toute la session.
        _muted = !AppState.Current.Settings.PlayAudio;
        ApplyVolume();
        SyncVolumeUi();
        SyncOverlayUi();
    }

    public void Start(IReadOnlyList<MemoryCard> items, int index)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        _paused = false;
        _closed = false;
        _playbackSuspended = false;
        ControlsBar.Visibility = Visibility.Visible;
        SyncVolumeUi();
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
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 255, 255)),
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
        CancelCaptionTimeLookup();
        _exactTimeLabel = null;
        Counter.Text = $"{_index + 1} / {_items.Count}";
        CaptionTitle.Text = AppState.Current.FormatDate(card);
        AutomationProperties.SetName(CaptionTitle, CaptionTitle.Text);
        CaptionSubtitle.Text = BuildSubtitle(card);

        var overlayPath = AppState.Current.OverlayFor(card.Id);
        _overlayVisible = overlayPath is not null;
        Stage.SetOverlay(ResolvePath(overlayPath));
        Stage.SetOverlayVisible(_overlayVisible);
        OverlayToggle.IsEnabled = overlayPath is not null;
        OverlayToggle.IsChecked = _overlayVisible;
        SyncOverlayUi();
        UpdateSaveButtonState();

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

        ApplyVolume();
        UpdateTimeReadout();
        BeginExactTimeLookup(card);
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
            UpdateTimeReadout();
            return;
        }

        var elapsed = (_photoElapsed + (DateTimeOffset.Now - _photoStarted)).TotalSeconds;
        var progress = Math.Clamp(elapsed / _photoDuration.TotalSeconds, 0, 1);
        UpdateProgress(progress);
        UpdateTimeReadout();
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

    /// <summary>
    /// Met à jour le temps courant et la durée. Pour une vidéo, la position vient du
    /// lecteur LibVLC ; pour une photo, de la durée d'affichage effective.
    /// </summary>
    private void UpdateTimeReadout()
    {
        if (_items.Count == 0 || _index < 0 || _index >= _items.Count)
        {
            SetTimeText("— / —");
            return;
        }

        MemoryCard card = _items[_index];
        double duration;
        double position;

        if (card.Kind == MediaKind.Video)
        {
            duration = Stage.LengthMs > 0
                ? Stage.LengthMs / 1000.0
                : card.DurationMs is > 0
                    ? card.DurationMs.Value / 1000.0
                    : 0;
            position = Stage.IsVideo && duration > 0 ? duration * Stage.Position : 0;
        }
        else
        {
            duration = _photoDuration.TotalSeconds;
            double elapsed = _photoElapsed.TotalSeconds;
            if (!_paused)
            {
                elapsed += (DateTimeOffset.Now - _photoStarted).TotalSeconds;
            }

            position = Math.Clamp(elapsed, 0, duration);
        }

        if (!double.IsFinite(duration) || duration <= 0)
        {
            SetTimeText("— / —");
            return;
        }

        string current = StoryExportPreviewBuilder.FormatDuration(position);
        string total = StoryExportPreviewBuilder.FormatDuration(duration);
        SetTimeText($"{current} / {total}", $"Position {current} sur {total}");
    }

    private void SetTimeText(string text, string? accessibleName = null)
    {
        if (!string.Equals(TimeText.Text, text, StringComparison.Ordinal))
        {
            TimeText.Text = text;
        }

        string name = accessibleName ?? text;
        if (!string.Equals(AutomationProperties.GetName(TimeText), name, StringComparison.Ordinal))
        {
            AutomationProperties.SetName(TimeText, name);
        }
    }

    /// <summary>
    /// Lance la recherche de l'heure exacte. Elle n'a lieu que si la date est
    /// réellement connue à la seconde et si une bibliothèque fournit des candidats.
    /// </summary>
    private void BeginExactTimeLookup(MemoryCard card)
    {
        CancelCaptionTimeLookup();
        _captionRequestVersion++;

        if (card.DatePrecision != DatePrecision.Second || AppState.Current.Library is null)
        {
            return;
        }

        _captionTimeCts = new CancellationTokenSource();
        _captionTimeTask = LoadExactTimeAsync(card, _captionRequestVersion, _captionTimeCts);
    }

    private void CancelCaptionTimeLookup()
    {
        // On annule sans disposer ici : la tâche en cours libère son propre jeton dans
        // son finally, ce qui évite de disposer un CancellationTokenSource encore utilisé.
        _captionTimeCts?.Cancel();
        _captionTimeCts = null;
        _captionTimeTask = null;
    }

    private async Task LoadExactTimeAsync(
        MemoryCard card,
        int version,
        CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            MemoLibrary? library = AppState.Current.Library;
            if (library is null)
            {
                return;
            }

            IReadOnlyList<DateCandidateInfo> candidates =
                await library.Catalog.GetDateCandidatesAsync(card.Id, token);

            if (token.IsCancellationRequested ||
                version != _captionRequestVersion ||
                _index < 0 ||
                _index >= _items.Count ||
                _items[_index].Id != card.Id)
            {
                return;
            }

            string? time = FindExactTime(candidates, card);
            if (time is null)
            {
                return;
            }

            _exactTimeLabel = time;
            string date = AppState.Current.FormatDate(card);
            CaptionTitle.Text = $"{date} · {time}";
            AutomationProperties.SetName(CaptionTitle, $"{date} à {time}");
        }
        catch (OperationCanceledException)
        {
            // La légende suivante a pris le relais.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // L'heure n'est jamais inventée : la date seule reste affichée.
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_captionTimeCts, cts))
            {
                _captionTimeCts = null;
                _captionTimeTask = null;
            }
        }
    }

    private static string? FindExactTime(IReadOnlyList<DateCandidateInfo> candidates, MemoryCard card)
    {
        List<DateCandidateInfo> matching = candidates
            .Where(c => c.Precision == DatePrecision.Second && c.LocalDate == card.LocalDate)
            .ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        // La source qui a déterminé la date de la fiche est prioritaire. Si elle est
        // présente mais illisible, on n'emprunte pas l'heure d'une autre source : cela
        // reviendrait à inventer une information que la fiche ne porte pas.
        List<DateCandidateInfo> preferred = matching
            .Where(c => c.Source == card.DateSource)
            .ToList();
        if (preferred.Count > 0)
        {
            foreach (DateCandidateInfo candidate in preferred.OrderByDescending(c => c.Confidence))
            {
                if (TryFormatCandidateTime(candidate, out string time))
                {
                    return time;
                }
            }

            return null;
        }

        foreach (DateCandidateInfo candidate in matching.OrderByDescending(c => c.Confidence))
        {
            if (TryFormatCandidateTime(candidate, out string time))
            {
                return time;
            }
        }

        return null;
    }

    private static bool TryFormatCandidateTime(DateCandidateInfo candidate, out string time)
    {
        time = string.Empty;

        if (!string.IsNullOrWhiteSpace(candidate.InstantUtc) &&
            DateTimeOffset.TryParse(
                candidate.InstantUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset instant))
        {
            TimeZoneInfo? zone =
                TryFindTimeZone(candidate.TimeZoneId) ??
                TryFindTimeZone(AppState.Current.Settings.TimeZoneId);
            DateTimeOffset local = zone is null
                ? instant.ToLocalTime()
                : TimeZoneInfo.ConvertTime(instant, zone);
            time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            return true;
        }

        // Un EXIF horodaté contient l'heure locale de prise de vue dans sa valeur brute.
        // Les formats ci-dessous exigent explicitement une heure : une date seule ne
        // peut donc jamais produire un « 00:00 » inventé.
        string raw = candidate.RawValue?.Trim() ?? string.Empty;
        if (raw.Length < 16)
        {
            return false;
        }

        string[] formats =
        {
            "yyyy:MM:dd HH:mm:ss",
            "yyyy:MM:dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm",
        };

        if (DateTime.TryParseExact(
                raw,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime parsed))
        {
            time = parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static TimeZoneInfo? TryFindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Applique le volume effectif au lecteur natif, en tenant compte du mode muet.</summary>
    private void ApplyVolume()
    {
        Stage.Volume = _muted ? 0 : _volume;
        Stage.Mute = _muted;
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolumeUi)
        {
            return;
        }

        SetVolume(e.NewValue);
    }

    private void SetVolume(double value)
    {
        int volume = Math.Clamp(
            (int)Math.Round(double.IsFinite(value) ? value : 0, MidpointRounding.AwayFromZero),
            0,
            100);

        if (volume == 0)
        {
            // Le curseur à zéro coupe le son sans oublier la dernière valeur audible.
            _muted = true;
        }
        else
        {
            _volume = volume;
            _lastAudibleVolume = volume;
            _muted = false;
        }

        ApplyVolume();
        SyncVolumeUi();
    }

    private void SetMuted(bool muted)
    {
        _muted = muted;
        if (!muted && _volume <= 0)
        {
            _volume = _lastAudibleVolume > 0 ? _lastAudibleVolume : 100;
        }

        ApplyVolume();
        SyncVolumeUi();
    }

    /// <summary>Aligne l'icône, le curseur et le libellé sur le même état.</summary>
    private void SyncVolumeUi()
    {
        _updatingVolumeUi = true;
        try
        {
            VolumeSlider.Value = _muted ? 0 : _volume;
            VolumeValue.Text = _muted ? "Muet" : $"{_volume} %";
            MuteIcon.Glyph = _muted ? "\uE74F" : "\uE767";

            string name = _muted ? "Rétablir le son" : "Couper le son";
            string tooltip = _muted ? "Rétablir le son (M)" : "Couper le son (M)";
            AutomationProperties.SetName(MuteButton, name);
            AutomationProperties.SetHelpText(
                MuteButton,
                "Le volume et l'état muet sont conservés pendant la session.");
            ToolTipService.SetToolTip(MuteButton, tooltip);
        }
        finally
        {
            _updatingVolumeUi = false;
        }
    }

    private void OnToggleOverlay(object sender, RoutedEventArgs e)
    {
        if (!OverlayToggle.IsEnabled)
        {
            return;
        }

        _overlayVisible = OverlayToggle.IsChecked == true;
        Stage.SetOverlayVisible(_overlayVisible);
        SyncOverlayUi();
    }

    private void ToggleOverlay()
    {
        if (!OverlayToggle.IsEnabled)
        {
            return;
        }

        OverlayToggle.IsChecked = !(OverlayToggle.IsChecked == true);
        _overlayVisible = OverlayToggle.IsChecked == true;
        Stage.SetOverlayVisible(_overlayVisible);
        SyncOverlayUi();
    }

    private void SyncOverlayUi()
    {
        OverlayIcon.Opacity = _overlayVisible ? 1 : 0.45;
        OverlayLabel.Opacity = _overlayVisible ? 1 : 0.65;

        AutomationProperties.SetName(
            OverlayToggle,
            OverlayToggle.IsEnabled
                ? _overlayVisible ? "Masquer le calque" : "Afficher le calque"
                : "Calque indisponible");
        AutomationProperties.SetHelpText(
            OverlayToggle,
            OverlayToggle.IsEnabled
                ? "Affiche ou masque le calque du souvenir (O)"
                : "Ce souvenir ne possède pas de calque.");
        ToolTipService.SetToolTip(
            OverlayToggle,
            OverlayToggle.IsEnabled
                ? _overlayVisible ? "Masquer le calque (O)" : "Afficher le calque (O)"
                : "Aucun calque pour ce souvenir");
    }

    private void UpdateSaveButtonState()
    {
        if (_savingMedia)
        {
            SaveMediaButton.IsEnabled = false;
            ToolTipService.SetToolTip(SaveMediaButton, "Enregistrement du média en cours…");
            return;
        }

        bool available = _items.Count > 0 &&
                         _index >= 0 &&
                         _index < _items.Count &&
                         ResolvePath(AppState.Current.MediaFor(_items[_index].Id)) is not null;
        SaveMediaButton.IsEnabled = available;
        ToolTipService.SetToolTip(
            SaveMediaButton,
            available
                ? "Enregistrer le média source sans réencodage"
                : "Le média source de ce souvenir est introuvable");
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

    /// <summary>
    /// Enregistre le média source courant sans réencodage. Le calque éventuel est copié
    /// dans un fichier distinct, à côté du média choisi ; un JSON de métadonnées accompagne
    /// toujours la copie.
    /// </summary>
    private async void OnSaveMedia(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || _savingMedia || _previewOpen || _exporting || _closed)
        {
            return;
        }

        if (_index < 0 || _index >= _items.Count)
        {
            return;
        }

        MemoryCard card = _items[_index];
        int itemNumber = _index + 1;
        int itemCount = _items.Count;
        string? main = ResolvePath(AppState.Current.MediaFor(card.Id));
        if (main is null)
        {
            await ShowMessageAsync(
                "Média introuvable",
                "Le média source de ce souvenir n'est pas disponible dans la bibliothèque. " +
                "Aucun fichier n'a été enregistré.");
            return;
        }

        bool pausedBySave = !_paused;
        _savingMedia = true;
        _savingMediaCts = new CancellationTokenSource();
        if (pausedBySave)
        {
            SetPaused(true);
            BigPlay.Visibility = Visibility.Collapsed;
        }

        UpdateSaveButtonState();
        SaveProgress.IsActive = true;
        SaveProgress.Visibility = Visibility.Visible;
        AutomationProperties.SetName(SaveMediaButton, "Enregistrement du média en cours");
        AutomationProperties.SetHelpText(
            SaveMediaButton,
            "Choisissez la destination du média source dans la boîte de dialogue.");

        try
        {
            // La recherche d'heure exacte est déjà lancée : l'attendre brièvement permet
            // d'enrichir les métadonnées sans bloquer l'enregistrement sur une lecture lente.
            if (_captionTimeTask is { } captionTask)
            {
                try
                {
                    await Task.WhenAny(captionTask, Task.Delay(1200));
                }
                catch (Exception)
                {
                    // Une légende sans heure reste valide.
                }
            }

            if (_closed)
            {
                return;
            }

            string? overlay = ResolvePath(AppState.Current.OverlayFor(card.Id));
            if (overlay is not null && PathsEqual(main, overlay))
            {
                overlay = null;
            }

            string extension = StoryMediaSaver.GetSafeExtension(
                main,
                card.Kind == MediaKind.Video ? ".mp4" : ".jpg");
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = card.Kind == MediaKind.Video
                    ? PickerLocationId.VideosLibrary
                    : PickerLocationId.PicturesLibrary,
                SuggestedFileName = BuildMediaFileName(card, itemNumber),
                DefaultFileExtension = extension,
            };
            picker.FileTypeChoices.Add(
                StoryMediaSaver.GetFileTypeLabel(extension),
                new List<string> { extension });

            if (App.MainWindow is null)
            {
                throw new InvalidOperationException(
                    "La fenêtre principale n'est pas disponible pour ouvrir le sélecteur de fichiers.");
            }

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || _closed)
            {
                return;
            }

            var request = new StoryMediaSaveRequest(
                main,
                file.Path,
                overlay,
                card.Kind == MediaKind.Video ? "vidéo" : "photo",
                AppState.Current.FormatDate(card),
                _exactTimeLabel,
                card.DatePrecision,
                card.DateSource,
                card.DateMeaning,
                itemNumber,
                itemCount,
                AppState.Current.LibraryName,
                card.SpaceName,
                card.LocationLabel,
                card.Width,
                card.Height,
                card.DurationMs,
                card.Format);

            StoryMediaSaveResult result = await StoryMediaSaver.SaveAsync(
                request,
                _savingMediaCts.Token);

            if (_closed)
            {
                return;
            }

            string message =
                $"Média enregistré :\n{result.MediaPath}\n" +
                $"{StoryExportPreviewBuilder.FormatSize(result.MediaBytes)} · " +
                $"SHA-256 {ShortHash(result.MediaSha256)}";
            if (result.OverlayPath is not null)
            {
                message +=
                    $"\n\nCalque enregistré séparément :\n{result.OverlayPath}\n" +
                    $"{StoryExportPreviewBuilder.FormatSize(result.OverlayBytes ?? 0)} · " +
                    $"SHA-256 {ShortHash(result.OverlaySha256 ?? string.Empty)}";
            }

            message +=
                $"\n\nMétadonnées JSON :\n{result.MetadataPath}\n\n" +
                "Aucun réencodage : le média est une copie exacte de la source.";

            await ShowMessageAsync("Média enregistré", message);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                await ShowMessageAsync(
                    "Enregistrement annulé",
                    "Aucun fichier partiel n'a été conservé. La Story reste disponible.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed)
            {
                await ShowMessageAsync("Enregistrement impossible", ex.Message);
            }
        }
        finally
        {
            _savingMedia = false;
            SaveProgress.IsActive = false;
            SaveProgress.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(SaveMediaButton, "Enregistrer le média courant");
            AutomationProperties.SetHelpText(
                SaveMediaButton,
                "Copie le média source tel quel, sans réencodage, avec un fichier JSON de métadonnées.");
            _savingMediaCts?.Dispose();
            _savingMediaCts = null;

            if (!_closed)
            {
                UpdateSaveButtonState();

                // On ne reprend la lecture que si la pause vient de l'enregistrement.
                if (pausedBySave && _paused)
                {
                    SetPaused(false);
                }
            }
        }
    }

    private string BuildMediaFileName(MemoryCard card, int itemNumber)
    {
        var parts = new List<string> { "memo" };
        if (card.LocalDate is { } date)
        {
            parts.Add(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(_exactTimeLabel))
        {
            parts.Add(_exactTimeLabel.Replace(":", string.Empty, StringComparison.Ordinal));
        }

        parts.Add(card.Kind == MediaKind.Video ? "video" : "photo");
        parts.Add(itemNumber.ToString("00", CultureInfo.InvariantCulture));
        return string.Join("-", parts);
    }

    private static string ShortHash(string hash) =>
        string.IsNullOrEmpty(hash)
            ? "—"
            : hash.Length > 16
                ? hash[..16] + "…"
                : hash;

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || _previewOpen || _exporting || _savingMedia || _closed)
        {
            return;
        }

        try
        {
            await OpenPreviewAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _previewOpen = false;
            CancelPreviewAnalysis();
            PreviewPanel.Visibility = Visibility.Collapsed;
            PreviewBusy.IsActive = false;
            _previewAnalyses = null;
            _previewPlan = null;
            RestorePlayback();
            await ShowMessageAsync("Prévisualisation impossible", ex.Message);
        }
    }

    private async Task OpenPreviewAsync()
    {
        InitializeExportOptions();

        _previewOpen = true;
        SuspendPlayback();

        _previewUpdating = true;
        PreviewQualityBox.SelectedIndex = _exportQuality == StoryExportQuality.Hd720 ? 1 : 0;
        PreviewAudioToggle.IsOn = _exportIncludeAudio;
        PreviewPhotoDuration.Value = _exportPhotoDuration;
        PreviewQualityBox.IsEnabled = false;
        PreviewAudioToggle.IsEnabled = false;
        PreviewPhotoDuration.IsEnabled = false;
        PreviewExportButton.IsEnabled = false;
        PreviewWarningBar.IsOpen = false;
        PreviewItems.Children.Clear();
        PreviewEmpty.Text = "Analyse des médias…";
        PreviewEmpty.Visibility = Visibility.Visible;
        PreviewSummary.Text = "Analyse des médias…";
        PreviewEstimate.Text = "Durée estimée —";
        PreviewBusy.IsActive = true;
        PreviewBusy.Visibility = Visibility.Visible;
        PreviewPanel.Visibility = Visibility.Visible;
        _previewUpdating = false;

        // Le panneau couvre les commandes du lecteur : on garde le focus dans la vue pour
        // qu'Échap ferme la prévisualisation dès l'analyse.
        Focus(FocusState.Programmatic);

        CancelPreviewAnalysis();
        _previewAnalysisCts = new CancellationTokenSource();
        CancellationToken token = _previewAnalysisCts.Token;
        int total = _items.Count;
        var progress = new Progress<int>(done =>
        {
            if (!_previewOpen)
            {
                return;
            }

            PreviewEmpty.Text = $"Analyse des médias… {done}/{total}";
            PreviewSummary.Text = $"Analyse des médias… {done}/{total}";
        });

        try
        {
            IReadOnlyList<StoryExportPreviewSource> sources = BuildPreviewSources();
            IReadOnlyList<StoryExportPreviewAnalysis> analyses =
                await StoryExportPreviewBuilder.AnalyzeAsync(sources, progress, token);

            if (!_previewOpen || token.IsCancellationRequested)
            {
                return;
            }

            _previewAnalyses = analyses;
            PreviewBusy.IsActive = false;
            PreviewBusy.Visibility = Visibility.Collapsed;
            PreviewQualityBox.IsEnabled = true;
            RefreshPreviewPlan();
            PreviewExportButton.Focus(FocusState.Programmatic);
        }
        catch (OperationCanceledException)
        {
            // Le panneau a été fermé pendant l'analyse.
        }
        catch (Exception ex)
        {
            if (!_previewOpen)
            {
                return;
            }

            PreviewBusy.IsActive = false;
            PreviewBusy.Visibility = Visibility.Collapsed;
            PreviewEmpty.Text = "Prévisualisation impossible.";
            PreviewEmpty.Visibility = Visibility.Visible;
            PreviewSummary.Text = "Prévisualisation impossible";
            PreviewWarningBar.Severity = InfoBarSeverity.Error;
            PreviewWarningBar.Title = "Analyse impossible";
            PreviewWarningBar.Message = ex.Message;
            PreviewWarningBar.IsOpen = true;
        }
    }

    private void InitializeExportOptions()
    {
        if (_exportOptionsInitialized)
        {
            return;
        }

        _exportOptionsInitialized = true;
        _exportQuality = StoryExportQuality.Hd1080;
        _exportIncludeAudio = AppState.Current.Settings.PlayAudio;
        _exportPhotoDuration = NormalizePhotoDuration(AppState.Current.Settings.PhotoDurationSeconds);
    }

    private IReadOnlyList<StoryExportPreviewSource> BuildPreviewSources()
    {
        var sources = new List<StoryExportPreviewSource>(_items.Count);
        for (int i = 0; i < _items.Count; i++)
        {
            MemoryCard card = _items[i];
            string? rawMain = AppState.Current.MediaFor(card.Id);
            string? main = ResolvePath(rawMain)
                           ?? (card.Kind == MediaKind.Photo
                               ? ResolvePath(AppState.Current.ThumbnailFor(card.Id))
                               : null);
            string? rawOverlay = AppState.Current.OverlayFor(card.Id);

            sources.Add(new StoryExportPreviewSource(
                i + 1,
                AppState.Current.FormatDate(card),
                card.Kind,
                main,
                ResolvePath(rawOverlay),
                !string.IsNullOrWhiteSpace(rawOverlay),
                card.DurationMs,
                card.Width,
                card.Height));
        }

        return sources;
    }

    private void RefreshPreviewPlan()
    {
        if (_previewAnalyses is null || _previewUpdating)
        {
            return;
        }

        _previewUpdating = true;
        try
        {
            _exportQuality = PreviewQualityBox.SelectedIndex == 1
                ? StoryExportQuality.Hd720
                : StoryExportQuality.Hd1080;
            _exportIncludeAudio = PreviewAudioToggle.IsOn;
            _exportPhotoDuration = NormalizePhotoDuration(PreviewPhotoDuration.Value);

            StoryExportPreviewPlan plan = StoryExportPreviewBuilder.CreatePlan(
                _previewAnalyses,
                _exportQuality,
                _exportIncludeAudio,
                _exportPhotoDuration);
            _previewPlan = plan;

            PreviewItems.Children.Clear();
            foreach (StoryExportPreviewRow row in plan.Rows)
            {
                PreviewItems.Children.Add(BuildPreviewRow(row));
            }

            if (plan.Rows.Count == 0)
            {
                PreviewEmpty.Text = "Aucun élément dans cette Story.";
                PreviewEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                PreviewEmpty.Visibility = Visibility.Collapsed;
            }

            PreviewSummary.Text = BuildPreviewSummary(plan);
            PreviewEstimate.Text =
                $"Durée estimée {StoryExportPreviewBuilder.FormatDuration(plan.DurationSeconds)}" +
                $" · ≈ {StoryExportPreviewBuilder.FormatSize(plan.EstimatedBytes)}";
            PreviewAudioToggle.IsEnabled = plan.VideoCount > 0;
            PreviewPhotoDuration.IsEnabled = plan.PhotoCount > 0;
            PreviewExportButton.IsEnabled = plan.CanExport;
            UpdatePreviewWarnings(plan);
        }
        finally
        {
            _previewUpdating = false;
        }
    }

    private string BuildPreviewSummary(StoryExportPreviewPlan plan)
    {
        var parts = new List<string> { Plural(plan.Rows.Count, "élément", "éléments") };
        if (plan.PhotoCount > 0)
        {
            parts.Add(Plural(plan.PhotoCount, "photo", "photos"));
        }
        if (plan.VideoCount > 0)
        {
            parts.Add(Plural(plan.VideoCount, "vidéo", "vidéos"));
        }
        if (plan.UnavailableCount > 0)
        {
            parts.Add(Plural(plan.UnavailableCount, "indisponible", "indisponibles"));
        }

        parts.Add($"durée {StoryExportPreviewBuilder.FormatDuration(plan.DurationSeconds)}");
        string quality = _exportQuality == StoryExportQuality.Hd720 ? "720p" : "1080p";
        parts.Add($"≈ {StoryExportPreviewBuilder.FormatSize(plan.EstimatedBytes)} en {quality}");
        string audio = plan.VideoCount == 0
            ? "sans piste audio"
            : plan.HasAudioTrack
                ? "avec audio"
                : "sans audio";
        parts.Add(audio);
        return string.Join(" · ", parts);
    }

    private void UpdatePreviewWarnings(StoryExportPreviewPlan plan)
    {
        if (plan.Warnings.Count == 0)
        {
            PreviewWarningBar.IsOpen = false;
            return;
        }

        PreviewWarningBar.Severity = plan.CanExport ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        PreviewWarningBar.Title = plan.CanExport ? "Points à vérifier" : "Export impossible";

        var shown = plan.Warnings.Take(4).ToList();
        string message = string.Join("\n", shown);
        if (plan.Warnings.Count > shown.Count)
        {
            message += $"\n… et {plan.Warnings.Count - shown.Count} autre(s) avertissement(s).";
        }

        PreviewWarningBar.Message = message;
        PreviewWarningBar.IsOpen = true;
    }

    private void OnPreviewQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_previewUpdating)
        {
            return;
        }

        RefreshPreviewPlan();
    }

    private void OnPreviewAudioToggled(object sender, RoutedEventArgs e)
    {
        if (_previewUpdating)
        {
            return;
        }

        RefreshPreviewPlan();
    }

    private void OnPreviewPhotoDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_previewUpdating)
        {
            return;
        }

        // Un champ vidé produit NaN : on revient à la dernière valeur valide.
        if (!double.IsFinite(sender.Value))
        {
            _previewUpdating = true;
            sender.Value = _exportPhotoDuration;
            _previewUpdating = false;
        }

        RefreshPreviewPlan();
    }

    private void OnPreviewCancel(object sender, RoutedEventArgs e) => ClosePreview();

    private void ClosePreview()
    {
        if (!_previewOpen)
        {
            return;
        }

        _previewOpen = false;
        CancelPreviewAnalysis();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewBusy.IsActive = false;
        PreviewItems.Children.Clear();
        _previewAnalyses = null;
        _previewPlan = null;

        // La lecture reprend exactement dans l'état où elle avait été suspendue.
        RestorePlayback();
        Focus(FocusState.Programmatic);
    }

    private void CancelPreviewAnalysis()
    {
        _previewAnalysisCts?.Cancel();
        _previewAnalysisCts?.Dispose();
        _previewAnalysisCts = null;
    }

    private static double NormalizePhotoDuration(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value, 1), 1, 30) : 4;

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural}";

    private static UIElement BuildPreviewRow(StoryExportPreviewRow row)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Text = row.Number.ToString(CultureInfo.InvariantCulture),
            FontSize = 12,
            Foreground = PreviewMutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(number, 0);
        grid.Children.Add(number);

        Border kind = BuildBadge(
            row.Kind == MediaKind.Video ? "Vidéo" : row.Kind == MediaKind.Photo ? "Photo" : "Inconnu",
            row.Kind == MediaKind.Video ? PreviewVideoChipBrush : PreviewPhotoChipBrush,
            row.Kind == MediaKind.Video ? PreviewAudioBrush : PreviewAccentBrush);
        kind.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(kind, 1);
        grid.Children.Add(kind);

        var description = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        description.Children.Add(new TextBlock
        {
            Text = row.Title,
            FontSize = 13,
            Foreground = PreviewTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        description.Children.Add(new TextBlock
        {
            Text = $"{row.FormatLabel} · {row.FileName}",
            FontSize = 11,
            Foreground = PreviewSecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(row.Warning))
        {
            description.Children.Add(new TextBlock
            {
                Text = $"⚠ {row.Warning}",
                FontSize = 11,
                Foreground = PreviewWarningBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        Grid.SetColumn(description, 2);
        grid.Children.Add(description);

        var duration = new TextBlock
        {
            Text = row.DurationLabel,
            FontSize = 12,
            Foreground = PreviewSecondaryBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(duration, 3);
        grid.Children.Add(duration);

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (row.HasOverlay)
        {
            badges.Children.Add(BuildBadge("Calque", PreviewPhotoChipBrush, PreviewAccentBrush));
        }
        if (row.HasAudio)
        {
            badges.Children.Add(BuildBadge("Audio", PreviewVideoChipBrush, PreviewAudioBrush));
        }
        if (badges.Children.Count == 0)
        {
            badges.Children.Add(new TextBlock
            {
                Text = "—",
                FontSize = 11,
                Foreground = PreviewMutedBrush,
            });
        }
        Grid.SetColumn(badges, 4);
        grid.Children.Add(badges);

        var border = new Border
        {
            Background = PreviewRowBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Child = grid,
            Margin = new Thickness(0, 0, 4, 0),
        };

        string type = row.Kind == MediaKind.Video ? "vidéo" : row.Kind == MediaKind.Photo ? "photo" : "média";
        var accessibility = new List<string>
        {
            $"Élément {row.Number}",
            type,
            row.Title,
            row.FormatLabel,
            $"durée {row.DurationLabel}",
        };
        if (row.HasOverlay)
        {
            accessibility.Add("avec calque");
        }
        if (row.HasAudio)
        {
            accessibility.Add("avec audio");
        }
        if (!string.IsNullOrWhiteSpace(row.Warning))
        {
            accessibility.Add($"avertissement : {row.Warning}");
        }
        AutomationProperties.SetName(border, string.Join(", ", accessibility));
        return border;
    }

    private static Border BuildBadge(string text, SolidColorBrush background, SolidColorBrush foreground) =>
        new()
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = foreground,
            },
        };

    private async void OnPreviewStartExport(object sender, RoutedEventArgs e)
    {
        if (_exporting || _previewPlan is not { CanExport: true } plan)
        {
            return;
        }

        _exporting = true;
        PreviewExportButton.IsEnabled = false;
        var started = false;

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = $"memo-story-{DateTime.Now:yyyyMMdd-HHmm}",
                DefaultFileExtension = ".mp4",
            };
            picker.FileTypeChoices.Add("Vidéo MP4", new List<string> { ".mp4" });

            if (App.MainWindow is null)
            {
                throw new InvalidOperationException(
                    "La fenêtre principale n'est pas disponible pour ouvrir le sélecteur de fichiers.");
            }

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                await ShowMessageAsync(
                    "Export impossible",
                    "Le fichier choisi n'expose pas de chemin local accessible à l'export. " +
                    "Choisissez un dossier de ce disque.");
                return;
            }

            // Le sélecteur a déjà demandé confirmation pour un fichier existant : on autorise
            // le remplacement explicite, jamais une source.
            var request = new StoryExportRequest(
                plan.Items,
                file.Path,
                "Memo",
                _exportQuality,
                _exportIncludeAudio,
                _exportPhotoDuration,
                OverwriteExistingFile: true);

            started = true;
            BeginExportUi();
            var progress = new Progress<StoryExportProgress>(UpdateExportProgress);
            StoryExportResult result = await new StoryExporter().ExportAsync(
                request,
                progress,
                _exportCts!.Token);

            if (result.Succeeded)
            {
                string message =
                    $"Vidéo créée : {result.OutputPath}\n" +
                    $"{StoryExportPreviewBuilder.FormatSize(result.SizeBytes)} · " +
                    $"{StoryExportPreviewBuilder.FormatDuration(result.Duration.TotalSeconds)}";
                await ShowMessageAsync(
                    "Export terminé",
                    AppendWarnings(message, result.Warnings));
            }
            else if (result.Cancelled)
            {
                await ShowMessageAsync(
                    "Export annulé",
                    "Aucun fichier final n'a été conservé. " +
                    "La sélection reste disponible pour un nouvel export.");
            }
            else
            {
                await ShowMessageAsync(
                    "Export impossible",
                    AppendWarnings(result.ErrorMessage ?? "Erreur inconnue.", result.Warnings));
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Export impossible", ex.Message);
        }
        finally
        {
            _exporting = false;
            ExportCancelButton.IsEnabled = false;

            if (started)
            {
                ExportPanel.Visibility = Visibility.Collapsed;
                PreviewItems.Children.Clear();
                _previewPlan = null;
                _previewAnalyses = null;
                RestorePlayback();
                Focus(FocusState.Programmatic);
            }
            else
            {
                // Le sélecteur ou son initialisation a échoué : la prévisualisation reste
                // ouverte et l'utilisateur peut réessayer.
                PreviewExportButton.IsEnabled = _previewPlan?.CanExport == true;
                if (PreviewExportButton.IsEnabled)
                {
                    PreviewExportButton.Focus(FocusState.Programmatic);
                }
            }

            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    private void BeginExportUi()
    {
        _previewOpen = false;
        CancelPreviewAnalysis();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewBusy.IsActive = false;

        ExportProgress.Value = 0;
        ExportStatus.Text = "Préparation…";
        ExportCancelButton.IsEnabled = true;
        ExportPanel.Visibility = Visibility.Visible;
        Focus(FocusState.Programmatic);

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
    }

    private void UpdateExportProgress(StoryExportProgress progress)
    {
        ExportProgress.Value = progress.Percent;
        string phase = progress.Phase switch
        {
            StoryExportPhase.Preparing => "Préparation",
            StoryExportPhase.Rendering => "Rendu",
            StoryExportPhase.Finalizing => "Finalisation",
            StoryExportPhase.Completed => "Terminé",
            StoryExportPhase.Cancelled => "Annulé",
            StoryExportPhase.Failed => "Échec",
            _ => "Export",
        };
        string suffix = progress.ItemCount > 0 && progress.ItemIndex > 0
            ? $" · {progress.ItemIndex}/{progress.ItemCount}"
            : string.Empty;
        ExportStatus.Text = $"{phase}{suffix} · {progress.Message}";
    }

    private void OnCancelExport(object sender, RoutedEventArgs e) => RequestCancelExport();

    private void RequestCancelExport()
    {
        if (!_exporting || _exportCts is null || _exportCts.IsCancellationRequested)
        {
            return;
        }

        _exportCts.Cancel();
        ExportCancelButton.IsEnabled = false;
        ExportStatus.Text = "Annulation demandée…";
    }

    private static string AppendWarnings(string message, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return message;
        }

        var shown = warnings.Take(3).ToList();
        string suffix = string.Join("\n", shown);
        if (warnings.Count > shown.Count)
        {
            suffix += $"\n… et {warnings.Count - shown.Count} autre(s).";
        }

        return $"{message}\n\n{suffix}";
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                MaxWidth = 520,
            },
            CloseButtonText = "Fermer",
            RequestedTheme = ElementTheme.Dark,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void Next()
    {
        // Une fin de vidéo ne doit pas changer la sélection pendant l'aperçu, l'export
        // ou une copie de média en cours.
        if (_previewOpen || _exporting || _savingMedia)
        {
            return;
        }

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
        if (_previewOpen || _exporting || _savingMedia || _index == 0)
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
        if (_savingMedia)
        {
            return;
        }

        SetPaused(!_paused);
    }

    private void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            UpdatePlayIcon();
            UpdateTimeReadout();
            BigPlay.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        _paused = paused;
        if (_items.Count > 0 && _index >= 0 && _index < _items.Count &&
            _items[_index].Kind == MediaKind.Video && Stage.IsVideo)
        {
            if (paused)
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
            // La durée passée en prévisualisation ou en export ne doit pas consommer
            // l'affichage de la photo en cours.
            if (paused)
            {
                _photoElapsed += DateTimeOffset.Now - _photoStarted;
            }
            else
            {
                _photoStarted = DateTimeOffset.Now;
            }
        }

        UpdatePlayIcon();
        UpdateTimeReadout();
        BigPlay.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SuspendPlayback()
    {
        if (_playbackSuspended)
        {
            return;
        }

        _playbackSuspended = true;
        _resumePlaybackAfterSuspend = !_paused;
        SetPaused(true);
        // Le panneau couvre déjà la scène : inutile d'afficher le gros bouton de reprise.
        BigPlay.Visibility = Visibility.Collapsed;
        // Les commandes du lecteur ne doivent pas rester atteignables au clavier.
        ControlsBar.Visibility = Visibility.Collapsed;
    }

    private void RestorePlayback()
    {
        if (!_playbackSuspended)
        {
            return;
        }

        _playbackSuspended = false;
        ControlsBar.Visibility = Visibility.Visible;
        SetPaused(!_resumePlaybackAfterSuspend);
    }

    private void UpdatePlayIcon()
    {
        PlayIcon.Glyph = _paused ? "\uE768" : "\uE769";
        AutomationProperties.SetName(
            PlayButton,
            _paused ? "Reprendre la lecture" : "Mettre en pause");
        ToolTipService.SetToolTip(
            PlayButton,
            _paused ? "Reprendre la lecture (espace)" : "Mettre en pause (espace)");
    }

    private void OnToggleMute(object sender, RoutedEventArgs e) => SetMuted(!_muted);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (_previewOpen)
        {
            ClosePreview();
            return;
        }

        if (_exporting)
        {
            RequestCancelExport();
            return;
        }

        _closed = true;
        _savingMediaCts?.Cancel();
        CancelCaptionTimeLookup();
        _timer?.Stop();
        Stage.StopVideo();
        Stage.Clear();
        Closed?.Invoke(this, EventArgs.Empty);
        Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_exporting)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                RequestCancelExport();
                e.Handled = true;
            }
            return;
        }

        if (_previewOpen)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                ClosePreview();
                e.Handled = true;
            }
            return;
        }

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
                ToggleOverlay();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.M:
                OnToggleMute(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }
}
