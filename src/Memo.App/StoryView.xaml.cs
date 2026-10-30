using System.Globalization;
using Memo.App.Export;
using Memo.App.Services;
using Memo.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    }

    public void Start(IReadOnlyList<MemoryCard> items, int index)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        _paused = false;
        _playbackSuspended = false;
        ControlsBar.Visibility = Visibility.Visible;
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

        Stage.Mute = _muted;
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

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || _previewOpen || _exporting)
        {
            return;
        }

        await OpenPreviewAsync();
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
            };
            picker.FileTypeChoices.Add("Vidéo MP4", new List<string> { ".mp4" });
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
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
                await ShowExportMessageAsync(
                    "Export terminé",
                    AppendWarnings(message, result.Warnings));
            }
            else if (result.Cancelled)
            {
                await ShowExportMessageAsync(
                    "Export annulé",
                    "Aucun fichier final n'a été conservé. " +
                    "La sélection reste disponible pour un nouvel export.");
            }
            else
            {
                await ShowExportMessageAsync(
                    "Export impossible",
                    AppendWarnings(result.ErrorMessage ?? "Erreur inconnue.", result.Warnings));
            }
        }
        catch (Exception ex)
        {
            await ShowExportMessageAsync("Export impossible", ex.Message);
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

    private async Task ShowExportMessageAsync(string title, string message)
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
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void Next()
    {
        // Une fin de vidéo ne doit pas changer la sélection pendant l'aperçu ou l'export.
        if (_previewOpen || _exporting)
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
        if (_previewOpen || _exporting || _index == 0)
        {
            return;
        }
        _index--;
        ShowCurrent();
    }

    private void OnNext(object sender, RoutedEventArgs e) => Next();
    private void OnPrevious(object sender, RoutedEventArgs e) => Previous();
    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause() => SetPaused(!_paused);

    private void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            UpdatePlayIcon();
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
        MuteIcon.Glyph = _muted ? "\uE74F" : "\uE767";
        MuteButton.Opacity = _muted ? 0.55 : 1;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            MuteButton,
            _muted ? "Rétablir le son" : "Couper le son");
    }

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
