using Memo.App.Controls;
using Memo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        button.Click += (_, _) => App.MainWindow?.ShowStory(list, index);
        return button;
    }

    public static ScrollViewer TileRow(IReadOnlyList<MemoryCard> cards, bool showDate = true)
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

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = panel,
            Margin = new Thickness(0, 0, 0, 24)
        };
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

