using System.Collections.ObjectModel;
using System.ComponentModel;
using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Memo.App.Pages;

public sealed partial class RecapsPage : Page
{
    private readonly ObservableCollection<RecapSelectionRow> _rows = new();
    private List<MemoryCard> _selection = new();
    private string _title = "Aucune sélection";
    private DateOnly? _periodStart;
    private DateOnly? _periodEnd;
    private CollectionInfo? _loadedRecap;
    private bool _ready;
    private bool _dialogOpen;
    private bool _buildingPeriods;

    public RecapsPage()
    {
        InitializeComponent();
        SelectionList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            if (!_ready)
            {
                BuildPeriods();
                _ready = true;
                Generate();
            }
            RefreshSavedRecaps();
        };
    }

    private void BuildPeriods()
    {
        _buildingPeriods = true;
        try
        {
            PeriodBox.Items.Clear();
            var kind = SelectedKind();
            // Le jour courant suit le fuseau de la bibliothèque, pas celui de Windows.
            var today = AppState.Current.Today;
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
            PeriodBox.SelectedIndex = PeriodBox.Items.Count > 0 ? 0 : -1;
        }
        finally
        {
            _buildingPeriods = false;
        }
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
        if (_ready && !_buildingPeriods)
        {
            Generate();
        }
    }

    private void OnGenerate(object sender, RoutedEventArgs e) => Generate();

    private void Generate()
    {
        var state = AppState.Current;
        var kind = SelectedKind();
        var today = state.Today;
        var item = PeriodBox.SelectedItem as ComboBoxItem;

        string? title = null;
        DateOnly start = default;
        DateOnly end = default;
        if (kind == "year" && item?.Tag is int year)
        {
            title = $"{year} en images";
            start = new DateOnly(year, 1, 1);
            end = new DateOnly(year, 12, 31);
        }
        else if (kind == "month" && item?.Tag is int month)
        {
            var name = new DateTime(today.Year, month, 1).ToString("MMMM yyyy", AppState.French);
            title = char.ToUpper(name[0]) + name[1..];
            start = new DateOnly(today.Year, month, 1);
            end = new DateOnly(today.Year, month, DateTime.DaysInMonth(today.Year, month));
        }
        else if (kind == "season" && item?.Tag is string season)
        {
            var y = today.Year;
            (title, start, end) = season switch
            {
                "spring" => ($"Printemps {y}", new DateOnly(y, 3, 1), new DateOnly(y, 5, 31)),
                "summer" => ($"Été {y}", new DateOnly(y, 6, 1), new DateOnly(y, 8, 31)),
                "autumn" => ($"Automne {y}", new DateOnly(y, 9, 1), new DateOnly(y, 11, 30)),
                // Le 29 février est couvert les années bissextiles.
                _ => ($"Hiver {y - 1}–{y}", new DateOnly(y - 1, 12, 1), new DateOnly(y, 2, DateTime.DaysInMonth(y, 2)))
            };
        }

        if (title is null)
        {
            return;
        }

        // Une génération repart d'une nouvelle sélection non enregistrée.
        var wasLoaded = _loadedRecap is not null;
        _loadedRecap = null;
        _title = title;
        _periodStart = start;
        _periodEnd = end;

        var candidates = state.Cards
            .Where(c => !c.IsExcluded)
            .Where(c => c.Status != MemoryStatus.OverlayOnly)
            .Where(c => c.LocalDate is { } d && d >= start && d <= end)
            // Les dates seulement connues au mois ou à l'année ne sont incluses que si
            // le réglage l'autorise ; elles restent signalées comme approximatives.
            .Where(c => state.Settings.IncludeUncertainDatesInRecaps || c.DatePrecision >= DatePrecision.Day)
            .OrderBy(c => c.LocalDate)
            .ThenBy(c => c.Id)
            .ToList();

        SetSelection(PickBalanced(candidates, 12));
        Notice.IsOpen = false;
        if (wasLoaded)
        {
            // La carte « chargée » ne doit plus être mise en avant après une régénération.
            RefreshSavedRecaps();
        }
    }

    /// <summary>
    /// Règle de sélection : au plus douze souvenirs répartis uniformément sur la période,
    /// favoris en tête de pool, puis remis dans l'ordre chronologique de lecture.
    /// </summary>
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
        return result.OrderBy(c => c.LocalDate).ThenBy(c => c.Id).ToList();
    }

    private void SetSelection(IEnumerable<MemoryCard> cards)
    {
        _selection = cards.ToList();
        _rows.Clear();
        for (var i = 0; i < _selection.Count; i++)
        {
            _rows.Add(new RecapSelectionRow(_selection[i], i, i < _selection.Count - 1));
        }
        Render();
    }

    private void Render()
    {
        var photos = _selection.Count(c => c.Kind == MediaKind.Photo);
        var videos = _selection.Count(c => c.Kind == MediaKind.Video);
        var others = _selection.Count - photos - videos;

        RecapTitle.Text = _selection.Count == 0 && _loadedRecap is null ? "Aucune sélection" : _title;
        RecapMeta.Text = _selection.Count == 0
            ? "Aucun souvenir sélectionné."
            : $"{_selection.Count} souvenir(s) · {photos} photo(s) · {videos} vidéo(s)"
              + (others > 0 ? $" · {others} autre(s)" : "")
              + (_selection.Any(c => c.IsFavorite) ? $" · {_selection.Count(c => c.IsFavorite)} favori(s)" : "");

        CoverageLabel.Text = _periodStart is { } start && _periodEnd is { } end
            ? $"Période : {RecapText.ShortDate(start)} – {RecapText.ShortDate(end)}"
            : "Couverture inconnue";

        CoverageNote.Text = BuildCoverageText();

        var first = _selection.FirstOrDefault();
        var cover = RecapVisuals.Image(first is null ? null : AppState.Current.ThumbnailFor(first.Id));
        CoverImage.Source = cover;
        CoverEmpty.Text = _selection.Count == 0 ? "Aucun souvenir sélectionné" : "Aperçu à générer";
        CoverEmpty.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;
        CoverBadge.Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CoverCount.Text = $"+{_selection.Count - 1}";

        SelectionSummary.Text = _selection.Count == 0
            ? "Générez une période ou ajoutez des souvenirs datés."
            : $"{_selection.Count} élément(s) · l’ordre ci-dessous est celui de la Story.";
        SelectionEmpty.Visibility = _selection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionList.Visibility = _selection.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        PreviewOrigin.Text = _loadedRecap is null
            ? "Nouvelle sélection — l’enregistrement créera une collection."
            : $"Récapitulatif enregistré « {_loadedRecap.Name} » — mise à jour ou nouvelle collection possible.";
        PreviewCounts.Text = BuildCountsText();
        PreviewDuration.Text = BuildDurationText();
        PreviewCoverage.Text = BuildCoverageText();
        PreviewNotes.Text = BuildNotesText();
    }

    private string BuildCountsText()
    {
        if (_selection.Count == 0)
        {
            return "Aucun souvenir : la Story serait vide.";
        }
        var photos = _selection.Count(c => c.Kind == MediaKind.Photo);
        var videos = _selection.Count(c => c.Kind == MediaKind.Video);
        var others = _selection.Count - photos - videos;
        return $"{_selection.Count} souvenir(s) · {photos} photo(s) · {videos} vidéo(s)"
               + (others > 0 ? $" · {others} autre(s)" : "");
    }

    private string BuildDurationText()
    {
        if (_selection.Count == 0)
        {
            return "Durée estimée : aucune.";
        }
        var photoSeconds = Math.Max(1, AppState.Current.Settings.PhotoDurationSeconds);
        var total = 0d;
        var unknownVideos = 0;
        foreach (var card in _selection)
        {
            if (card.Kind == MediaKind.Video)
            {
                if (card.DurationMs is > 0)
                {
                    total += card.DurationMs.Value / 1000d;
                }
                else
                {
                    total += photoSeconds;
                    unknownVideos++;
                }
            }
            else if (card.Kind == MediaKind.Photo)
            {
                total += photoSeconds;
            }
        }

        var text = $"Durée estimée en Story : {RecapText.FormatDuration(TimeSpan.FromSeconds(total))}";
        text += $" · photos à {photoSeconds} s";
        if (unknownVideos > 0)
        {
            text += $" · {unknownVideos} vidéo(s) sans durée connue comptée(s) à {photoSeconds} s";
        }
        return text;
    }

    private string BuildCoverageText()
    {
        var dated = _selection.Where(c => c.LocalDate is not null).ToList();
        var undated = _selection.Count - dated.Count;
        if (dated.Count == 0)
        {
            return undated > 0
                ? $"Couverture inconnue : {undated} souvenir(s) sans date, non compté(s)."
                : "Couverture inconnue : aucun souvenir daté.";
        }

        var first = dated.Min(c => c.LocalDate!.Value);
        var last = dated.Max(c => c.LocalDate!.Value);
        var approximate = dated.Any(c => c.DatePrecision < DatePrecision.Day);
        var days = last.DayNumber - first.DayNumber + 1;
        var text = $"{(approximate ? "Couverture indicative" : "Couverture")} : "
                   + $"{RecapText.ShortDate(first)} → {RecapText.ShortDate(last)} ({days} jour(s))";
        if (approximate)
        {
            text += " · certaines dates sont approximatives";
        }
        if (undated > 0)
        {
            text += $" · {undated} sans date non compté(s)";
        }
        return text;
    }

    private string BuildNotesText()
    {
        var notes = new List<string>();
        if (_selection.Count == 0)
        {
            notes.Add("Ajoutez ou générez des souvenirs avant d’enregistrer.");
        }

        var undated = _selection.Count(c => c.LocalDate is null);
        if (undated > 0)
        {
            notes.Add($"{undated} souvenir(s) sans date : aucune date n’est inventée et ils ne comptent pas dans la couverture.");
        }

        if (_selection.Any(c => c.LocalDate is not null && c.DatePrecision < DatePrecision.Day))
        {
            notes.Add("Les dates connues seulement au mois ou à l’année sont signalées comme approximatives.");
        }

        var otherKinds = _selection.Count(c => c.Kind is not MediaKind.Photo and not MediaKind.Video);
        if (otherKinds > 0)
        {
            notes.Add($"{otherKinds} élément(s) sans photo ni vidéo ne sont pas comptés dans la durée estimée.");
        }

        notes.Add("Les souvenirs exclus et les médias non datés ne sont jamais ajoutés lors d’une génération.");
        if (_loadedRecap is not null)
        {
            notes.Add("La mise à jour remplace le contenu enregistré ; « Nouveau récapitulatif » conserve l’original.");
        }
        return string.Join(" ", notes);
    }

    private void RefreshSavedRecaps()
    {
        SavedRecaps.Children.Clear();
        var recaps = AppState.Current.Recaps;
        NoSavedRecaps.Visibility = recaps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var recap in recaps)
        {
            var isLoaded = recap.Id == _loadedRecap?.Id;
            var panel = new StackPanel { Width = 220, Spacing = 6 };
            var cover = new Border
            {
                Height = 120,
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["MemoPhotoPlaceholderBrush"]
            };
            var image = RecapVisuals.Image(recap.CoverThumbnail);
            if (image is not null)
            {
                cover.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
            }
            panel.Children.Add(cover);
            panel.Children.Add(new TextBlock
            {
                Text = recap.Name,
                Style = Widgets.Style("MemoBodyTextStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            });
            var range = recap.FirstDate is { } first && recap.LastDate is { } last
                ? $"{RecapText.ShortDate(first)} – {RecapText.ShortDate(last)}"
                : "dates incomplètes";
            panel.Children.Add(new TextBlock
            {
                Text = $"{recap.ItemCount} souvenir(s) · {range}",
                Style = Widgets.Style("MemoCaptionTextStyle")
            });
            if (isLoaded)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Chargé pour modification",
                    Style = Widgets.Style("MemoCaptionTextStyle"),
                    Foreground = Widgets.Brush("MemoAccentBrush")
                });
            }

            var wrapper = new Border
            {
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(isLoaded ? 2 : 0),
                BorderBrush = isLoaded ? Widgets.Brush("MemoAccentBrush") : null
            };
            wrapper.Child = panel;

            var button = new Button
            {
                Content = wrapper,
                Style = Widgets.Style("MemoTileButtonStyle"),
                Padding = new Thickness(0)
            };
            AutomationProperties.SetName(
                button,
                $"Recharger le récapitulatif {recap.Name}, {recap.ItemCount} souvenirs");
            AutomationProperties.SetHelpText(
                button,
                "Ouvre la sélection enregistrée dans l’éditeur pour la modifier ou la relire en Story.");
            button.Click += async (_, _) => await LoadRecapAsync(recap);
            SavedRecaps.Children.Add(button);
        }
    }

    private async Task LoadRecapAsync(CollectionInfo recap)
    {
        var savedIndex = AppState.Current.Recaps.IndexOf(recap);
        var items = await AppState.Current.GetAlbumItemsAsync(recap.Id);

        // Une exclusion reste prioritaire, même dans une collection déjà enregistrée.
        var filtered = new List<MemoryCard>();
        var seen = new HashSet<long>();
        foreach (var card in items)
        {
            if (card.IsExcluded || !seen.Add(card.Id))
            {
                continue;
            }
            filtered.Add(card);
        }
        var excludedCount = items.Count - filtered.Count;

        _loadedRecap = recap;
        _title = recap.Name;
        var dated = filtered.Where(c => c.LocalDate is not null).ToList();
        _periodStart = dated.Count > 0 ? dated.Min(c => c.LocalDate!.Value) : null;
        _periodEnd = dated.Count > 0 ? dated.Max(c => c.LocalDate!.Value) : null;
        SetSelection(filtered);
        RefreshSavedRecaps();

        var undated = filtered.Count(c => c.LocalDate is null);
        var message = $"{filtered.Count} souvenir(s) rechargé(s) dans l’ordre enregistré.";
        if (excludedCount > 0)
        {
            message += $" {excludedCount} souvenir(s) exclu(s) ont été retirés de la sélection.";
        }
        if (undated > 0)
        {
            message += $" {undated} souvenir(s) sans date restent affichés sans date inventée et hors couverture.";
        }
        ShowNotice("Récapitulatif rechargé", message, InfoBarSeverity.Informational);
        FocusSavedRecap(savedIndex);
    }

    private void FocusSavedRecap(int index)
    {
        if (index < 0)
        {
            return;
        }
        // Le rendu des cartes vient d'être reconstruit : on rend le focus à la carte choisie.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (index < SavedRecaps.Children.Count && SavedRecaps.Children[index] is Button button)
            {
                button.Focus(FocusState.Keyboard);
            }
        });
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0)
        {
            ShowNotice("Story vide", "Ajoutez au moins un souvenir avant de lancer la lecture.", InfoBarSeverity.Warning);
            return;
        }
        App.MainWindow?.ShowStory(_selection, 0);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveRow(sender, -1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveRow(sender, 1);

    private void MoveRow(object sender, int offset)
    {
        if (sender is not FrameworkElement { DataContext: RecapSelectionRow row })
        {
            return;
        }
        var index = _selection.FindIndex(c => c.Id == row.Card.Id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _selection.Count)
        {
            return;
        }

        (_selection[index], _selection[target]) = (_selection[target], _selection[index]);
        _rows.Move(index, target);
        RefreshRowPositions();
        Render();
        Notice.IsOpen = false;
        FocusRow(target, offset < 0 ? "up" : "down");
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecapSelectionRow row })
        {
            return;
        }
        var index = _selection.FindIndex(c => c.Id == row.Card.Id);
        if (index < 0)
        {
            return;
        }

        _selection.RemoveAt(index);
        _rows.RemoveAt(index);
        RefreshRowPositions();
        Render();
        Notice.IsOpen = false;
        if (_rows.Count > 0)
        {
            FocusRow(Math.Min(index, _rows.Count - 1), "remove");
        }
        else
        {
            AddButton.Focus(FocusState.Keyboard);
        }
    }

    private void RefreshRowPositions()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].UpdatePosition(i, i < _rows.Count - 1);
        }
    }

    private void FocusRow(int index, string tag)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }
        var row = _rows[index];
        SelectionList.ScrollIntoView(row);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (SelectionList.ContainerFromItem(row) is DependencyObject container
                && FindButton(container, tag) is { } button)
            {
                button.Focus(FocusState.Keyboard);
            }
        });
    }

    private static Button? FindButton(DependencyObject root, string tag)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && button.Tag as string == tag)
            {
                return button;
            }
            if (FindButton(child, tag) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private void OnSortByDate(object sender, RoutedEventArgs e)
    {
        if (_selection.Count < 2)
        {
            return;
        }
        SetSelection(_selection
            .OrderBy(c => c.LocalDate ?? DateOnly.MaxValue)
            .ThenBy(c => c.Id));
        ShowNotice(
            "Ordre chronologique rétabli",
            "Les souvenirs datés sont classés du plus ancien au plus récent ; les souvenirs sans date restent en fin de sélection.",
            InfoBarSeverity.Informational);
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_periodStart is null || _periodEnd is null)
        {
            ShowNotice(
                "Période inconnue",
                "Aucune date ne permet de proposer des souvenirs à ajouter. Chargez un récapitulatif daté ou utilisez une période.",
                InfoBarSeverity.Warning);
            return;
        }

        var selectedIds = _selection.Select(c => c.Id).ToHashSet();
        var candidates = AppState.Current.Cards
            .Where(c => !c.IsExcluded && !selectedIds.Contains(c.Id))
            .Where(c => c.Status != MemoryStatus.OverlayOnly)
            .Where(c => c.LocalDate is { } d && d >= _periodStart.Value && d <= _periodEnd.Value)
            .Where(c => AppState.Current.Settings.IncludeUncertainDatesInRecaps || c.DatePrecision >= DatePrecision.Day)
            .OrderBy(c => c.LocalDate)
            .ThenBy(c => c.Id)
            .Select(c => new RecapCandidateRow(c))
            .ToList();

        if (candidates.Count == 0)
        {
            ShowNotice(
                "Aucun autre souvenir",
                "Tous les souvenirs datés de cette période sont déjà sélectionnés, exclus ou sans date exploitable.",
                InfoBarSeverity.Informational);
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            Height = 360,
            ItemsSource = candidates,
            ItemTemplate = (DataTemplate)Resources["CandidateItemTemplate"]
        };
        var content = new StackPanel { Spacing = 8, Width = 520 };
        content.Children.Add(Widgets.Caption(
            "Sélectionnez un ou plusieurs souvenirs. Utilisez Ctrl ou Maj pour étendre la sélection ; l’ordre final reste modifiable dans la liste."));
        content.Children.Add(list);

        var dialog = new ContentDialog
        {
            Title = "Ajouter des souvenirs",
            Content = content,
            PrimaryButtonText = "Ajouter",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var additions = list.SelectedItems.OfType<RecapCandidateRow>()
            .Select(r => r.Card)
            .OrderBy(c => c.LocalDate)
            .ThenBy(c => c.Id)
            .ToList();
        if (additions.Count == 0)
        {
            return;
        }

        SetSelection(_selection.Concat(additions));
        ShowNotice(
            "Souvenirs ajoutés",
            $"{additions.Count} souvenir(s) ajouté(s) en fin de sélection. Les boutons de chaque ligne permettent d’ajuster l’ordre.",
            InfoBarSeverity.Success);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0)
        {
            ShowNotice(
                "Rien à enregistrer",
                "Ajoutez au moins un souvenir daté avant de créer un récapitulatif.",
                InfoBarSeverity.Warning);
            return;
        }

        var suggestedName = _loadedRecap?.Name ?? $"{_title} · récapitulatif";
        var nameBox = new TextBox
        {
            Text = suggestedName,
            Header = "Nom du récapitulatif",
            MaxLength = 80,
            Style = Widgets.Style("MemoTextBoxStyle")
        };
        nameBox.Loaded += (_, _) =>
        {
            nameBox.Focus(FocusState.Programmatic);
            nameBox.SelectAll();
        };

        var content = new StackPanel { Spacing = 12, Width = 560 };
        content.Children.Add(nameBox);
        content.Children.Add(BuildPreviewContent());

        // La prévisualisation peut dépasser la hauteur de la fenêtre avec une
        // sélection fournie : le dialogue reste parcourable au clavier.
        var dialogContent = new ScrollViewer
        {
            Content = content,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var updating = _loadedRecap is not null;
        var dialog = new ContentDialog
        {
            Title = "Prévisualiser et enregistrer",
            Content = dialogContent,
            PrimaryButtonText = updating ? "Mettre à jour" : "Enregistrer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (updating)
        {
            dialog.SecondaryButtonText = "Nouveau récapitulatif";
        }

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.None)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? suggestedName : nameBox.Text.Trim();
        var ids = _selection.Select(c => c.Id).ToList();
        var update = updating && result == ContentDialogResult.Primary;
        CollectionInfo? saved;
        try
        {
            saved = update
                ? await AppState.Current.UpdateRecapAsync(_loadedRecap!.Id, name, ids)
                : await AppState.Current.SaveRecapAsync(name, ids);
        }
        catch (Exception ex)
        {
            ShowNotice("Enregistrement impossible", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (saved is null)
        {
            ShowNotice(
                "Enregistrement impossible",
                "Le catalogue n’a pas renvoyé la collection enregistrée ; la sélection reste disponible à l’écran.",
                InfoBarSeverity.Error);
            return;
        }

        _loadedRecap = saved;
        _title = name;
        Render();
        RefreshSavedRecaps();
        ShowNotice(
            update ? "Récapitulatif mis à jour" : "Récapitulatif enregistré",
            $"« {name} » contient {_selection.Count} souvenir(s) dans l’ordre affiché. "
            + "Il reste rechargeable, modifiable et lisible en Story.",
            InfoBarSeverity.Success);
    }

    private StackPanel BuildPreviewContent()
    {
        var panel = new StackPanel { Spacing = 8, Width = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = BuildCountsText(),
            Style = Widgets.Style("MemoBodyTextStyle")
        });

        var list = new StackPanel();
        for (var i = 0; i < _selection.Count; i++)
        {
            var card = _selection[i];
            var row = new Grid { Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var position = Widgets.Caption($"{i + 1}.");
            position.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(position);

            var date = new TextBlock
            {
                Text = RecapText.Date(card),
                Style = Widgets.Style("MemoSecondaryTextStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(date, 1);
            row.Children.Add(date);

            var detail = new TextBlock
            {
                Text = RecapText.Detail(card, includeDuration: false),
                Style = Widgets.Style("MemoBodyTextStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(detail, 2);
            row.Children.Add(detail);

            var duration = Widgets.Caption(RecapText.Clock(card));
            duration.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(duration, 3);
            row.Children.Add(duration);
            list.Children.Add(row);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Ordre de lecture",
            Style = Widgets.Style("MemoSectionTextStyle")
        });
        panel.Children.Add(list);
        panel.Children.Add(new TextBlock
        {
            Text = BuildDurationText(),
            Style = Widgets.Style("MemoSecondaryTextStyle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = BuildCoverageText(),
            Style = Widgets.Style("MemoSecondaryTextStyle")
        });
        panel.Children.Add(new TextBlock
        {
            Text = BuildNotesText(),
            Style = Widgets.Style("MemoCaptionTextStyle")
        });
        return panel;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen)
        {
            return ContentDialogResult.None;
        }
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}

/// <summary>
/// Ligne de la sélection : porte les libellés et l'état des boutons de déplacement.
/// </summary>
public sealed class RecapSelectionRow : INotifyPropertyChanged
{
    private int _position;
    private bool _canMoveDown;

    public RecapSelectionRow(MemoryCard card, int position, bool canMoveDown)
    {
        Card = card;
        _position = position;
        _canMoveDown = canMoveDown;
        Thumbnail = RecapVisuals.Image(AppState.Current.ThumbnailFor(card.Id));
    }

    public MemoryCard Card { get; }

    public ImageSource? Thumbnail { get; }

    public bool CanMoveUp => _position > 0;

    public bool CanMoveDown => _canMoveDown;

    public string PositionLabel => (_position + 1).ToString("00");

    public string DateLabel => RecapText.Date(Card);

    public string DetailLabel => RecapText.Detail(Card, includeDuration: false);

    public string DurationLabel => RecapText.Clock(Card);

    public string MoveUpLabel => $"Monter le souvenir du {DateLabel}, actuellement en position {_position + 1}";

    public string MoveDownLabel => $"Descendre le souvenir du {DateLabel}, actuellement en position {_position + 1}";

    public string RemoveLabel => $"Retirer le souvenir du {DateLabel} de la sélection";

    public override string ToString() => $"{PositionLabel}. {DateLabel} — {DetailLabel}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdatePosition(int position, bool canMoveDown)
    {
        var positionChanged = _position != position;
        _position = position;
        if (positionChanged)
        {
            Raise(nameof(PositionLabel));
            Raise(nameof(MoveUpLabel));
            Raise(nameof(MoveDownLabel));
        }
        Raise(nameof(CanMoveUp));
        if (_canMoveDown != canMoveDown)
        {
            _canMoveDown = canMoveDown;
            Raise(nameof(CanMoveDown));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Candidat proposé dans la boîte d'ajout.
/// </summary>
public sealed class RecapCandidateRow
{
    public RecapCandidateRow(MemoryCard card)
    {
        Card = card;
        DateLabel = RecapText.Date(card);
        DetailLabel = RecapText.Detail(card);
    }

    public MemoryCard Card { get; }

    public string DateLabel { get; }

    public string DetailLabel { get; }

    public override string ToString() => $"{DateLabel} — {DetailLabel}";
}

internal static class RecapText
{
    public static string Date(MemoryCard card)
    {
        if (card.LocalDate is not { } date)
        {
            return "Date inconnue";
        }

        var text = card.DatePrecision switch
        {
            DatePrecision.Year => date.ToString("yyyy", AppState.French),
            DatePrecision.Month => Capitalize(date.ToString("MMMM yyyy", AppState.French)),
            _ => date.ToString("d MMM yyyy", AppState.French)
        };
        return card.DatePrecision >= DatePrecision.Day ? text : $"vers {text}";
    }

    public static string Detail(MemoryCard card, bool includeDuration = true)
    {
        var parts = new List<string> { Kind(card) };
        if (includeDuration && card.DurationMs is > 0 && card.Kind == MediaKind.Video)
        {
            parts.Add(FormatClock(TimeSpan.FromMilliseconds(card.DurationMs.Value)));
        }
        if (card.IsFavorite)
        {
            parts.Add("favori");
        }
        if (card.LocalDate is not null && card.DatePrecision < DatePrecision.Day)
        {
            parts.Add("date approximative");
        }
        if (!string.IsNullOrWhiteSpace(card.LocationLabel))
        {
            parts.Add(card.LocationLabel);
        }
        if (card.Status == MemoryStatus.OverlayOnly)
        {
            parts.Add("calque seul");
        }
        else if (card.OverlayCount > 0)
        {
            parts.Add("calque");
        }
        return string.Join(" · ", parts);
    }

    public static string Clock(MemoryCard card)
        => card.Kind == MediaKind.Video && card.DurationMs is > 0
            ? FormatClock(TimeSpan.FromMilliseconds(card.DurationMs.Value))
            : string.Empty;

    public static string Kind(MemoryCard card) => card.Kind switch
    {
        MediaKind.Video => "Vidéo",
        MediaKind.Photo => "Photo",
        _ => "Autre"
    };

    public static string ShortDate(DateOnly date) => date.ToString("d MMM yyyy", AppState.French);

    public static string FormatClock(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes} min {span.Seconds:00} s";
        }
        return $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))} s";
    }

    private static string Capitalize(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0], AppState.French) + value[1..];
}

internal static class RecapVisuals
{
    public static BitmapImage? Image(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            var isPackaged = path.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase);
            if (!isPackaged && !File.Exists(path))
            {
                return null;
            }
            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }
}
