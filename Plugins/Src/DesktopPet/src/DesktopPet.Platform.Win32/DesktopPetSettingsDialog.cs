using DesktopPet.Abstractions;
using System.Runtime.InteropServices;

namespace DesktopPet.Platform.Win32;

/// <summary>
/// 纯 Win32 设置对话框，无任何 UI 框架依赖。
/// 显示连接设置（Host / Port / AutoConnect）供用户编辑，
/// 点击"保存"后返回更新后的 PetConnectionSettings；点击"取消"返回 null。
/// </summary>
public sealed class DesktopPetSettingsDialog
{
    // ── 控件 ID ───────────────────────────────────────────────────────────────
    private const int IdEditHost        = 102;
    private const int IdEditPort        = 104;
    private const int IdCheckAutoConnect = 105;
    private const int IdBtnSave         = 106;
    private const int IdBtnCancel       = 107;

    // ── Win32 样式常量 ────────────────────────────────────────────────────────
    private const int WS_VISIBLE        = 0x10000000;
    private const int WS_CHILD          = 0x40000000;
    private const int WS_BORDER         = 0x00800000;
    private const int WS_TABSTOP        = 0x00010000;
    private const int WS_CAPTION        = 0x00C00000;
    private const int WS_SYSMENU        = 0x00080000;
    private const int WS_POPUP          = unchecked((int)0x80000000);
    private const int WS_EX_DLGMODALFRAME = 0x00000001;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int ES_LEFT           = 0x0000;
    private const int ES_AUTOHSCROLL    = 0x0080;
    private const int ES_NUMBER         = 0x2000;
    private const int BS_DEFPUSHBUTTON  = 0x00000001;
    private const int BS_PUSHBUTTON     = 0x00000000;
    private const int BS_AUTOCHECKBOX   = 0x00000003;
    private const int SS_LEFT           = 0x00000000;

    private const int WM_DESTROY        = 0x0002;
    private const int WM_CLOSE          = 0x0010;
    private const int WM_COMMAND        = 0x0111;
    private const int WM_SETFONT        = 0x0030;
    private const int WM_GETTEXT        = 0x000D;
    private const int BM_GETCHECK       = 0x00F0;
    private const int BM_SETCHECK       = 0x00F1;
    private const int BST_CHECKED       = 1;
    private const int BST_UNCHECKED     = 0;

    private const string DialogClassName = "DesktopPetSettingsDialogClass";

    // ── 实例状态 ──────────────────────────────────────────────────────────────
    private nint _hwnd;
    private nint _editHost;
    private nint _editPort;
    private nint _checkAutoConnect;
    private PetConnectionSettings? _result;
    private bool _closed;

    // 必须保持委托的强引用，防止 GC 回收
    private readonly Win32Native.WindowProc _windowProc;

    public DesktopPetSettingsDialog()
    {
        _windowProc = WndProc;
    }

