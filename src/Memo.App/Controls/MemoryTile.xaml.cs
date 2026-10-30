using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Memo.App.Controls;

public sealed partial class MemoryTile : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card), typeof(MemoryCard), typeof(MemoryTile), new PropertyMetadata(null, OnCardChanged));

    public static readonly DependencyProperty ShowDateProperty = DependencyProperty.Register(
        nameof(ShowDate), typeof(bool), typeof(MemoryTile), new PropertyMetadata(false, OnCardChanged));

    public MemoryTile()
    {
        InitializeComponent();
    }

    public MemoryCard? Card
    {
        get => (MemoryCard?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public bool ShowDate
    {
        get => (bool)GetValue(ShowDateProperty);
        set => SetValue(ShowDateProperty, value);
    }

    private static void OnCardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MemoryTile)d).Apply();

    private void Apply()
    {
        var card = Card;
        if (card is null)
        {
            return;
        }

        DateBadge.Visibility = ShowDate && card.LocalDate is not null ? Visibility.Visible : Visibility.Collapsed;
        if (card.LocalDate is { } date)
        {
            DateText.Text = date.ToDateTime(TimeOnly.MinValue).ToString("d MMM yyyy", Services.AppState.French);
        }

        FavoriteBadge.Visibility = card.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        ExcludedBadge.Visibility = card.IsExcluded ? Visibility.Visible : Visibility.Collapsed;

        var dateText = card.LocalDate is { } dateValue
            ? dateValue.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", Services.AppState.French)
            : "date inconnue";
        var kindText = card.Kind == MediaKind.Video ? "vidéo" : card.Kind == MediaKind.Photo ? "photo" : "calque";
        var nameParts = new List<string> { dateText, kindText };
        if (!string.IsNullOrWhiteSpace(card.LocationLabel)) nameParts.Add(card.LocationLabel);
        if (card.IsFavorite) nameParts.Add("favori");
        if (card.Status == MemoryStatus.OverlayOnly) nameParts.Add("média non trouvé");
        else if (card.OverlayCount > 0) nameParts.Add("calque disponible");
        AutomationProperties.SetName(this, string.Join(", ", nameParts));
        AutomationProperties.SetHelpText(this, "Appuyez sur Entrée pour ouvrir le souvenir.");

        var isOverlayOnly = card.Status == MemoryStatus.OverlayOnly;
        MissingLayer.Visibility = isOverlayOnly ? Visibility.Visible : Visibility.Collapsed;
        NoPreview.Visibility = Visibility.Collapsed;

        if (card.Kind == MediaKind.Video)
        {
            TypeBadge.Visibility = Visibility.Visible;
            TypeIcon.Glyph = "\uE768";
            TypeText.Text = FormatDuration(card.DurationMs);
        }
        else
        {
            TypeBadge.Visibility = Visibility.Collapsed;
        }

        if (isOverlayOnly)
        {
            Thumb.Source = null;
            Loading.Visibility = Visibility.Collapsed;
            return;
        }

        var path = Services.AppState.Current.ThumbnailFor(card.Id);
        if (string.IsNullOrEmpty(path))
        {
            Thumb.Source = null;
            Loading.Visibility = Visibility.Collapsed;
            NoPreviewIcon.Glyph = card.Kind == MediaKind.Video ? "\uE768" : "\uE91B";
            NoPreviewText.Text = card.Kind == MediaKind.Video ? "Vidéo · aperçu à générer" : "Photo · aperçu à générer";
            NoPreview.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 360 };
            bitmap.ImageOpened += (_, _) =>
            {
                Loading.Visibility = Visibility.Collapsed;
                Thumb.Opacity = 1;
            };
            bitmap.ImageFailed += (_, _) =>
            {
                Loading.Visibility = Visibility.Collapsed;
                Thumb.Opacity = 1;
            };
            Thumb.Opacity = 0;
            Loading.Visibility = Visibility.Visible;
            bitmap.UriSource = new Uri(path, UriKind.RelativeOrAbsolute);
            Thumb.Source = bitmap;
        }
        catch
        {
            Loading.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatDuration(long? ms)
    {
        if (ms is null or <= 0)
        {
            return "Vidéo";
        }
        var span = TimeSpan.FromMilliseconds(ms.Value);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}


