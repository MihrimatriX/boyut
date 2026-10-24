using Boyut.App.Services;
using Boyut.Core.Layout;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Boyut.App.Controls;

/// <param name="Node">Index node, or -1 for the "… more items" tile.</param>
public sealed record TreemapTile(int Node, string Name, string SizeText, double Value, Color Color, bool IsDirectory)
{
    /// <summary>Second level: a folder's own children, drawn inside its tile when there is room.</summary>
    public IReadOnlyList<TreemapTile>? Children { get; init; }

    /// <summary>Extra tooltip line, e.g. the share of the folder.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// Squarified treemap of one folder, two levels deep (WizTree / SpaceSniffer style): big folders show a header strip
/// with their own children inside. Tiles are plain Borders on a Canvas: cheap for the few hundred tiles we draw
/// and easy to theme.
/// </summary>
public sealed partial class TreemapView : Grid
{
    private const double Gap = 2;
    private const double HeaderHeight = 20;
    private static readonly SolidColorBrush TileText = new(Color.FromArgb(255, 0x14, 0x20, 0x2B));
    private readonly Canvas canvas = new();
    private readonly Dictionary<int, Border> elements = [];
    private readonly DispatcherQueueTimer resizeTimer;
    private Border? highlighted;

    public TreemapView()
    {
        Children.Add(canvas);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // Dragging the pane splitter resizes us on every mouse move: lay out once the size settles.
        resizeTimer = DispatcherQueue.CreateTimer();
        resizeTimer.Interval = TimeSpan.FromMilliseconds(40);
        resizeTimer.IsRepeating = false;
        resizeTimer.Tick += (_, _) => Render();
        SizeChanged += (_, _) => { resizeTimer.Stop(); resizeTimer.Start(); };
        ActualThemeChanged += (_, _) => Highlight();
        Unloaded += (_, _) => resizeTimer.Stop();
    }

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(object), typeof(TreemapView), new PropertyMetadata(null, (d, _) => ((TreemapView)d).Render()));

    public IReadOnlyList<TreemapTile>? Items
    {
        get => GetValue(ItemsProperty) as IReadOnlyList<TreemapTile>;
        set => SetValue(ItemsProperty, value);
    }

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(int), typeof(TreemapView), new PropertyMetadata(-1, (d, _) => ((TreemapView)d).Highlight()));

    /// <summary>Outlined when it is one of the tiles (selecting a file in the tree marks it on the map).</summary>
    public int SelectedNode
    {
        get => (int)GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public event EventHandler<TreemapTile>? TileInvoked;

    private void Render()
    {
        canvas.Children.Clear();
        elements.Clear();
        highlighted = null;
        var items = Items;
        double width = ActualWidth, height = ActualHeight;
        if (items is null || items.Count == 0 || width < 8 || height < 8) return;
        Place(items, 0, 0, width, height, nested: false);
        Highlight();
    }

    private void Place(IReadOnlyList<TreemapTile> items, double x, double y, double width, double height, bool nested)
    {
        var values = new double[items.Count];
        for (int i = 0; i < values.Length; i++) values[i] = items[i].Value;
        var rects = Squarifier.Layout(values, x, y, width, height);
        for (int i = 0; i < items.Count; i++)
        {
            var r = rects[i];
            if (r.Width < 3 || r.Height < 3) continue;
            var item = items[i];
            if (!nested && item.Children is { Count: > 0 } kids && r.Width >= 96 && r.Height >= 64)
            {
                AddTile(item, r, frame: true);
                Place(kids, r.X + 4, r.Y + HeaderHeight, r.Width - 8 - Gap, r.Height - HeaderHeight - 4 - Gap, nested: true);
            }
            else
            {
                AddTile(item, r, frame: false);
            }
        }
    }

    /// <param name="frame">A folder whose children are drawn on top of it: only its header strip shows.</param>
    private void AddTile(TreemapTile item, TreemapRect r, bool frame)
    {
        var tile = new Border
        {
            Width = Math.Max(0, r.Width - Gap),
            Height = Math.Max(0, r.Height - Gap),
            Background = frame ? new SolidColorBrush(Ui.Shade(item.Color, 0.86)) : Cushion(item.Color),
            CornerRadius = new CornerRadius(Math.Min(4, Math.Min(r.Width, r.Height) / 4)),
            Padding = frame ? new Thickness(6, 2, 6, 0) : new Thickness(6, 4, 6, 4),
        };

        if (frame)
        {
            tile.Child = Label($"{item.Name}  ·  {item.SizeText}", bold: true, VerticalAlignment.Top);
        }
        else if (r.Width >= 56 && r.Height >= 26)
        {
            var stack = new StackPanel();
            stack.Children.Add(Label(item.Name, bold: true, VerticalAlignment.Top));
            if (r.Height >= 42) stack.Children.Add(Label(item.SizeText, bold: false, VerticalAlignment.Top));
            tile.Child = stack;
        }

        string label = item.Detail.Length > 0 ? $"{item.Name}\n{item.SizeText} · {item.Detail}" : $"{item.Name}\n{item.SizeText}";
        ToolTipService.SetToolTip(tile, label);
        AutomationProperties.SetName(tile, label.Replace('\n', ' '));

        tile.Tapped += (_, e) => { e.Handled = true; TileInvoked?.Invoke(this, item); };
        tile.PointerEntered += (_, _) => tile.Opacity = 0.85;
        tile.PointerExited += (_, _) => tile.Opacity = 1;

        Canvas.SetLeft(tile, r.X + Gap / 2);
        Canvas.SetTop(tile, r.Y + Gap / 2);
        canvas.Children.Add(tile);
        if (item.Node >= 0) elements[item.Node] = tile;
    }

    private static TextBlock Label(string text, bool bold, VerticalAlignment alignment) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = TileText,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        MaxLines = 1,
        VerticalAlignment = alignment,
    };

    private void Highlight()
    {
        if (highlighted is not null)
        {
            highlighted.BorderThickness = new Thickness(0);
            highlighted.BorderBrush = null;
            highlighted = null;
        }
        if (!elements.TryGetValue(SelectedNode, out var tile)) return;
        tile.BorderBrush = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        tile.BorderThickness = new Thickness(2);
        highlighted = tile;
    }

    /// <summary>Top-lit gradient, so tiles read as raised rather than flat paint.</summary>
    private static LinearGradientBrush Cushion(Color c) => new()
    {
        StartPoint = new(0, 0),
        EndPoint = new(0, 1),
        GradientStops =
        {
            new GradientStop { Color = c, Offset = 0 },
            new GradientStop { Color = Ui.Shade(c, 0.84), Offset = 1 },
        },
    };
}