    // ── 公开入口 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 以模态方式显示设置对话框。
    /// 返回更新后的 <see cref="PetConnectionSettings"/>；用户取消则返回 null。
    /// </summary>
    public PetConnectionSettings? ShowModal(nint ownerHwnd, PetConnectionSettings current)
    {
        RegisterClass();
        CreateDialogWindow(ownerHwnd, current);

        if (_closed) return null;

        // 模态消息循环：PeekMessage 非阻塞轮询，避免 DestroyWindow 后 GetMessageW 永久挂起。
        // IsDialogMessageW 负责 Tab 键焦点切换等对话框键盘行为。
        const uint PM_REMOVE = 0x0001;
        while (!_closed)
        {
            if (Win32Native.PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
            {
                if (msg.message == Win32Native.WM_QUIT)
                {
                    // 把 WM_QUIT 重新投递回主循环，避免吞掉退出信号
                    Win32Native.PostQuitMessage((int)msg.wParam);
                    break;
                }

                if (!Win32Native.IsDialogMessageW(_hwnd, ref msg))
                {
                    Win32Native.TranslateMessage(ref msg);
                    Win32Native.DispatchMessageW(ref msg);
                }
            }
            else
            {
                // 无消息时短暂让出 CPU，避免空转
                System.Threading.Thread.Sleep(1);
            }
        }

        return _result;
    }

    // ── 窗口类注册 ────────────────────────────────────────────────────────────

    private void RegisterClass()
    {
        var className = Marshal.StringToHGlobalUni(DialogClassName);
        try
        {
            var wndClass = new Win32Native.WNDCLASSEXW
            {
                cbSize        = (uint)Marshal.SizeOf<Win32Native.WNDCLASSEXW>(),
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_windowProc),
                hInstance     = Win32Native.GetModuleHandleW(0),
                hbrBackground = Win32Native.GetStockObject(Win32Native.WHITE_BRUSH),
                lpszClassName = className,
            };
            Win32Native.RegisterClassExW(ref wndClass); // 重复注册时忽略错误
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    // ── 窗口 + 控件创建 ───────────────────────────────────────────────────────

    private void CreateDialogWindow(nint ownerHwnd, PetConnectionSettings current)
    {
        const int W = 360;
        const int H = 220;

        // 居中于 owner 窗口（若无 owner 则偏移到屏幕中部）
        var x = 200;
        var y = 200;
        if (ownerHwnd != 0 && Win32Native.GetWindowRect(ownerHwnd, out var ownerRect))
        {
            x = ownerRect.Left + (ownerRect.Right  - ownerRect.Left - W) / 2;
            y = ownerRect.Top  + (ownerRect.Bottom - ownerRect.Top  - H) / 2;
        }

        _hwnd = Win32Native.CreateWindowExW(
            WS_EX_DLGMODALFRAME | WS_EX_TOPMOST,
            DialogClassName,
            "DesktopPet 设置",
            WS_POPUP | WS_CAPTION | WS_SYSMENU,
            x, y, W, H,
            ownerHwnd, 0,
            Win32Native.GetModuleHandleW(0), 0);

        if (_hwnd == 0)
        {
            _closed = true;
            return;
        }

        var hFont = Win32Native.GetStockObject(Win32Native.DEFAULT_GUI_FONT);

        // 第一行：Host
        CreateAndFontControl("STATIC", "Cortana 主机地址：",
            SS_LEFT | WS_VISIBLE | WS_CHILD,
            14, 18, 156, 18, 101, hFont);

        _editHost = CreateAndFontControl("EDIT", current.Host,
            ES_LEFT | ES_AUTOHSCROLL | WS_BORDER | WS_VISIBLE | WS_CHILD | WS_TABSTOP,
            178, 16, 168, 22, IdEditHost, hFont);

        // 第二行：Port
        CreateAndFontControl("STATIC", "端口号：",
            SS_LEFT | WS_VISIBLE | WS_CHILD,
            14, 52, 156, 18, 103, hFont);

        _editPort = CreateAndFontControl("EDIT", current.Port.ToString(),
            ES_LEFT | ES_AUTOHSCROLL | ES_NUMBER | WS_BORDER | WS_VISIBLE | WS_CHILD | WS_TABSTOP,
            178, 50, 168, 22, IdEditPort, hFont);

        // 第三行：AutoConnect 复选框
        _checkAutoConnect = CreateAndFontControl("BUTTON", "启动时自动连接 Cortana",
            BS_AUTOCHECKBOX | WS_VISIBLE | WS_CHILD | WS_TABSTOP,
            14, 86, 280, 22, IdCheckAutoConnect, hFont);

        Win32Native.SendMessageW(_checkAutoConnect, BM_SETCHECK,
            current.AutoConnect ? (nuint)BST_CHECKED : BST_UNCHECKED, 0);

        // 按钮区：右对齐，右边留 14px 与左边对称
        // W=360, 按钮宽82，间距8：取消按钮右边界=360-14=346，保存按钮在其左边
        CreateAndFontControl("BUTTON", "保存",
            BS_DEFPUSHBUTTON | WS_VISIBLE | WS_CHILD | WS_TABSTOP,
            250, 152, 82, 28, IdBtnSave, hFont);

        CreateAndFontControl("BUTTON", "取消",
            BS_PUSHBUTTON | WS_VISIBLE | WS_CHILD | WS_TABSTOP,
            264, 152, 82, 28, IdBtnCancel, hFont);

        Win32Native.ShowWindow(_hwnd, 5 /* SW_SHOW */);
        Win32Native.UpdateWindow(_hwnd);
    }

    private nint CreateAndFontControl(
        string className, string text, int style,
        int x, int y, int w, int h, int id, nint hFont)
    {
        var hwnd = Win32Native.CreateWindowExW(
            0, className, text, style,
            x, y, w, h,
            _hwnd, (nint)id,
            Win32Native.GetModuleHandleW(0), 0);

        if (hwnd != 0 && hFont != 0)
        {
            Win32Native.SendMessageW(hwnd, WM_SETFONT, (nuint)hFont, 1);
        }

        return hwnd;
    }

    // ── 消息处理 ──────────────────────────────────────────────────────────────

    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_COMMAND:
            {
                var id = (int)(wParam & 0xFFFF);
                if (id == IdBtnSave)
                {
                    CommitSettings();
                    Win32Native.DestroyWindow(_hwnd);
                    return 0;
                }
                if (id == IdBtnCancel)
                {
                    _result = null;
                    Win32Native.DestroyWindow(_hwnd);
                    return 0;
                }
                break;
            }
            case WM_CLOSE:
                _result = null;
                Win32Native.DestroyWindow(_hwnd);
                return 0;

            case WM_DESTROY:
                _closed = true;
                // 投递 WM_QUIT 结束模态消息循环，但不退出宿主应用
                // 使用 PostQuitMessage 会退出整个应用，这里改为设置标志即可
                // （循环条件已是 !_closed，DestroyWindow 后 _closed=true 自然退出）
                return 0;
        }

        return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void CommitSettings()
    {
        var host = ReadEditText(_editHost).Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";

        var portText = ReadEditText(_editPort).Trim();
        var port = int.TryParse(portText, out var p) && p is >= 1 and <= 65535 ? p : 52841;

        var autoConnect =
            (int)Win32Native.SendMessageW(_checkAutoConnect, BM_GETCHECK, 0, 0) == BST_CHECKED;

        _result = new PetConnectionSettings
        {
            Host        = host,
            Port        = port,
            AutoConnect = autoConnect,
        };
    }

    private static string ReadEditText(nint editHwnd)
    {
        if (editHwnd == 0) return string.Empty;

        const int BufLen = 512;
        var buf = Marshal.AllocHGlobal(BufLen * 2); // Unicode chars
        try
        {
            Win32Native.SendMessageW(editHwnd, WM_GETTEXT, (nuint)BufLen, buf);
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
