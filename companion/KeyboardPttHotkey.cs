using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FreeAiSsd.Companion;

internal sealed class KeyboardPttHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly Action _onPress;
    private readonly Action _onRelease;
    private readonly CompanionLog _log;
    private readonly int _vkCode;
    private readonly LowLevelKeyboardProc _proc; // field reference prevents GC collection
    private IntPtr _hookHandle;
    private bool _pressed;
    private bool _disposed;

    public KeyboardPttHotkey(string binding, Action onPress, Action onRelease, CompanionLog log)
    {
        _onPress = onPress;
        _onRelease = onRelease;
        _log = log;
        var value = binding[4..].Trim();
        var key = Enum.TryParse<Keys>(value, true, out var parsed) ? parsed : Keys.F8;
        _vkCode = (int)key;
        _proc = HookCallback;
    }

    /// <summary>Returns true if the hook installed successfully.</summary>
    public bool Start()
    {
        // WH_KEYBOARD_LL is a low-level global hook: hMod must be IntPtr.Zero per MSDN.
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            _log.Write($"Keyboard PTT hook failed to install: Win32 error {Marshal.GetLastWin32Error()}");
            return false;
        }
        return true;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((int)ks.vkCode == _vkCode)
            {
                var msg = (int)wParam;
                if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && !_pressed)
                {
                    _pressed = true;
                    Task.Run(_onPress);
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _pressed = false;
                    Task.Run(_onRelease);
                }
            }
        }

        // Always pass through — never swallow the key from other applications.
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
