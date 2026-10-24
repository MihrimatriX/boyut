using Boyut.App.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Boyut.App.Controls;

/// <summary>One column of a list. Width is in pixels; a column created without a width is the one that takes the rest.</summary>
public sealed class GridColumn(string key, string header, double width = 0)
{
    public string Key { get; } = key;
    public string Header { get; } = header;
    public double DefaultWidth { get; } = width;
    public double Width { get; set; } = width;
    public bool Star => DefaultWidth <= 0;
    public double MinWidth { get; init; } = 48;
    public bool AlignRight { get; init; }
    public bool Sortable { get; init; } = true;
    /// <summary>First click sorts largest / newest first.</summary>
    public bool DescendingFirst { get; init; }
    /// <summary>When the list is too narrow, lower priorities are squeezed out first. The star column always stays.</summary>
    public int Priority { get; init; }

    /// <summary>Turned off by the user (header right-click menu).</summary>
    public bool IsHidden { get; set; }
    /// <summary>Temporarily dropped because the list is too narrow.</summary>
    public bool IsSqueezed { get; set; }
    public bool IsShown => !IsHidden && !IsSqueezed;
}

/// <summary>
/// Column layout shared by a list's header and every row template, so widths, hidden columns and sort order
/// live in one place. Rows opt in with <c>controls:Columns.Layout</c>; each cell's <c>Grid.Column</c> is its
/// index in <see cref="Items"/>, and is remapped here as columns come and go.
/// </summary>
public sealed class GridColumns
{
    public const double Gap = 12;
    private const double MaxWidth = 900;

    private readonly string id;
    private readonly string defaultSort;
    private readonly bool defaultDescending;
    private readonly List<WeakReference<Grid>> rows = [];
    private double available = double.PositiveInfinity;

    public GridColumns(string id, string defaultSort, bool defaultDescending, params GridColumn[] columns)
    {
        this.id = id;
        this.defaultSort = SortKey = defaultSort;
        this.defaultDescending = SortDescending = defaultDescending;
        Items = columns;
        StarIndex = Array.FindIndex(columns, c => c.Star);

        if (!SettingsStore.Current.Grids.TryGetValue(id, out var saved)) return;
        foreach (var c in Items)
        {
            if (c.Star) continue;
            if (saved.Widths.TryGetValue(c.Key, out double w)) c.Width = Math.Clamp(w, c.MinWidth, MaxWidth);
            c.IsHidden = saved.Hidden.Contains(c.Key);
        }
        if (Items.Any(c => c.Sortable && c.Key == saved.SortKey))
        {
            SortKey = saved.SortKey!;
            SortDescending = saved.SortDescending;
        }
    }

    public IReadOnlyList<GridColumn> Items { get; }
    public int StarIndex { get; }
    public string SortKey { get; private set; }
    public bool SortDescending { get; private set; }

    /// <summary>Widths or visible columns changed (rows are already updated when this fires).</summary>
    public event EventHandler? LayoutChanged;
    public event EventHandler? SortChanged;

