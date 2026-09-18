using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EchoesUnseen.Services;

/// <summary>
/// Read-only low-level keyboard hook that watches the number keys 1–8 (top row
/// and numpad) so the Guitar-Hero guide's STEP mode knows when you've played a
/// note. It OBSERVES only — it never swallows or sends keys, so Guild Wars 2
/// still receives every press normally.
///
/// The hook is installed on the UI thread (which pumps messages), so the
/// <see cref="NumberKeyDown"/> event fires on the UI thread and is safe to touch
/// UI from. Install/remove is cheap; we only keep it hooked while a step-mode
/// guide is running.
/// </summary>
public sealed class KeyWatcher : IDisposable
{
    public event Action<int>? NumberKeyDown;   // fires with 1..8

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _proc;
    private IntPtr _hook = IntPtr.Zero;

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        using var cur = Process.GetCurrentProcess();
        using var mod = cur.MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod?.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            int? n = vk switch
            {
                >= 0x31 and <= 0x38 => vk - 0x30,       // top-row 1..8
                >= 0x61 and <= 0x68 => vk - 0x60,       // numpad 1..8
                _ => null,
            };
            if (n.HasValue)
            {
                try { NumberKeyDown?.Invoke(n.Value); } catch { }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
