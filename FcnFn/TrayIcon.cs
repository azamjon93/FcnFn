using System.Runtime.InteropServices;

namespace FcnFn;

internal sealed record TrayCallbacks(
    Func<bool> IsEnabled, Action ToggleEnabled,
    Func<bool> IsDiag, Action ToggleDiag,
    Func<bool> IsAutoStart, Action ToggleAutoStart,
    Action Quit);

internal sealed class TrayIcon : IDisposable
{
    private const uint ID_ENABLED = 1, ID_DIAG = 2, ID_AUTOSTART = 3, ID_QUIT = 4;
    private const string ClassName = "FcnFnTrayWnd";

    private readonly TrayCallbacks _cb;
    private readonly Native.WndProc _wndProc; // pinned
    private readonly IntPtr _hwnd;
    private readonly uint _wmTaskbarCreated;
    private IntPtr _icon;
    private Native.NOTIFYICONDATA _nid;
    private bool _disposed;

    public TrayIcon(TrayCallbacks callbacks)
    {
        _cb = callbacks;
        _wndProc = WndProcImpl;

        IntPtr hInstance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        Native.RegisterClassW(ref wc);

        _wmTaskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");

        _hwnd = Native.CreateWindowExW(0, ClassName, "FcnFn", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        _icon = IconFactory.CreateFnIcon(_cb.IsEnabled());
        _nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = Native.WM_TRAY,
            hIcon = _icon,
            szTip = Tip(_cb.IsEnabled()),
            szInfo = "",
            szInfoTitle = "",
        };
        Native.Shell_NotifyIconW(Native.NIM_ADD, ref _nid);
    }

    private static string Tip(bool enabled) => enabled ? "FcnFn — remap ON" : "FcnFn — remap OFF";

    public void SetEnabled(bool enabled)
    {
        IntPtr old = _icon;
        _icon = IconFactory.CreateFnIcon(enabled);
        _nid.hIcon = _icon;
        _nid.szTip = Tip(enabled);
        _nid.uFlags = Native.NIF_ICON | Native.NIF_TIP;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _nid);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
    }

    public void ShowBalloon(string title, string text)
    {
        _nid.uFlags = Native.NIF_INFO;
        _nid.szInfoTitle = title;
        _nid.szInfo = text;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _nid);
    }

    private void ShowMenu()
    {
        IntPtr menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING | (_cb.IsEnabled() ? Native.MF_CHECKED : 0),
            ID_ENABLED, "Enabled");
        Native.AppendMenuW(menu, Native.MF_STRING | (_cb.IsDiag() ? Native.MF_CHECKED : 0),
            ID_DIAG, "Diagnostic logging");
        Native.AppendMenuW(menu, Native.MF_STRING | (_cb.IsAutoStart() ? Native.MF_CHECKED : 0),
            ID_AUTOSTART, "Run at login");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, ID_QUIT, "Quit");

        Native.GetCursorPos(out var pt);
        Native.SetForegroundWindow(_hwnd); // so the menu dismisses on focus loss
        int cmd = Native.TrackPopupMenu(menu,
            Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        Native.DestroyMenu(menu);

        switch ((uint)cmd)
        {
            case ID_ENABLED: _cb.ToggleEnabled(); break;
            case ID_DIAG: _cb.ToggleDiag(); break;
            case ID_AUTOSTART: _cb.ToggleAutoStart(); break;
            case ID_QUIT: _cb.Quit(); break;
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _wmTaskbarCreated)
        {
            _nid.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
            Native.Shell_NotifyIconW(Native.NIM_ADD, ref _nid);
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case Native.WM_TRAY:
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == Native.WM_RBUTTONUP) ShowMenu();
                else if (ev is Native.WM_LBUTTONUP or Native.WM_LBUTTONDBLCLK) _cb.ToggleEnabled();
                return IntPtr.Zero;

            case Native.WM_ENDSESSION:
                _cb.Quit();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _cb.Quit();
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref _nid);
        if (_icon != IntPtr.Zero) { Native.DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
    }
}
