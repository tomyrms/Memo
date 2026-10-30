using Memo.App.Analysis;
using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Memo.App.Pages;

public sealed partial class SearchPage : Page
{
    private static readonly Dictionary<long, string> DemoOcr = new()
    {
        [3] = "BON APREM 15:44",
        [12] = "15:44",
        [13] = "BON APREM",
    };

    private List<MemoryCard> _results = new();
    private bool _ready;

    public SearchPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { _ready = true; await RunAsync(string.Empty); };
    }

    private async void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_ready)
        {
            return;
        }
        var text = sender.SelectedItem?.Text ?? "Texte";
        TextPanel.Visibility = text == "Texte" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePanel.Visibility = text == "Personnes" ? Visibility.Visible : Visibility.Collapsed;
        VisualPanel.Visibility = text == "Ressemblance" ? Visibility.Visible : Visibility.Collapsed;

        if (text == "Personnes")
        {
            await LoadPeopleAsync();
        }
        else if (text == "Ressemblance")
        {
            PopulateReferences();
        }
    }

    private void OnQueryChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _ = RunAsync(sender.Text);
        }
    }

    private async Task RunAsync(string query)
    {
        var q = query.Trim();
        if (AppState.Current.Library is not null)
        {
            _results = (await AppState.Current.SearchAsync(q)).ToList();
        }
        else
        {
            var cards = AppState.Current.Cards.AsEnumerable();
            if (q.Length > 0)
            {
                cards = cards.Where(c =>
                    (c.LocationLabel?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    c.ExternalKey.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (DemoOcr.TryGetValue(c.Id, out var ocr) && ocr.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (c.LocalDate?.ToString("d MMMM yyyy", AppState.French).Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
            }
            _results = cards.OrderByDescending(c => c.LocalDate).ToList();
        }
        Results.ItemsSource = _results;
        ResultCount.Text = _results.Count == 1 ? "1 souvenir" : $"{_results.Count} souvenirs";
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = q.Length == 0
            ? "Saisissez un mot pour chercher dans les lieux, les dates et les textes reconnus."
            : $"Aucun résultat pour « {q} ». Les textes reconnus proviennent de l'OCR local, pas des légendes Snapchat.";
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MemoryCard card)
        {
            App.MainWindow?.ShowStory(_results, Math.Max(0, _results.IndexOf(card)));
        }
    }

    private async void OnPerson1(object sender, RoutedEventArgs e) => await LoadPeopleAsync();

    private async Task LoadPeopleAsync()
    {
        PeopleList.Children.Clear();
        if (AppState.Current.Library is null)
        {
            PeopleStatus.Text = "Ouvrez une bibliothèque pour charger les groupes de visages.";
            return;
        }

        var groups = await AppState.Current.Library.Analysis.GetPersonGroupsAsync();
        if (groups.Count == 0)
        {
            PeopleStatus.Text = "Aucun groupe pour l'instant. Lancez l'analyse des visages depuis Réglages, puis revenez ici.";
            return;
        }

        PeopleStatus.Text = $"{groups.Count} groupe(s) proposé(s). Les noms restent à confirmer par vous.";
        foreach (var group in groups)
        {
            PeopleList.Children.Add(PersonTile(group));
        }
    }

    private Button PersonTile(PersonGroupInfo group)
    {
        var panel = new StackPanel { Width = 180, Spacing = 6 };
        var cover = new Border
        {
            Height = 120,
            CornerRadius = new CornerRadius(4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MemoPhotoPlaceholderBrush"]
        };
        if (group.CoverPath is not null && File.Exists(group.CoverPath))
        {
            cover.Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(group.CoverPath)),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
            };
        }
        panel.Children.Add(cover);
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(group.Name) ? "Groupe sans nom" : group.Name,
            Style = Widgets.Style("MemoBodyTextStyle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{group.MemoryCount} souvenir(s) · {group.FaceCount} visage(s)",
            Style = Widgets.Style("MemoCaptionTextStyle")
        });

        var button = new Button
        {
            Content = panel,
            Style = Widgets.Style("MemoTileButtonStyle"),
            Padding = new Thickness(0)
        };
        button.Click += async (_, _) => await ShowPersonGroupAsync(group);
        return button;
    }

    private async Task ShowPersonGroupAsync(PersonGroupInfo group)
    {
        if (AppState.Current.Library is null)
        {
            return;
        }
        var ids = await AppState.Current.Library.Analysis.GetMemoriesForPersonAsync(group.Id);
        var cards = AppState.Current.Cards.Where(c => ids.Contains(c.Id)).ToList();
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(Widgets.Secondary($"{cards.Count} souvenir(s) associé(s)."));
        body.Children.Add(Widgets.Caption("Les groupes sont des propositions locales, pas des identités certaines. Corrigez-les dans une prochaine version."));
        var play = new Button
        {
            Content = "Lire ces souvenirs",
            Style = Widgets.Style("MemoPrimaryButtonStyle"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        play.Click += (_, _) =>
        {
            if (cards.Count > 0)
            {
                App.MainWindow?.ShowStory(cards, 0);
            }
        };
        body.Children.Add(play);

        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(group.Name) ? "Groupe de visages" : group.Name,
            Content = body,
            CloseButtonText = "Fermer",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void PopulateReferences()
    {
        ReferenceBox.Items.Clear();
        foreach (var card in AppState.Current.Cards.Where(c => c.Kind == MediaKind.Photo).Take(300))
        {
            ReferenceBox.Items.Add(new ComboBoxItem
            {
                Content = $"{Widgets.DateLabel(card)} · {card.LocationLabel ?? card.ExternalKey}",
                Tag = card.Id
            });
        }
        ReferenceBox.SelectedIndex = ReferenceBox.Items.Count > 0 ? 0 : -1;
        VisualStatus.Text = ReferenceBox.Items.Count == 0
            ? "Aucune photo disponible pour la comparaison."
            : "Choisissez une référence, puis lancez la comparaison.";
        SimilarGrid.ItemsSource = null;
    }

    private async void OnFindSimilar(object sender, RoutedEventArgs e)
    {
        if (AppState.Current.Library is null)
        {
            VisualStatus.Text = "Ouvrez une bibliothèque pour utiliser la recherche visuelle.";
            return;
        }
        if (ReferenceBox.SelectedItem is not ComboBoxItem item || item.Tag is not long referenceId)
        {
            return;
        }

        var hashes = await AppState.Current.Library.Analysis.GetVisualHashesAsync();
        var reference = hashes.FirstOrDefault(h => h.MemoryId == referenceId);
        if (reference.Hash == 0)
        {
            VisualStatus.Text = "Lancez d'abord l'analyse locale depuis Réglages (empreintes visuelles).";
            return;
        }

        var similar = VisualHashService.FindSimilar(reference.Hash, hashes, 12)
            .Where(s => s.MemoryId != referenceId)
            .Select(s => s.MemoryId)
            .ToHashSet();
        var cards = AppState.Current.Cards.Where(c => similar.Contains(c.Id)).ToList();
        SimilarGrid.ItemsSource = cards;
        VisualStatus.Text = cards.Count == 0
            ? "Aucune image proche trouvée."
            : $"{cards.Count} image(s) proche(s), de la plus proche à la plus éloignée.";
    }

    private void OnSimilarClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MemoryCard card && SimilarGrid.ItemsSource is IEnumerable<MemoryCard> list)
        {
            var items = list.ToList();
            App.MainWindow?.ShowStory(items, Math.Max(0, items.IndexOf(card)));
        }
    }
}


