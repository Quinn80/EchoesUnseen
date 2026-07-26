using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace EchoesUnseen.Services;

/// <summary>
/// Win32 global hotkey registration wrapper.
///
/// WPF's built-in KeyBinding and Command shortcuts only work when the app has
/// keyboard focus. That's useless for an overlay — Guild Wars 2 will always
/// have focus. So we use the Win32 RegisterHotKey API to get OS-level hotkeys
/// that fire regardless of which window is in focus.
///
/// USAGE:
///   var hotkeys = new GlobalHotkeyService(mainWindow);
///   hotkeys.Register("F1", () => ShowPanel("screen-reader"));
///   hotkeys.Register("Ctrl+Shift+H", () => ResetHudPosition());
///   // ... on shutdown:
///   hotkeys.Dispose();
///
/// NOTE: this MUST be created after the window handle exists
/// (i.e. after OnSourceInitialized), otherwise the HWND is 0.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    // ── Win32 interop ────────────────────────────────────────────────────────
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;
    private bool _disposed;

    public GlobalHotkeyService(System.Windows.Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("Could not get HwndSource for window.");
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Register a hotkey by string spec like "F1", "Ctrl+Shift+H", "Alt+M".
    /// Returns true if registered successfully. Duplicate registrations
    /// (same key combo already claimed by another app) return false.
    /// </summary>
    public bool Register(string spec, Action callback)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;
        if (!TryParse(spec, out var modifiers, out var vk)) return false;

        var id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers, vk)) return false;

        _callbacks[id] = callback;
        return true;
    }

    /// <summary>
    /// Unregister all hotkeys. Call this before registering a new set
    /// (for example when the user changes keybinds in Settings).
    /// </summary>
    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys.ToList())
            UnregisterHotKey(_hwnd, id);
        _callbacks.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                try { cb(); }
                catch (Exception ex) { CrashLogger.Log("GlobalHotkey callback", ex); }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Parse a spec like "Ctrl+Shift+H" into Win32 modifier flags and a virtual-key code.
    /// Supports: Ctrl, Shift, Alt, Win as modifiers; F1–F12, letter keys, Escape as main keys.
    /// </summary>
    private static bool TryParse(string spec, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "shift":                modifiers |= MOD_SHIFT;   break;
                case "alt":                  modifiers |= MOD_ALT;     break;
                case "win":                  modifiers |= MOD_WIN;     break;
                default: return false;
            }
        }

        var last = parts[^1];
        // F1–F24
        if (last.StartsWith('F') && int.TryParse(last[1..], out var fn) && fn >= 1 && fn <= 24)
        {
            vk = (uint)(0x70 + fn - 1); // VK_F1 = 0x70
            return true;
        }
        // Letters
        if (last.Length == 1 && char.IsLetter(last[0]))
        {
            vk = char.ToUpperInvariant(last[0]);
            return true;
        }
        // Digits
        if (last.Length == 1 && char.IsDigit(last[0]))
        {
            vk = (uint)last[0];
            return true;
        }
        // Special
        switch (last.ToLowerInvariant())
        {
            case "escape": case "esc": vk = 0x1B; return true;
            case "space":              vk = 0x20; return true;
            case "enter": case "return": vk = 0x0D; return true;
            case "tab":                vk = 0x09; return true;
            case "left":               vk = 0x25; return true; // VK_LEFT
            case "up":                 vk = 0x26; return true; // VK_UP
            case "right":              vk = 0x27; return true; // VK_RIGHT
            case "down":               vk = 0x28; return true; // VK_DOWN
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        try { _source.RemoveHook(WndProc); } catch { }
    }
}