    public void SortBy(string key)
    {
        var column = Items.FirstOrDefault(c => c.Key == key);
        if (column is not { Sortable: true }) return;
        if (SortKey == key) SortDescending = !SortDescending;
        else { SortKey = key; SortDescending = column.DescendingFirst; }
        Save();
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Live while dragging; call <see cref="Save"/> when the drag ends.</summary>
    public void SetWidth(GridColumn column, double width)
    {
        if (column.Star) return;
        width = Math.Clamp(width, column.MinWidth, MaxWidth);
        if (width == column.Width) return;
        column.Width = width;
        Relayout();
    }

    public void SetHidden(GridColumn column, bool hidden)
    {
        if (column.Star || column.IsHidden == hidden) return;
        column.IsHidden = hidden;
        Relayout();
        Save();
    }

    public void Reset()
    {
        foreach (var c in Items) { c.Width = c.DefaultWidth; c.IsHidden = false; }
        bool sortChanged = SortKey != defaultSort || SortDescending != defaultDescending;
        SortKey = defaultSort;
        SortDescending = defaultDescending;
        Relayout();
        Save();
        if (sortChanged) SortChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The header reports the width its cells get; columns are squeezed out to keep the star column usable.</summary>
    public void Fit(double width)
    {
        if (width <= 0) return;
        available = width;
        var before = Items.Select(c => c.IsSqueezed).ToArray();
        Squeeze(width);
        if (!Items.Select(c => c.IsSqueezed).SequenceEqual(before)) Relayout();
    }

    /// <summary>Strict priority: drop the lowest-priority fixed columns until the star column gets its MinWidth.</summary>
    private void Squeeze(double width)
    {
        foreach (var c in Items) c.IsSqueezed = false;
        if (double.IsInfinity(width) || StarIndex < 0) return;

        double Needed()
        {
            var shown = Items.Where(c => c.IsShown).ToList();
            return shown.Sum(c => c.Star ? c.MinWidth : c.Width) + Gap * (shown.Count - 1);
        }

        foreach (var c in Items.Where(c => !c.Star && !c.IsHidden).OrderBy(c => c.Priority))
        {
            if (Needed() <= width) break;
            c.IsSqueezed = true;
        }
    }

    private void Relayout()
    {
        Squeeze(available);
        rows.RemoveAll(r => !r.TryGetTarget(out _));
        foreach (var r in rows)
            if (r.TryGetTarget(out var grid)) Apply(grid);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        SettingsStore.Current.Grids[id] = new GridLayout
        {
            Widths = Items.Where(c => !c.Star && c.Width != c.DefaultWidth).ToDictionary(c => c.Key, c => c.Width),
            Hidden = Items.Where(c => c.IsHidden).Select(c => c.Key).ToList(),
            SortKey = SortKey,
            SortDescending = SortDescending,
        };
        SettingsStore.Save();
    }

    internal void Register(Grid grid)
    {
        rows.Add(new WeakReference<Grid>(grid));
        Apply(grid);
    }

    /// <summary>
    /// Rebuilds the grid's columns as [cell, gap, cell, gap, …, parking]. Gaps are real columns (not ColumnSpacing)
    /// so a hidden column leaves no space behind; its cell is parked in a zero-width column at the end.
    /// Returns the physical column of each item, or -1 when it is not shown.
    /// </summary>
    internal int[] Apply(Grid grid)
    {
        grid.ColumnSpacing = 0;
        var defs = grid.ColumnDefinitions;
        defs.Clear();
        var slot = new int[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            var c = Items[i];
            if (!c.IsShown) { slot[i] = -1; continue; }
            if (defs.Count > 0) defs.Add(new ColumnDefinition { Width = new GridLength(Gap) });
            slot[i] = defs.Count;
            defs.Add(c.Star
                ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = c.MinWidth }
                : new ColumnDefinition { Width = new GridLength(c.Width) });
        }
        int parking = defs.Count;
        defs.Add(new ColumnDefinition { Width = new GridLength(0) });

        foreach (var child in grid.Children)
        {
            if (child is ColumnGripper || child is not FrameworkElement cell) continue;
            int logical = Columns.GetLogical(cell);
            if (logical < 0)
            {
                logical = Grid.GetColumn(cell);
                Columns.SetLogical(cell, logical);
            }
            bool shown = logical < slot.Length && slot[logical] >= 0;
            Grid.SetColumn(cell, shown ? slot[logical] : parking);
            // Zero height too: wrapping text squeezed to zero width would otherwise grow the row tall.
            cell.MaxHeight = shown ? double.PositiveInfinity : 0;
            cell.IsHitTestVisible = shown;
        }
        return slot;
    }
}

/// <summary>Attached properties for row templates: <c>controls:Columns.Layout="{x:Bind vm:SomeViewModel.Current.Columns}"</c>.</summary>
public static class Columns
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.RegisterAttached(
        "Layout", typeof(GridColumns), typeof(Columns), new PropertyMetadata(null, OnLayoutChanged));

    public static GridColumns? GetLayout(Grid grid) => grid.GetValue(LayoutProperty) as GridColumns;
    public static void SetLayout(Grid grid, GridColumns? value) => grid.SetValue(LayoutProperty, value);

    /// <summary>The cell's column as written in XAML, remembered before <see cref="GridColumns.Apply"/> moves it.</summary>
    private static readonly DependencyProperty LogicalProperty = DependencyProperty.RegisterAttached(
        "Logical", typeof(int), typeof(Columns), new PropertyMetadata(-1));

    internal static int GetLogical(UIElement element) => (int)element.GetValue(LogicalProperty);
    internal static void SetLogical(UIElement element, int value) => element.SetValue(LogicalProperty, value);

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || e.NewValue is not GridColumns columns) return;
        columns.Register(grid);
        // Cells may not exist yet when the property is set during template load.
        grid.Loaded += (_, _) => columns.Apply(grid);
    }
}

/// <summary>Header row for a <see cref="GridColumns"/> list: click to sort, drag a column edge to resize (double-click resets), right-click to pick columns.</summary>
public sealed partial class GridHeader : Grid
{
    private static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
    private readonly List<(GridColumn Column, TextBlock Label, FontIcon Arrow, ColumnGripper? Grip)> parts = [];
    private double dragStartX, dragStartWidth;

