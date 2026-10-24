using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Boyut.App.Controls;

/// <summary>
/// Vertical bar between two panes. Reports how far it was dragged; the page decides which pane grows.
/// Double-click asks for the default size. The grip shows on hover, like Windows 11's own splitters.
/// </summary>
public sealed partial class PaneSplitter : Grid
{
    private readonly Border grip;
    private double startX;
    private bool dragging;

    public PaneSplitter()
    {
        Width = 12;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable everywhere
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        grip = new Border { Style = (Style)Application.Current.Resources["PaneSplitterGrip"], Opacity = 0 };
        Children.Add(grip);
        ToolTipService.SetToolTip(this, "Sürükle: bölme genişliği · çift tık: varsayılan");

        PointerEntered += (_, _) => grip.Opacity = 1;
        PointerExited += (_, _) => { if (!dragging) grip.Opacity = 0; };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => ReleasePointerCapture(e.Pointer);
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, e) => { e.Handled = true; ResetRequested?.Invoke(this, EventArgs.Empty); };
    }

    public event EventHandler? DragStarted;
    /// <summary>Horizontal distance from where the drag started, in pixels (positive = to the right).</summary>
    public event EventHandler<double>? Dragging;
    public event EventHandler? DragCompleted;
    public event EventHandler? ResetRequested;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer)) return;
        // Window coordinates: the splitter itself moves while the panes resize.
        startX = e.GetCurrentPoint(null).Position.X;
        dragging = true;
        grip.Opacity = 1;
        e.Handled = true;
        DragStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        Dragging?.Invoke(this, e.GetCurrentPoint(null).Position.X - startX);
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        grip.Opacity = 0;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
