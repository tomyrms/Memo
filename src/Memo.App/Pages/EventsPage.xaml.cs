using Memo.App.Services;
using Memo.App.Ui;
using Memo.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Memo.App.Pages;

/// <summary>
/// Écran des propositions d'événements. Les cartes restent des suggestions :
/// elles peuvent être lues, enregistrées comme collection locale ou ignorées.
/// </summary>
public sealed partial class EventsPage : Page
{
    private readonly List<EventSuggestion> _suggestions = new();
    private readonly List<CollectionInfo> _savedEvents = new();
    private const int SuggestionPageSize = 20;
    private int _visibleSuggestions = SuggestionPageSize;
    private bool _loading;
    private string? _notice;
    private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Success;

    public EventsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await BuildAsync();
    }

    private async Task BuildAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            Root.Children.Clear();
            Root.Children.Add(BuildHeader());
            Root.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Left,
            });

            var suggestions = await EventSuggestionService.Current.BuildSuggestionsAsync();
            var saved = await EventSuggestionService.Current.GetSavedEventsAsync();

            _suggestions.Clear();
            _suggestions.AddRange(suggestions);
            _savedEvents.Clear();
            _savedEvents.AddRange(saved);
            _visibleSuggestions = SuggestionPageSize;
            Render();
        }
        catch (Exception ex)
        {
            Root.Children.Clear();
            Root.Children.Add(BuildHeader());
            Root.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "Événements indisponibles",
                Message = ex.Message,
            });
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render()
    {
        Root.Children.Clear();
        Root.Children.Add(BuildHeader());

        if (_notice is not null)
        {
            Root.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = true,
                Severity = _noticeSeverity,
                Title = "Événements",
                Message = _notice,
            });
            _notice = null;
        }

        Root.Children.Add(BuildIntro());
        Root.Children.Add(BuildSuggestionsSection());
        Root.Children.Add(BuildSavedSection());
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Widgets.Title("Événements"));
        titles.Children.Add(Widgets.Secondary(
            "Des propositions de regroupements temporels, à relire avant de les conserver."));
        grid.Children.Add(titles);

        if (EventSuggestionService.Current.HasIgnored)
        {
            var reset = new Button
            {
                Content = "Réinitialiser les ignorées",
                Style = Widgets.Style("MemoQuietButtonStyle"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            reset.Click += async (_, _) => await ResetIgnoredAsync();
            Grid.SetColumn(reset, 1);
            grid.Children.Add(reset);
        }

        return grid;
    }

    private static UIElement BuildIntro()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Propositions locales, pas des catégories Snapchat",
            Style = Widgets.Style("MemoSectionTextStyle"),
        });
        panel.Children.Add(Widgets.Secondary(
            "Memo rapproche des souvenirs datés consécutifs : moins de six heures d'écart, " +
            "même journée ou même week-end. Les éléments masqués sont exclus et deux espaces " +
            "distincts ne sont jamais regroupés. Aucun historique de localisation n'est utilisé : " +
            "seuls les lieux explicitement associés à un souvenir sont repris."));
        panel.Children.Add(Widgets.Caption(
            "Si l'heure manque, seule la date est utilisée. Aucune vidéo n'est fusionnée. " +
            "Enregistrer une proposition crée une collection locale de références ; " +
            "les fichiers sources ne sont ni déplacés ni renommés."));
        return Widgets.Card(panel, new Thickness(16));
    }

    private UIElement BuildSuggestionsSection()
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Widgets.SectionHeader($"Suggestions ({_suggestions.Count})"));

        if (_suggestions.Count == 0)
        {
            section.Children.Add(Widgets.EmptyState(
                "Aucun événement à proposer",
                "Il faut au moins deux souvenirs datés consécutifs, hors éléments masqués. " +
                "Une date corrigée depuis la bibliothèque peut faire apparaître une proposition.",
                "Ouvrir la bibliothèque",
                () => App.MainWindow?.Navigate("library")));
            return section;
        }

        foreach (var suggestion in _suggestions.Take(_visibleSuggestions))
        {
            section.Children.Add(BuildSuggestionCard(suggestion));
        }

        if (_suggestions.Count > _visibleSuggestions)
        {
            var remaining = _suggestions.Count - _visibleSuggestions;
            var more = new Button
            {
                Content = $"Afficher {Math.Min(SuggestionPageSize, remaining)} proposition(s) de plus",
                Style = Widgets.Style("MemoSecondaryButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };
            more.Click += (_, _) =>
            {
                _visibleSuggestions = Math.Min(_suggestions.Count, _visibleSuggestions + SuggestionPageSize);
                Render();
            };
            section.Children.Add(more);
        }

        section.Children.Add(Widgets.Caption(
            "Une proposition traitée disparaît de cette liste. Les souvenirs, leurs dates " +
            "et leurs fichiers restent inchangés."));
        return section;
    }

    private UIElement BuildSuggestionCard(EventSuggestion suggestion)
    {
        var grid = new Grid { ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var coverGrid = new Grid
        {
            Width = 300,
            Height = 190,
            VerticalAlignment = VerticalAlignment.Top,
        };
        coverGrid.Children.Add(CreateCover(suggestion.CoverPath, 300, 190, "Aperçu indisponible"));

        var badge = new Border
        {
            Margin = new Thickness(8),
            Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Widgets.Brush("MemoAccentBrush"),
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock
            {
                Text = "Proposition",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Widgets.Brush("MemoAccentTextBrush"),
            },
        };
        coverGrid.Children.Add(badge);
        grid.Children.Add(coverGrid);

        var content = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(Widgets.Caption("Titre proposé"));
        content.Children.Add(new TextBlock
        {
            Text = suggestion.Title,
            Style = Widgets.Style("MemoSubtitleTextStyle"),
        });
        content.Children.Add(new TextBlock
        {
            Text = suggestion.PeriodLabel,
            Style = Widgets.Style("MemoBodyTextStyle"),
        });
        content.Children.Add(Widgets.Caption(suggestion.CountLabel));
        content.Children.Add(Widgets.Caption(suggestion.PlaceLabel));
        content.Children.Add(BuildThumbnailStrip(suggestion));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var play = new Button
        {
            Content = "Lire la Story",
            Style = Widgets.Style("MemoPrimaryButtonStyle"),
        };
        play.Click += (_, _) => App.MainWindow?.ShowStory(suggestion.Memories, 0);

        var save = new Button
        {
            Content = "Enregistrer comme collection",
            Style = Widgets.Style("MemoSecondaryButtonStyle"),
        };
        save.Click += async (_, _) => await SaveSuggestionAsync(suggestion, save);

        var ignore = new Button
        {
            Content = "Ignorer",
            Style = Widgets.Style("MemoQuietButtonStyle"),
        };
        ignore.Click += async (_, _) => await IgnoreSuggestionAsync(suggestion, ignore);

        actions.Children.Add(play);
        actions.Children.Add(save);
        actions.Children.Add(ignore);
        content.Children.Add(actions);

        content.Children.Add(Widgets.Caption(
            "Regroupement local par dates proches. Aucun historique de localisation, " +
            "aucune fausse position et aucune fusion de vidéos."));

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return Widgets.Card(grid, new Thickness(16));
    }

    private static UIElement BuildThumbnailStrip(EventSuggestion suggestion)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var count = Math.Min(6, suggestion.Memories.Count);
        for (var index = 0; index < count; index++)
        {
            var card = suggestion.Memories[index];
            var thumb = CreateCover(
                AppState.Current.ThumbnailFor(card.Id),
                56,
                56,
                card.Kind == MediaKind.Video ? "Vidéo" : "Photo");

            var button = new Button
            {
                Content = thumb,
                Style = Widgets.Style("MemoTileButtonStyle"),
                Width = 56,
                Height = 56,
                Padding = new Thickness(0),
            };
            var storyIndex = index;
            button.Click += (_, _) => App.MainWindow?.ShowStory(suggestion.Memories, storyIndex);
            AutomationProperties.SetName(
                button,
                $"{Widgets.DateLabel(card)} — {(card.Kind == MediaKind.Video ? "vidéo" : "photo")}");
            panel.Children.Add(button);
        }

        if (suggestion.Memories.Count > count)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"+{suggestion.Memories.Count - count}",
                Style = Widgets.Style("MemoCaptionTextStyle"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            });
        }

        return panel;
    }

    private UIElement BuildSavedSection()
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Widgets.SectionHeader($"Événements enregistrés ({_savedEvents.Count})"));

        if (_savedEvents.Count == 0)
        {
            section.Children.Add(Widgets.Caption(
                "Aucun événement enregistré. Une proposition enregistrée devient une " +
                "collection locale de références, renommable et supprimable depuis cette page."));
            return section;
        }

        foreach (var collection in _savedEvents)
        {
            section.Children.Add(BuildSavedRow(collection));
        }

        return section;
    }

    private UIElement BuildSavedRow(CollectionInfo collection)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(CreateCover(collection.CoverThumbnail, 64, 64, "—"));

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = collection.Name,
            Style = Widgets.Style("MemoBodyTextStyle"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(Widgets.Caption($"{collection.ItemCount} souvenir(s) · collection locale"));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var play = new Button
        {
            Content = "Lire",
            Style = Widgets.Style("MemoSecondaryButtonStyle"),
        };
        play.Click += async (_, _) => await PlaySavedAsync(collection);

        var rename = new Button
        {
            Content = "Renommer",
            Style = Widgets.Style("MemoQuietButtonStyle"),
        };
        rename.Click += async (_, _) => await RenameSavedAsync(collection);

        var delete = new Button
        {
            Content = "Supprimer",
            Style = Widgets.Style("MemoQuietButtonStyle"),
        };
        delete.Click += async (_, _) => await DeleteSavedAsync(collection);

        actions.Children.Add(play);
        actions.Children.Add(rename);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return Widgets.Card(grid, new Thickness(12));
    }

    private async Task SaveSuggestionAsync(EventSuggestion suggestion, Button button)
    {
        var nameBox = new TextBox
        {
            Text = suggestion.Title,
            Header = "Nom de la collection",
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(Widgets.Secondary($"{suggestion.PeriodLabel} · {suggestion.CountLabel}"));
        panel.Children.Add(nameBox);
        panel.Children.Add(Widgets.Caption(
            "La collection référence les souvenirs existants. Les fichiers ne sont ni " +
            "déplacés ni fusionnés et aucun lieu n'est inventé."));

        var dialog = new ContentDialog
        {
            Title = "Enregistrer comme collection",
            Content = panel,
            PrimaryButtonText = "Enregistrer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = suggestion.Title;
        }

        button.IsEnabled = false;
        try
        {
            var saved = await EventSuggestionService.Current.SaveAsCollectionAsync(suggestion, name);
            _notice = $"« {saved.Name} » est enregistré comme collection locale " +
                      $"({saved.ItemCount} souvenir(s)).";
            _noticeSeverity = InfoBarSeverity.Success;
            await BuildAsync();
        }
        catch (Exception ex)
        {
            _notice = $"Impossible d'enregistrer la collection : {ex.Message}";
            _noticeSeverity = InfoBarSeverity.Error;
            await BuildAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task IgnoreSuggestionAsync(EventSuggestion suggestion, Button button)
    {
        button.IsEnabled = false;
        try
        {
            await EventSuggestionService.Current.IgnoreAsync(suggestion);
            _notice = "Suggestion ignorée. Utilisez « Réinitialiser les ignorées » " +
                      "pour la faire réapparaître.";
            _noticeSeverity = InfoBarSeverity.Informational;
            await BuildAsync();
        }
        catch (Exception ex)
        {
            _notice = $"Impossible d'ignorer la suggestion : {ex.Message}";
            _noticeSeverity = InfoBarSeverity.Error;
            await BuildAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task ResetIgnoredAsync()
    {
        try
        {
            await EventSuggestionService.Current.ClearIgnoredAsync();
            _notice = "Les propositions ignorées sont de nouveau visibles.";
            _noticeSeverity = InfoBarSeverity.Success;
        }
        catch (Exception ex)
        {
            _notice = $"Impossible de réinitialiser les suggestions ignorées : {ex.Message}";
            _noticeSeverity = InfoBarSeverity.Error;
        }

        await BuildAsync();
    }

    private async Task PlaySavedAsync(CollectionInfo collection)
    {
        try
        {
            var items = await EventSuggestionService.Current.GetSavedEventItemsAsync(collection);
            if (items.Count == 0)
            {
                _notice = "Cet événement ne contient plus de souvenir accessible.";
                _noticeSeverity = InfoBarSeverity.Warning;
                Render();
                return;
            }

            App.MainWindow?.ShowStory(items, 0);
        }
        catch (Exception ex)
        {
            _notice = $"Impossible de lire cet événement : {ex.Message}";
            _noticeSeverity = InfoBarSeverity.Error;
            Render();
        }
    }

    private async Task RenameSavedAsync(CollectionInfo collection)
    {
        var nameBox = new TextBox
        {
            Text = collection.Name,
            Header = "Nom de la collection",
        };
        var dialog = new ContentDialog
        {
            Title = "Renommer l'événement",
            Content = nameBox,
            PrimaryButtonText = "Renommer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var name = nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            await EventSuggestionService.Current.RenameSavedEventAsync(collection, name);
            _notice = $"L'événement est renommé « {name} ».";
            _noticeSeverity = InfoBarSeverity.Success;
            await BuildAsync();
        }
        catch (Exception ex)
        {
            _notice = $"Impossible de renommer l'événement : {ex.Message}";
            _noticeSeverity = InfoBarSeverity.Error;
            await BuildAsync();
        }
    }

    private async Task DeleteSavedAsync(CollectionInfo collection)
    {
        var dialog = new ContentDialog
        {
            Title = "Supprimer cet événement ?",
            Content = new TextBlock
            {
                Text = "La collection locale sera supprimée. Les souvenirs et les fichiers " +
                       "sources ne sont pas modifiés.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Supprimer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await EventSuggestionService.Current.DeleteSavedEventAsync(collection);
            _notice = "L'événement enregistré est supprimé. La proposition peut réapparaître.";
            _noticeSeverity = InfoBarSeverity.Success;
            await BuildAsync();
        }
        catch (Exception ex)
        {
            _notice = $"Impossible de supprimer l'événement : {ex.Message}";
            _noticeSeverity = InfoBarSeverity.Error;
            await BuildAsync();
        }
    }

    private static Border CreateCover(string? path, double width, double height, string placeholder)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(4),
            Background = Widgets.Brush("MemoPhotoPlaceholderBrush"),
        };

        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                border.Child = new Image
                {
                    Source = new BitmapImage(new Uri(path)),
                    Stretch = Stretch.UniformToFill,
                };
                return border;
            }
            catch
            {
                // Le chemin peut être absent ou invalide : un texte de remplacement suffit.
            }
        }

        border.Child = new TextBlock
        {
            Text = placeholder,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Style = Widgets.Style("MemoCaptionTextStyle"),
        };
        return border;
    }
}