    public GridHeader()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // right-click anywhere on the header
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => FillMenu(menu);
        ContextFlyout = menu;
    }

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(GridColumns), typeof(GridHeader),
        new PropertyMetadata(null, (d, e) => ((GridHeader)d).Attach(e.OldValue as GridColumns, e.NewValue as GridColumns)));

    public GridColumns? Columns
    {
        get => GetValue(ColumnsProperty) as GridColumns;
        set => SetValue(ColumnsProperty, value);
    }

    // Fit against the width offered by the parent: ActualWidth can't shrink below the fixed columns' sum,
    // because a Grid is arranged at least as wide as its desired size (and clipped).
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        Columns?.Fit(availableSize.Width - Padding.Left - Padding.Right);
        return base.MeasureOverride(availableSize);
    }

    private void Attach(GridColumns? old, GridColumns? columns)
    {
        if (old is not null) { old.LayoutChanged -= OnLayoutChanged; old.SortChanged -= OnSortChanged; }
        Children.Clear();
        parts.Clear();
        if (columns is null) return;
        columns.LayoutChanged += OnLayoutChanged;
        columns.SortChanged += OnSortChanged;

        for (int i = 0; i < columns.Items.Count; i++)
        {
            var c = columns.Items[i];
            var label = new TextBlock { Text = c.Header.ToUpper(Tr), TextTrimming = TextTrimming.CharacterEllipsis };
            var arrow = new FontIcon { FontSize = 10, Visibility = Visibility.Collapsed };
            var cell = new Button
            {
                Style = (Style)Application.Current.Resources["GridHeaderCell"],
                HorizontalAlignment = c.AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { label, arrow } },
                IsHitTestVisible = c.Sortable,
            };
            ToolTipService.SetToolTip(cell, $"{c.Header} sütununa göre sırala · sağ tık: sütunlar");
            Grid.SetColumn(cell, i);
            cell.Click += (_, _) => Columns?.SortBy(c.Key);
            Children.Add(cell);

            ColumnGripper? grip = null;
            if (!c.Star)
            {
                // The edge facing the star column follows the mouse; the star column absorbs the change.
                bool rightOfStar = i > columns.StarIndex;
                grip = new ColumnGripper();
                ToolTipService.SetToolTip(grip, "Sürükle: genişlik · çift tık: varsayılan");
                grip.PointerPressed += (_, e) =>
                {
                    if (!grip.CapturePointer(e.Pointer)) return;
                    dragStartX = e.GetCurrentPoint(this).Position.X;
                    dragStartWidth = c.Width;
                    e.Handled = true;
                };
                grip.PointerMoved += (_, e) =>
                {
                    if (grip.PointerCaptures is not { Count: > 0 }) return;
                    double dx = e.GetCurrentPoint(this).Position.X - dragStartX;
                    Columns?.SetWidth(c, dragStartWidth + (rightOfStar ? -dx : dx));
                };
                grip.PointerReleased += (_, e) => grip.ReleasePointerCapture(e.Pointer);
                grip.PointerCaptureLost += (_, _) => Columns?.Save();
                grip.DoubleTapped += (_, _) => { Columns?.SetWidth(c, c.DefaultWidth); Columns?.Save(); };
                Children.Add(grip);
            }
            parts.Add((c, label, arrow, grip));
        }
        OnLayoutChanged(this, EventArgs.Empty);
        OnSortChanged(this, EventArgs.Empty);
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        if (Columns is not { } columns) return;
        var slot = columns.Apply(this);
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Grip is not { } grip) continue;
            grip.Visibility = slot[i] >= 0 ? Visibility.Visible : Visibility.Collapsed;
            // The gap column beside the cell, on the star column's side.
            if (slot[i] >= 0) Grid.SetColumn(grip, i > columns.StarIndex ? slot[i] - 1 : slot[i] + 1);
        }
    }

    private void OnSortChanged(object? sender, EventArgs e)
    {
        if (Columns is not { } columns) return;
        foreach (var (c, label, arrow, _) in parts)
        {
            bool sorted = c.Key == columns.SortKey;
            arrow.Visibility = sorted ? Visibility.Visible : Visibility.Collapsed;
            arrow.Glyph = columns.SortDescending ? "" : "";
            label.FontWeight = sorted ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void FillMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        if (Columns is not { } columns) return;
        foreach (var c in columns.Items.Where(c => !c.Star))
        {
            var item = new ToggleMenuFlyoutItem { Text = c.Header, IsChecked = !c.IsHidden };
            item.Click += (_, _) => columns.SetHidden(c, !item.IsChecked);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var reset = new MenuFlyoutItem { Text = "Sütunları varsayılana döndür" };
        reset.Click += (_, _) => columns.Reset();
        menu.Items.Add(reset);
    }
}

/// <summary>Drag handle filling the gap between two header cells.</summary>
internal sealed partial class ColumnGripper : Grid
{
    public ColumnGripper()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(new Border { Style = (Style)Application.Current.Resources["GridHeaderDivider"] });
    }
}
