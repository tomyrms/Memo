using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Memo.App.Pages;

public sealed partial class RecapsPage : Page
{
    private List<MemoryCard> _selection = new();
    private (string Title, DateOnly Start, DateOnly End)? _period;
    private bool _ready;

    public RecapsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _ready = true;
            BuildPeriods();
            Generate();
        };
    }

    private void BuildPeriods()
    {
        PeriodBox.Items.Clear();
        var kind = SelectedKind();
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (kind == "year")
        {
            foreach (var year in AppState.Current.Cards.Where(c => c.LocalDate is not null)
                         .Select(c => c.LocalDate!.Value.Year).Distinct().OrderByDescending(y => y))
            {
                PeriodBox.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });
            }
        }
        else if (kind == "month")
        {
            for (var i = 1; i <= 12; i++)
            {
                var name = new DateTime(today.Year, i, 1).ToString("MMMM", AppState.French);
                PeriodBox.Items.Add(new ComboBoxItem { Content = char.ToUpper(name[0]) + name[1..], Tag = i });
            }
        }
        else
        {
            PeriodBox.Items.Add(new ComboBoxItem { Content = "Printemps (mars–mai)", Tag = "spring" });
            PeriodBox.Items.Add(new ComboBoxItem { Content = "Été (juin–août)", Tag = "summer" });
            PeriodBox.Items.Add(new ComboBoxItem { Content = "Automne (sept.–nov.)", Tag = "autumn" });
            PeriodBox.Items.Add(new ComboBoxItem { Content = "Hiver (déc.–févr.)", Tag = "winter" });
        }
        PeriodBox.SelectedIndex = 0;
    }

    private string SelectedKind()
        => (KindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "year";

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }
        BuildPeriods();
        Generate();
    }

    private void OnPeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
        {
            Generate();
        }
    }

    private void OnGenerate(object sender, RoutedEventArgs e) => Generate();

    private void Generate()
    {
        var state = AppState.Current;
        var kind = SelectedKind();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var item = PeriodBox.SelectedItem as ComboBoxItem;

        if (kind == "year" && item?.Tag is int year)
        {
            _period = ($"{year} en images", new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }
        else if (kind == "month" && item?.Tag is int month)
        {
            var name = new DateTime(today.Year, month, 1).ToString("MMMM yyyy", AppState.French);
            _period = (char.ToUpper(name[0]) + name[1..], new DateOnly(today.Year, month, 1), new DateOnly(today.Year, month, DateTime.DaysInMonth(today.Year, month)));
        }
        else if (kind == "season" && item?.Tag is string season)
        {
            var y = today.Year;
            _period = season switch
            {
                "spring" => ($"Printemps {y}", new DateOnly(y, 3, 1), new DateOnly(y, 5, 31)),
                "summer" => ($"Été {y}", new DateOnly(y, 6, 1), new DateOnly(y, 8, 31)),
                "autumn" => ($"Automne {y}", new DateOnly(y, 9, 1), new DateOnly(y, 11, 30)),
                _ => ($"Hiver {y - 1}–{y}", new DateOnly(y - 1, 12, 1), new DateOnly(y, 2, 28))
            };
        }

        if (_period is null)
        {
            return;
        }

        var candidates = state.Cards
            .Where(c => !c.IsExcluded && c.LocalDate is { } d && d >= _period.Value.Start && d <= _period.Value.End)
            .OrderBy(c => c.LocalDate)
            .ToList();

        _selection = PickBalanced(candidates, 12);
        Render();
    }

    private static List<MemoryCard> PickBalanced(List<MemoryCard> candidates, int max)
    {
        if (candidates.Count <= max)
        {
            return candidates;
        }
        var favorites = candidates.Where(c => c.IsFavorite).ToList();
        var pool = favorites.Concat(candidates.Where(c => !c.IsFavorite)).Distinct().ToList();
        var step = pool.Count / (double)max;
        var result = new List<MemoryCard>();
        for (var i = 0; i < max; i++)
        {
            var index = (int)Math.Round(i * step);
            index = Math.Clamp(index, 0, pool.Count - 1);
            var item = pool[index];
            if (!result.Contains(item))
            {
                result.Add(item);
            }
        }
        return result.OrderBy(c => c.LocalDate).ToList();
    }

    private void Render()
    {
        if (_period is null)
        {
            return;
        }
        RecapTitle.Text = _period.Value.Title;
        var count = _selection.Count;
        var favorites = _selection.Count(c => c.IsFavorite);
        RecapMeta.Text = $"{count} souvenir(s) · {_selection.Count(c => c.Kind == MediaKind.Video)} vidéo(s) · {favorites} favori(s)";
        CoverageLabel.Text = count == 0
            ? "Aucun média daté dans cette période."
            : $"Couverture : {_period.Value.Start:d MMM} – {_period.Value.End:d MMM yyyy}";

        var first = _selection.FirstOrDefault();
        if (first is not null && AppState.Current.ThumbnailFor(first.Id) is { } coverPath)
        {
            CoverImage.Source = new BitmapImage(new Uri(coverPath));
            CoverEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverImage.Source = null;
            CoverEmpty.Visibility = Visibility.Visible;
        }
        CoverBadge.Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CoverCount.Text = $"+{_selection.Count - 1}";

        ItemsGrid.ItemsSource = _selection;
        Notice.IsOpen = false;
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_selection.Count > 0)
        {
            App.MainWindow?.ShowStory(_selection, 0);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MemoryCard card)
        {
            App.MainWindow?.ShowStory(_selection, Math.Max(0, _selection.IndexOf(card)));
        }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_period is null)
        {
            return;
        }
        var candidates = AppState.Current.Cards
            .Where(c => !c.IsExcluded && c.LocalDate is { } d && d >= _period.Value.Start && d <= _period.Value.End)
            .OrderBy(c => c.LocalDate)
            .ToList();

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 420,
            ItemsSource = candidates,
            ItemTemplate = (DataTemplate)Resources["EditItemTemplate"]
        };
        foreach (var selected in _selection)
        {
            list.SelectedItems.Add(selected);
        }

        var dialog = new ContentDialog
        {
            Title = "Modifier la sélection",
            Content = list,
            PrimaryButtonText = "Appliquer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _selection = list.SelectedItems.OfType<MemoryCard>().OrderBy(c => c.LocalDate).ToList();
            Render();
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_period is null || _selection.Count == 0)
        {
            return;
        }
        var name = $"{_period.Value.Title} · récapitulatif";
        if (AppState.Current.Library is not null)
        {
            await AppState.Current.SaveRecapAsync(name, _selection.Select(c => c.Id).ToList());
        }
        else
        {
            var id = AppState.Current.Albums.Count == 0 ? 1 : AppState.Current.Albums.Max(a => a.Id) + 1;
            AppState.Current.Albums.Add(new CollectionInfo(
                id, name, CollectionKind.Recap, DateTimeOffset.Now, _selection.Count,
                AppState.Current.ThumbnailFor(_selection[0].Id), _selection[0].LocalDate, _selection[^1].LocalDate));
        }
        Notice.Title = "Récapitulatif enregistré";
        Notice.Message = $"« {name} » est conservé dans Albums et Récapitulatifs. Il reste modifiable.";
        Notice.Severity = InfoBarSeverity.Success;
        Notice.IsOpen = true;
    }
}




