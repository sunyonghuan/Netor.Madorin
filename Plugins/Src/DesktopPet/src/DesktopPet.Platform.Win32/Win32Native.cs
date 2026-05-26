using System.Runtime.InteropServices;

namespace DesktopPet.Platform.Win32;

internal static partial class Win32Native
{
    internal const int CW_USEDEFAULT = unchecked((int)0x80000000);
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_THICKFRAME = 0x00040000;
    internal const int WS_SYSMENU = 0x00080000;
    internal const int WS_MINIMIZEBOX = 0x00020000;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    internal const int WM_DESTROY = 0x0002;
    internal const int WM_MOVE = 0x0003;
    internal const int WM_SIZE = 0x0005;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_ERASEBKGND = 0x0014;
    internal const int WM_COMMAND = 0x0111;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_PAINT = 0x000F;
    internal const int WM_USER = 0x0400;
    internal const int WM_LBUTTONDOWN   = 0x0201;
    internal const int WM_LBUTTONDBLCLK = 0x0203;
    internal const int WM_RBUTTONDOWN   = 0x0204;
    internal const int WM_RBUTTONUP     = 0x0205;
    internal const int WM_MOUSEMOVE     = 0x0200;
    internal const int WM_MOUSEWHEEL    = 0x020A;
    internal const int WM_CAPTURECHANGED = 0x0215;
    internal const int WM_NCCALCSIZE = 0x0083;
    internal const int WM_NCACTIVATE = 0x0086;
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int WM_QUIT = 0x0012;
    internal const int WM_SETCURSOR = 0x0020;
    internal const int WM_MOUSELEAVE = 0x02A3;

    internal const int HTCLIENT_CURSOR = 1; // alias for clarity in WM_SETCURSOR
    internal static readonly nint IDC_ARROW = new(32512);

    [LibraryImport("user32.dll")]
    internal static partial nint LoadCursorW(nint hInstance, nint lpCursorName);

    [LibraryImport("user32.dll")]
    internal static partial nint SetCursor(nint hCursor);

    internal const int HTTRANSPARENT = -1;
    internal const int HTCLIENT = 1;
    internal const int HTCAPTION = 2;
    internal const int HTLEFT = 10;
    internal const int HTRIGHT = 11;
    internal const int HTTOP = 12;
    internal const int HTTOPLEFT = 13;
    internal const int HTTOPRIGHT = 14;
    internal const int HTBOTTOM = 15;
    internal const int HTBOTTOMLEFT = 16;
    internal const int HTBOTTOMRIGHT = 17;

    internal const uint LWA_ALPHA = 0x00000002;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint IMAGE_ICON = 1;
    internal const uint LR_SHARED = 0x00008000;
    internal static readonly nint IDI_APPLICATION = new(32512);

    internal static readonly nint HWND_TOPMOST = new(-1);
    internal static readonly nint HWND_NOTOPMOST = new(-2);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public long rgbReserved1;
        public long rgbReserved2;
        public long rgbReserved3;
        public long rgbReserved4;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    internal delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint data);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WNDCLASSEXW wndClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG message, nint hwnd, uint messageFilterMin, uint messageFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG message);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(ref MSG message);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    internal static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint hwnd, ref PAINTSTRUCT paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(nint menu, uint flags, nuint idNewItem, string? item);

    [LibraryImport("user32.dll")]
    internal static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    internal static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    internal static partial nint SendMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    internal static partial nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellNotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(nint hdc, nint clipRect, MonitorEnumProc proc, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFOEXW info);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint hdc, ref RECT rect, nint brush);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint obj);

    // ── DWM per-pixel alpha compositing ──────────────────────────────────────

    /// <summary>
    /// MARGINS used by DwmExtendFrameIntoClientArea.
    /// Set all four to -1 to extend the DWM glass sheet across the entire window,
    /// which enables per-pixel alpha compositing for D3D11-rendered content.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    /// <summary>
    /// Extends the DWM glass frame into the client area.
    /// Passing MARGINS{-1,-1,-1,-1} covers the whole window, turning it into a
    /// per-pixel-alpha surface whose transparent pixels show the desktop behind.
    /// </summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    /// <summary>
    /// Forces DWM to flush pending composition for the given window.
    /// </summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    // ── Dialog helpers ────────────────────────────────────────────────────────

    internal const int WHITE_BRUSH      = 0;
    internal const int DEFAULT_GUI_FONT = 17;

    internal const uint TME_LEAVE = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public uint  cbSize;
        public uint  dwFlags;
        public nint  hwndTrack;
        public uint  dwHoverTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    [LibraryImport("gdi32.dll")]
    internal static partial nint GetStockObject(int fnObject);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsDialogMessageW(nint hwnd, ref MSG msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessageW(out MSG message, nint hwnd, uint msgFilterMin, uint msgFilterMax, uint removeMsg);

    // ── 窗口样式动态修改 ──────────────────────────────────────────────────────

    internal const int GWL_EXSTYLE = -20;

    /// <summary>
    /// 64 位进程必须用 GetWindowLongPtrW；此处用 nint 返回值兼容 32/64 位。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtrW(nint hwnd, int nIndex);

    /// <summary>
    /// 64 位进程必须用 SetWindowLongPtrW；此处用 nint 兼容 32/64 位。
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtrW(nint hwnd, int nIndex, nint dwNewLong);
}
