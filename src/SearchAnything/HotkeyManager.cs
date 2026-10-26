using System;
using System.Windows.Interop;

namespace SearchAnything;

/// <summary>Registers a system-wide hotkey using a hidden message-only window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xF1D;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("SearchAnythingHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = NativeMethods.HWND_MESSAGE,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Replaces any previously registered shortcut. Returns false if another app already owns it.</summary>
    public bool Register(Hotkey hotkey)
    {
        Unregister();
        _registered = NativeMethods.RegisterHotKey(
            _source.Handle, HotkeyId, hotkey.NativeModifiers | NativeMethods.MOD_NOREPEAT, hotkey.VirtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }
}
