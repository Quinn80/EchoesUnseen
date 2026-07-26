using System.Windows;
using System.Windows.Controls;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Screen Reader Panel — reads any text on screen aloud.
///
/// WORKFLOW:
///   1. User clicks "Select Area"
///   2. This panel's host window is hidden, a new SelectionOverlayWindow opens
///   3. User drags a rectangle on the full-screen overlay
///   4. Overlay closes, returns rectangle coordinates
///   5. ScreenCaptureService grabs that region as PNG bytes
///   6. OcrService runs Windows.Media.Ocr on the PNG
///   7. Result is displayed and spoken via TtsService
///
/// The "Read Again" button re-speaks the last captured text — useful when a
/// user wants to hear it at a different speed or just missed it the first time.
///
/// BUG FIXES PRESERVED FROM PREVIOUS BUILD:
///   - No z-index trap: SelectionOverlayWindow is a SEPARATE top-level Window,
///     never a child of this panel. Architecturally impossible to conflict
///     with panel click-dismiss or z-index stacking.
///   - No OCR fragmentation: Windows.Media.Ocr handles multi-line text
///     correctly without PSM configuration. Previous Tesseract bug cannot recur.
/// </summary>
public partial class ScreenReaderPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;
    private string _lastText = "";

    public ScreenReaderPanel()
    {
        InitializeComponent();
        if (!OcrService.IsAvailable())
            StatusText.Text = "⚠ Windows OCR engine not available. Install a Windows language pack with OCR support.";
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    private async void SelectArea_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this);
        var selectionWindow = new SelectionOverlayWindow();

        Rect? selection = null;
        bool cancelled = false;

        selectionWindow.RegionSelected += (_, rect) => selection = rect;
        selectionWindow.Cancelled += (_, _) => cancelled = true;

        // Hide main overlay while selecting so it doesn't obscure the target.
        // Use Hide() rather than WindowState=Minimized so it re-shows instantly.
        if (mainWindow != null) mainWindow.Visibility = Visibility.Hidden;

        var tcs = new TaskCompletionSource();
        selectionWindow.Closed += (_, _) => tcs.TrySetResult();
        selectionWindow.Show();
        selectionWindow.Activate();
        selectionWindow.Focus();
        await tcs.Task;

        if (mainWindow != null) mainWindow.Visibility = Visibility.Visible;

        if (cancelled || selection is null)
        {
            StatusText.Text = "Selection cancelled.";
            return;
        }

        await CaptureAndReadAsync(selection.Value);
    }

    private async Task CaptureAndReadAsync(Rect dipRect)
    {
        StatusText.Text = "Capturing...";
        try
        {
            var png = ScreenCaptureService.CapturePng(dipRect);
            if (png == null || png.Length == 0)
            {
                StatusText.Text = "Could not capture that area. Try again.";
                return;
            }

            StatusText.Text = "Reading text...";
            var text = await OcrService.ReadAsync(png);
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "No text detected in that area.";
                LastReadText.Text = "(No text detected.)";
                ReadAgainBtn.IsEnabled = false;
                return;
            }

            _lastText = text.Trim();
            LastReadText.Text = _lastText;
            ReadAgainBtn.IsEnabled = true;
            StatusText.Text = $"Read {_lastText.Length} characters. Speaking...";
            if (_tts != null) await _tts.SpeakAsync(_lastText);
            StatusText.Text = "Done.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            CrashLogger.Log("ScreenReaderPanel.CaptureAndReadAsync", ex);
        }
    }

    private async void ReadAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || string.IsNullOrEmpty(_lastText)) return;
        await _tts.SpeakAsync(_lastText);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _lastText = "";
        LastReadText.Text = "(No text captured yet.)";
        ReadAgainBtn.IsEnabled = false;
        StatusText.Text = "Cleared.";
    }
}
