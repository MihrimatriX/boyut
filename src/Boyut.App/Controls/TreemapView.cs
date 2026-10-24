using Boyut.Core.Layout;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Boyut.App.Controls;

public sealed record TreemapTile(int Node, string Name, string SizeText, double Value, Color Color, bool IsSelected, bool IsDirectory);

/// <summary>
/// Squarified treemap of one folder's children. Tiles are plain Borders on a Canvas:
/// cheap enough for the ~60 tiles we show and easy to theme.
/// </summary>
public sealed partial class TreemapView : Grid
{
    private static readonly SolidColorBrush TileText = new(Color.FromArgb(255, 0x14, 0x20, 0x2B));
    private readonly Canvas canvas = new();

    public TreemapView()
    {
        Children.Add(canvas);
        SizeChanged += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(object), typeof(TreemapView), new PropertyMetadata(null, (d, _) => ((TreemapView)d).Render()));

    public IReadOnlyList<TreemapTile>? Items
    {
        get => GetValue(ItemsProperty) as IReadOnlyList<TreemapTile>;
        set => SetValue(ItemsProperty, value);
    }

    public event EventHandler<TreemapTile>? TileInvoked;

    private void Render()
    {
        canvas.Children.Clear();
        var items = Items;
        double width = ActualWidth, height = ActualHeight;
        if (items is null || items.Count == 0 || width < 4 || height < 4) return;

        var values = new double[items.Count];
        for (int i = 0; i < values.Length; i++) values[i] = items[i].Value;
        var rects = Squarifier.Layout(values, 0, 0, width, height);
        var selectedStroke = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);

        for (int i = 0; i < items.Count; i++)
        {
            var r = rects[i];
            if (r.Width < 3 || r.Height < 3) continue;
            var item = items[i];

            var tile = new Border
            {
                Width = Math.Max(0, r.Width - 2),
                Height = Math.Max(0, r.Height - 2),
                Background = Cushion(item.Color),
                CornerRadius = new CornerRadius(Math.Min(6, Math.Min(r.Width, r.Height) / 4)),
                BorderBrush = item.IsSelected ? selectedStroke : null,
                BorderThickness = new Thickness(item.IsSelected ? 2 : 0),
                Padding = new Thickness(8, 6, 8, 6),
            };

            if (r.Width >= 64 && r.Height >= 36)
            {
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = TileText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                });
                stack.Children.Add(new TextBlock { Text = item.SizeText, FontSize = 12, Foreground = TileText });
                tile.Child = stack;
            }

            string label = $"{item.Name}, {item.SizeText}";
            ToolTipService.SetToolTip(tile, label);
            AutomationProperties.SetName(tile, label);

            var captured = item;
            tile.Tapped += (_, e) => { e.Handled = true; TileInvoked?.Invoke(this, captured); };
            tile.PointerEntered += (_, _) => tile.Opacity = 0.82;
            tile.PointerExited += (_, _) => tile.Opacity = 1;

            Canvas.SetLeft(tile, r.X + 1);
            Canvas.SetTop(tile, r.Y + 1);
            canvas.Children.Add(tile);
        }
    }

    /// <summary>Top-lit gradient, so tiles read as raised glass rather than flat paint.</summary>
    private static LinearGradientBrush Cushion(Color c) => new()
    {
        StartPoint = new(0, 0),
        EndPoint = new(0, 1),
        GradientStops =
        {
            new GradientStop { Color = c, Offset = 0 },
            new GradientStop { Color = Color.FromArgb(255, (byte)(c.R * 0.82), (byte)(c.G * 0.82), (byte)(c.B * 0.82)), Offset = 1 },
        },
    };
}
