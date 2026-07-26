using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EchoesUnseen.Views;

/// <summary>
/// Full-screen modal overlay that lets the user drag a selection rectangle.
///
/// WHY THIS IS A SEPARATE TOP-LEVEL Window (not a child of the panel):
///   In the previous Electron build, the selection overlay was rendered
///   inside the panel's React tree. The panel's z-index and click-dismiss
///   handler intercepted mouse events before the overlay saw them, so the
///   user couldn't actually draw a selection. We worked around it with
///   createPortal to document.body and capture-phase event listeners.
///
///   In WPF we do it RIGHT from the start: this is a top-level Window
///   managed independently by the OS. Two separate windows can never have
///   z-index conflicts — the concept doesn't apply across windows.
///
/// USAGE:
///   var win = new SelectionOverlayWindow();
///   win.RegionSelected += (_, rect) => { /* rect is in screen DIPs */ };
///   win.Cancelled += (_, _) => { /* user pressed Escape */ };
///   win.Show();
///
/// COORDINATE SYSTEM:
///   The returned Rect is in WPF DIPs (device-independent pixels) relative to
///   the primary screen. The caller (ScreenCaptureService) converts to physical
///   pixels via DPI scaling before passing to Win32 BitBlt.
/// </summary>
public partial class SelectionOverlayWindow : Window
{
    /// <summary>Fired when the user releases the mouse on a non-trivial selection.</summary>
    public event EventHandler<Rect>? RegionSelected;

    /// <summary>Fired when the user presses Escape (no rectangle emitted).</summary>
    public event EventHandler? Cancelled;

    private Point? _dragStart;
    private bool _finished;

    public SelectionOverlayWindow()
    {
        InitializeComponent();
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OverlayCanvas);
        Instructions.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _dragStart.Value.X);
        Canvas.SetTop(SelectionRect, _dragStart.Value.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        OverlayCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart == null) return;
        var cur = e.GetPosition(OverlayCanvas);
        var left = Math.Min(_dragStart.Value.X, cur.X);
        var top  = Math.Min(_dragStart.Value.Y, cur.Y);
        var w    = Math.Abs(cur.X - _dragStart.Value.X);
        var h    = Math.Abs(cur.Y - _dragStart.Value.Y);
        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart == null) return;
        OverlayCanvas.ReleaseMouseCapture();

        var endPos = e.GetPosition(OverlayCanvas);
        var rect = new Rect(
            Math.Min(_dragStart.Value.X, endPos.X),
            Math.Min(_dragStart.Value.Y, endPos.Y),
            Math.Abs(endPos.X - _dragStart.Value.X),
            Math.Abs(endPos.Y - _dragStart.Value.Y));

        _dragStart = null;

        // Ignore trivial "clicks" — user needs to actually drag a meaningful area.
        if (rect.Width < 8 || rect.Height < 8)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            Instructions.Visibility = Visibility.Visible;
            return;
        }

        _finished = true;
        RegionSelected?.Invoke(this, rect);
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _finished = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
            Close();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_finished)
        {
            // Window closed without a selection (e.g. user Alt+F4'd)
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }
}
