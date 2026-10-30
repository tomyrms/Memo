using Memo.App.Controls;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Memo.App.Ui;

internal static class Widgets
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        Style = Style("MemoTitleTextStyle")
    };

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        Style = Style("MemoSubtitleTextStyle"),
        VerticalAlignment = VerticalAlignment.Center
    };

    public static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Style = Style("MemoSecondaryTextStyle")
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Style("MemoCaptionTextStyle")
    };

    public static Border Card(UIElement content, Thickness? padding = null) => new()
    {
        Style = Style("MemoCardBorderStyle"),
        Padding = padding ?? new Thickness(16),
        Child = content
    };

    public static Grid SectionHeader(string title, string? actionText = null, Action? action = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Section(title));

        if (actionText is not null && action is not null)
        {
            var button = new Button
            {
                Content = actionText,
                Style = Style("MemoQuietButtonStyle"),
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Click += (_, _) => action();
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        return grid;
    }

    public static Button TileButton(MemoryCard card, IReadOnlyList<MemoryCard> list, int index, bool showDate = false)
    {
        var tile = new MemoryTile { Card = card, ShowDate = showDate };
        var button = new Button
        {
            Content = tile,
            Style = Style("MemoTileButtonStyle"),
            Width = 180,
            Height = 240,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(button, $"{DateLabel(card)} — {(card.Kind == MediaKind.Video ? "vidéo" : "photo")}");
        AutomationProperties.SetHelpText(button, "Entrée pour ouvrir la Story sur ce souvenir.");
        button.Click += (_, _) => App.MainWindow?.ShowStory(list, index);
        return button;
    }

    /// <summary>
    /// Rangée horizontale défilante. Les flèches de défilement sont de vrais boutons,
    /// accessibles au clavier et au lecteur d'écran ; le défilement natif (molette,
    /// pavé tactile, barre de défilement) reste disponible. Les enfants directs qui
    /// sont des boutons reçoivent en plus la navigation par flèches gauche/droite.
    /// </summary>
    public static Grid ScrollRow(FrameworkElement content, string? context = null, double bottomMargin = 24)
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
            BringIntoViewOnFocusChange = true,
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(context))
        {
            AutomationProperties.SetName(scroll, context);
        }

        var left = ScrollButton(forward: false, context, scroll);
        var right = ScrollButton(forward: true, context, scroll);

        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, bottomMargin)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(scroll, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(scroll);
        grid.Children.Add(right);

        if (content is Panel host)
        {
            EnableRowKeyboardNavigation(host);
        }

        void UpdateButtons()
        {
            var scrollable = scroll.ScrollableWidth > 0.5;
            left.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;
            right.Visibility = scrollable ? Visibility.Visible : Visibility.Collapsed;

            // Ne jamais désactiver le bouton qui porte le focus : on le laisse actif
            // jusqu'à ce que le focus le quitte, pour éviter une perte de focus.
            var canLeft = scrollable && scroll.HorizontalOffset > 0.5;
            var canRight = scrollable && scroll.HorizontalOffset < scroll.ScrollableWidth - 0.5;
            left.IsEnabled = canLeft || left.FocusState != FocusState.Unfocused;
            right.IsEnabled = canRight || right.FocusState != FocusState.Unfocused;
        }

        // La largeur défilable n'est connue qu'après la mesure : on repasse une fois
        // la mise en page terminée plutôt que de dépendre de l'ordre des SizeChanged.
        void ScheduleUpdate() => scroll.DispatcherQueue.TryEnqueue(UpdateButtons);

        scroll.ViewChanged += (_, _) => UpdateButtons();
        scroll.SizeChanged += (_, _) => ScheduleUpdate();
        content.SizeChanged += (_, _) => ScheduleUpdate();
        scroll.Loaded += (_, _) => ScheduleUpdate();
        left.LostFocus += (_, _) => UpdateButtons();
        right.LostFocus += (_, _) => UpdateButtons();
        UpdateButtons();

        return grid;
    }

    public static Grid TileRow(IReadOnlyList<MemoryCard> cards, bool showDate = true, string? context = null)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };
        for (var i = 0; i < cards.Count; i++)
        {
            panel.Children.Add(TileButton(cards[i], cards, i, showDate));
        }

        return ScrollRow(panel, context);
    }

    private static Button ScrollButton(bool forward, string? context, ScrollViewer scroll)
    {
        var label = forward ? "Faire défiler vers la droite" : "Faire défiler vers la gauche";
        if (!string.IsNullOrWhiteSpace(context))
        {
            label += " — " + context;
        }

        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = forward ? "\uE76C" : "\uE76B",
                FontSize = 16
            },
            Style = Style("MemoQuietButtonStyle"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = forward ? new Thickness(8, 0, 0, 0) : new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, forward
            ? "Affiche la suite de la rangée."
            : "Affiche le début de la rangée.");
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) =>
        {
            // Un cran couvre presque une page de tuiles sans sauter la première visible.
            var step = Math.Max(240, scroll.ViewportWidth - 200);
            var target = forward ? scroll.HorizontalOffset + step : scroll.HorizontalOffset - step;
            scroll.ChangeView(target, null, null, false);
        };
        return button;
    }

    private static void EnableRowKeyboardNavigation(Panel host)
    {
        var buttons = host.Children.OfType<Button>().ToList();
        if (buttons.Count == 0)
        {
            return;
        }

        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            AutomationProperties.SetPositionInSet(button, i + 1);
            AutomationProperties.SetSizeOfSet(button, buttons.Count);
            AutomationProperties.SetHelpText(button, "Entrée pour ouvrir. Flèches gauche et droite pour parcourir la rangée.");

            var index = i;
            button.KeyDown += (_, args) => MoveFocus(buttons, index, args);
        }
    }

    private static void MoveFocus(IReadOnlyList<Button> buttons, int index, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End))
        {
            return;
        }

        var target = args.Key switch
        {
            VirtualKey.Left => index - 1,
            VirtualKey.Right => index + 1,
            VirtualKey.Home => 0,
            _ => buttons.Count - 1
        };

        args.Handled = true;
        if (target >= 0 && target < buttons.Count)
        {
            buttons[target].Focus(FocusState.Keyboard);
        }
    }

    public static Border EmptyState(string title, string message, string? actionText = null, Action? action = null)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = title, Style = Style("MemoSubtitleTextStyle") });
        panel.Children.Add(Secondary(message));
        if (actionText is not null && action is not null)
        {
            var button = new Button
            {
                Content = actionText,
                Style = Style("MemoPrimaryButtonStyle"),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }

        return Card(panel, new Thickness(24));
    }

    public static string DateLabel(MemoryCard card)
        => card.LocalDate is { } d ? d.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy", Services.AppState.French) : "Date inconnue";

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

