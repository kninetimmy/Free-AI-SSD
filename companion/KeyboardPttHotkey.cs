using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FreeAiSsd.Companion;

internal sealed class KeyboardPttHotkey : NativeWindow, IDisposable
{
    private const int WmHotKey = 0x0312;
    private readonly Action _onPress;
    private readonly Action _onRelease;
    private readonly CompanionLog _log;
    private readonly Keys _key;
    private bool _pressed;

    public KeyboardPttHotkey(string binding, Action onPress, Action onRelease, CompanionLog log)
    {
        _onPress = onPress;
        _onRelease = onRelease;
        _log = log;
        var value = binding[4..].Trim();
        _key = Enum.TryParse<Keys>(value, true, out var parsed) ? parsed : Keys.F8;
        CreateHandle(new CreateParams());
    }

    public void Start()
    {
        RegisterHotKey(Handle, 1, 0, (uint)_key);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey)
        {
            if (!_pressed)
            {
                _pressed = true;
                _onPress();
                Task.Delay(100).ContinueWith(_ =>
                {
                    _pressed = false;
                    _onRelease();
                });
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        try { UnregisterHotKey(Handle, 1); } catch { }
        DestroyHandle();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
