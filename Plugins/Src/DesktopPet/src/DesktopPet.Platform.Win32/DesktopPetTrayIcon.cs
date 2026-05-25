namespace DesktopPet.Platform.Win32;

public sealed class DesktopPetTrayIcon : IDisposable
{
    private const uint TrayIconId = 1;
    private nint _hwnd;
    private bool _isAdded;

    public void Add(nint hwnd, uint callbackMessage, string tooltip)
    {
        if (_isAdded)
        {
            return;
        }

        var data = CreateData(hwnd, callbackMessage, tooltip);
        _isAdded = Win32Native.ShellNotifyIconW(Win32Native.NIM_ADD, ref data);
        if (_isAdded)
        {
            _hwnd = hwnd;
        }
    }

    public void Update(nint hwnd, uint callbackMessage, string tooltip)
    {
        if (!_isAdded)
        {
            Add(hwnd, callbackMessage, tooltip);
            return;
        }

        var data = CreateData(hwnd, callbackMessage, tooltip);
        Win32Native.ShellNotifyIconW(Win32Native.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (!_isAdded)
        {
            return;
        }

        var data = new Win32Native.NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId
        };

        Win32Native.ShellNotifyIconW(Win32Native.NIM_DELETE, ref data);
        _hwnd = 0;
        _isAdded = false;
    }

    private static Win32Native.NOTIFYICONDATAW CreateData(nint hwnd, uint callbackMessage, string tooltip)
    {
        return new Win32Native.NOTIFYICONDATAW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = TrayIconId,
            uFlags = Win32Native.NIF_MESSAGE | Win32Native.NIF_ICON | Win32Native.NIF_TIP,
            uCallbackMessage = callbackMessage,
            hIcon = Win32Native.LoadIconW(0, Win32Native.IDI_APPLICATION),
            szTip = tooltip.Length <= 127 ? tooltip : tooltip[..127]
        };
    }
}
