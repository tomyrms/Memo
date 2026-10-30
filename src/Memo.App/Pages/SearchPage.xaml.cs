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

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_ready)
        {
            return;
        }
        var text = sender.SelectedItem?.Text ?? "Texte";
        TextPanel.Visibility = text == "Texte" ? Visibility.Visible : Visibility.Collapsed;
        PeoplePanel.Visibility = text == "Personnes" ? Visibility.Visible : Visibility.Collapsed;
        VisualPanel.Visibility = text == "Ressemblance" ? Visibility.Visible : Visibility.Collapsed;
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

    private async void OnPerson1(object sender, RoutedEventArgs e) => await ShowPersonAsync("Personne 1", new long[] { 2, 7, 12, 18 }, 8);
    private async void OnPerson2(object sender, RoutedEventArgs e) => await ShowPersonAsync("Personne 2", new long[] { 9, 13, 19 }, 5);

    private async Task ShowPersonAsync(string name, long[] ids, int claimed)
    {
        var cards = AppState.Current.Cards.Where(c => ids.Contains(c.Id)).ToList();
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(Widgets.Secondary($"{cards.Count} souvenir(s) visibles dans cette démonstration sur {claimed} annoncés."));
        body.Children.Add(Widgets.Caption("Les groupes sont des propositions locales, pas des identités certaines."));
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
            Title = name,
            Content = body,
            CloseButtonText = "Fermer",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnEnableVisual(object sender, RoutedEventArgs e)
    {
        VisualStatus.Text = "Le calcul local sera disponible après activation dans Réglages. Aucun résultat n'est affiché tant que l'analyse n'a pas eu lieu.";
    }
}


