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
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;

    /// <summary>Pause avant d'interroger le catalogue, en millisecondes.</summary>
    private const int SearchDelayMs = 275;

    public SearchPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _ready = true;
            await RunAsync(string.Empty, ++_searchGeneration, CancellationToken.None);
        };
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
            ScheduleSearch(sender.Text);
        }
    }

    /// <summary>
    /// Programme une recherche après une courte pause. Chaque frappe annule la requête
    /// précédente : une réponse lente ne peut donc pas remplacer un texte plus récent.
    /// </summary>
    private void ScheduleSearch(string query)
    {
        CancelPendingSearch();
        var generation = ++_searchGeneration;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearchAsync(query, generation, cts);
    }

    private void CancelPendingSearch()
    {
        // Le jeton est annulé ici ; la tâche qui le possède le libère dans son finally.
        _searchCts?.Cancel();
        _searchCts = null;
    }

    private async Task DebouncedSearchAsync(string query, int generation, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SearchDelayMs, cts.Token);
            await RunAsync(query, generation, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Requête remplacée par une frappe plus récente : aucun résultat n'est appliqué.
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            // Une erreur du catalogue ne doit pas laisser les résultats de la requête précédente.
            if (generation == _searchGeneration)
            {
                _results = new List<MemoryCard>();
                Results.ItemsSource = _results;
                ResultCount.Text = "Recherche indisponible";
                EmptyText.Visibility = Visibility.Visible;
                EmptyText.Text = "La recherche a échoué : " + ex.Message;
            }
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }
        }
    }

    private async Task RunAsync(string query, int generation, CancellationToken ct)
    {
        var q = query.Trim();
        List<MemoryCard> results;
        if (AppState.Current.Library is not null)
        {
            results = (await AppState.Current.SearchAsync(q, ct)).ToList();
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
            results = cards.OrderByDescending(c => c.LocalDate).ToList();
        }

        ct.ThrowIfCancellationRequested();
        if (generation != _searchGeneration)
        {
            // Une requête plus récente est déjà en cours : ce résultat est obsolète.
            return;
        }

        _results = results;
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

    private async Task LoadPeopleAsync()
    {
        PeopleList.Children.Clear();
        PeopleNotice.IsOpen = false;
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

        PeopleStatus.Text = $"{groups.Count} groupe(s) proposé(s). Clic droit sur un groupe pour le renommer, le fusionner ou le supprimer.";
        foreach (var group in groups)
        {
            PeopleList.Children.Add(PersonTile(group));
        }
    }

    /// <summary>Nom affiché d'un groupe : les groupes non nommés restent distinguables par leur couverture.</summary>
    private static string GroupLabel(PersonGroupInfo group)
        => string.IsNullOrWhiteSpace(group.Name) ? "Groupe sans nom" : group.Name;

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
            Text = GroupLabel(group),
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

        // Les corrections restent accessibles au clic droit, sans masquer les souvenirs du groupe.
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Voir les souvenirs" };
        open.Click += async (_, _) => await ShowPersonGroupAsync(group);
        menu.Items.Add(open);
        var rename = new MenuFlyoutItem { Text = "Renommer…" };
        rename.Click += async (_, _) => await RenameGroupAsync(group);
        menu.Items.Add(rename);
        var merge = new MenuFlyoutItem { Text = "Fusionner avec…" };
        merge.Click += async (_, _) => await MergeGroupAsync(group);
        menu.Items.Add(merge);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "Supprimer…" };
        delete.Click += async (_, _) => await DeleteGroupAsync(group);
        menu.Items.Add(delete);
        button.ContextFlyout = menu;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            button,
            $"{GroupLabel(group)}, {group.MemoryCount} souvenir(s), {group.FaceCount} visage(s). Clic droit pour les actions.");
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
        body.Children.Add(Widgets.Caption("Les groupes sont des propositions locales, pas des identités certaines. Renommez, fusionnez ou supprimez-les selon vos souvenirs."));
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

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var rename = new Button { Content = "Renommer", Style = Widgets.Style("MemoSecondaryButtonStyle") };
        var merge = new Button { Content = "Fusionner…", Style = Widgets.Style("MemoSecondaryButtonStyle") };
        var delete = new Button { Content = "Supprimer…", Style = Widgets.Style("MemoQuietButtonStyle") };
        actions.Children.Add(rename);
        actions.Children.Add(merge);
        actions.Children.Add(delete);
        body.Children.Add(actions);

        // L'action est mémorisée puis exécutée après la fermeture : un ContentDialog ne peut pas
        // en ouvrir un autre avant que le premier ait rendu la main.
        string? pending = null;
        var dialog = new ContentDialog
        {
            Title = string.IsNullOrWhiteSpace(group.Name) ? "Groupe de visages" : group.Name,
            Content = body,
            CloseButtonText = "Fermer",
            XamlRoot = XamlRoot
        };
        rename.Click += (_, _) => { pending = "rename"; dialog.Hide(); };
        merge.Click += (_, _) => { pending = "merge"; dialog.Hide(); };
        delete.Click += (_, _) => { pending = "delete"; dialog.Hide(); };
        await dialog.ShowAsync();

        switch (pending)
        {
            case "rename":
                await RenameGroupAsync(group);
                break;
            case "merge":
                await MergeGroupAsync(group);
                break;
            case "delete":
                await DeleteGroupAsync(group);
                break;
        }
    }

    private async Task RenameGroupAsync(PersonGroupInfo group)
    {
        if (AppState.Current.Library is null)
        {
            return;
        }

        var nameBox = new TextBox
        {
            Text = group.Name ?? string.Empty,
            PlaceholderText = "Nom du groupe",
            MaxLength = 80
        };
        var dialog = new ContentDialog
        {
            Title = "Renommer le groupe",
            Content = nameBox,
            PrimaryButtonText = "Enregistrer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowPeopleNotice("Nom requis", "Saisissez un nom non vide pour ce groupe.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await AppState.Current.Library.Analysis.RenamePersonGroupAsync(group.Id, name);
            await LoadPeopleAsync();
            ShowPeopleNotice(
                "Groupe renommé",
                $"Le groupe s'appelle désormais « {name} ». Il reste une proposition, pas une identité vérifiée.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowPeopleNotice("Renommage impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task MergeGroupAsync(PersonGroupInfo group)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            return;
        }

        var groups = await library.Analysis.GetPersonGroupsAsync();
        var others = groups.Where(g => g.Id != group.Id).ToList();
        if (others.Count == 0)
        {
            ShowPeopleNotice("Fusion impossible", "Il faut au moins deux groupes pour fusionner.", InfoBarSeverity.Warning);
            return;
        }

        var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var other in others)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{GroupLabel(other)} · {other.MemoryCount} souvenir(s)",
                Tag = other.Id
            });
        }
        combo.SelectedIndex = 0;

        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(Widgets.Secondary($"Choisissez le groupe à rattacher à « {GroupLabel(group)} »."));
        body.Children.Add(combo);
        body.Children.Add(Widgets.Caption(
            "Les visages du groupe choisi rejoignent le groupe ouvert, puis le groupe choisi est supprimé. " +
            "Les souvenirs et les médias ne sont pas modifiés. Si le groupe ouvert n'a pas de nom, le nom du groupe fusionné est conservé."));

        var dialog = new ContentDialog
        {
            Title = "Fusionner deux groupes",
            Content = body,
            PrimaryButtonText = "Fusionner",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not long sourceId)
        {
            return;
        }

        try
        {
            await library.Analysis.MergePersonGroupsAsync(group.Id, sourceId);
            var sourceName = others.FirstOrDefault(g => g.Id == sourceId)?.Name;
            if (string.IsNullOrWhiteSpace(group.Name) && !string.IsNullOrWhiteSpace(sourceName))
            {
                // Le groupe cible n'avait pas de nom : on conserve celui saisi sur l'autre groupe.
                await library.Analysis.RenamePersonGroupAsync(group.Id, sourceName!);
            }
            await LoadPeopleAsync();
            ShowPeopleNotice(
                "Groupes fusionnés",
                "Les visages ont été réunis dans un seul groupe. Vérifiez la proposition et corrigez-la si nécessaire.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowPeopleNotice("Fusion impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task DeleteGroupAsync(PersonGroupInfo group)
    {
        var library = AppState.Current.Library;
        if (library is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Supprimer ce groupe ?",
            Content =
                $"« {GroupLabel(group)} » ({group.MemoryCount} souvenir(s), {group.FaceCount} visage(s)) sera supprimé. " +
                "Les visages restent détectés mais ne sont plus associés à un groupe. " +
                "Aucun souvenir, média ou album n'est supprimé.",
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await library.Analysis.DeletePersonGroupAsync(group.Id);
            await LoadPeopleAsync();
            ShowPeopleNotice(
                "Groupe supprimé",
                "Les souvenirs et les médias sont intacts. Une nouvelle analyse peut proposer d'autres groupes.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowPeopleNotice("Suppression impossible", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowPeopleNotice(string title, string message, InfoBarSeverity severity)
    {
        PeopleNotice.Title = title;
        PeopleNotice.Message = message;
        PeopleNotice.Severity = severity;
        PeopleNotice.IsOpen = true;
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


