using DesktopPet.Abstractions;
using DesktopPet.Configuration;
using System.Runtime.InteropServices;

namespace DesktopPet.Platform.Win32;

public sealed class DesktopPetWindow
{
    private const string WindowClassName = "DesktopPetWindowClass";
    private const int SW_SHOW = 5;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_POPUP = 0x00000010;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TrayCallbackMessage = Win32Native.WM_USER + 1;

    private const int MenuShow = 1001;
    private const int MenuHide = 1002;
    private const int MenuTopMost = 1003;
    private const int MenuClickThrough = 1004;
    private const int MenuLockPosition = 1005;
    private const int MenuResetPlacement = 1006;
    private const int MenuOpenModelsDirectory = 1007;
    private const int MenuOpenConfigurationDirectory = 1008;
    private const int MenuOpenLogsDirectory = 1009;
    private const int MenuSizeSmall = 1010;
    private const int MenuSizeMedium = 1011;
    private const int MenuSizeLarge = 1012;
    private const int MenuOpacity100 = 1020;
    private const int MenuOpacity85 = 1021;
    private const int MenuOpacity70 = 1022;
    private const int MenuStartup = 1030;
    private const int MenuSettings = 1035;
    // IDs 1040–1079 reserved for dynamic Live2D model entries (up to 40 models)
    private const int MenuModelBase = 1040;
    private const int MenuModelMax = 1079;
    // IDs 1080–1119 reserved for dynamic GLB/VRM model entries (up to 40 models)
    private const int MenuGltfModelBase = 1080;
    private const int MenuGltfModelMax = 1119;
    private const int MenuExit = 1099;

    private readonly DesktopPetSettingsStore _settingsStore;
    private readonly Win32Native.WindowProc _windowProc;
    private readonly DesktopPetTrayIcon _trayIcon = new();
    private readonly bool _useLayeredWindow;
    private DesktopPetSettings _settings;
    private GCHandle _selfHandle;
    private nint _hwnd;
    private bool _isHidden;
    private bool _isCreated;
    private string[] _availableModels = [];
    private string[] _availableGltfModels = [];

    // Right-mouse-button drag state (for 3D model rotation)
    private bool  _rmbDragging;     // true once drag threshold crossed
    private bool  _rmbDown;         // true between WM_RBUTTONDOWN and WM_RBUTTONUP
    private bool  _rmbSelfRelease;  // true when WE call ReleaseCapture (suppress WM_CAPTURECHANGED reset)
    private int   _rmbStartX;
    private int   _rmbStartY;
    private int   _rmbLastX;
    private int   _rmbLastY;
    private const int RmbDragThreshold = 8; // pixels before drag is recognised

    // Mouse hover / border overlay state
    private bool _mouseInWindow;    // true while mouse is inside the client area
    private nint _arrowCursor;      // IDC_ARROW handle, loaded once

    public DesktopPetWindow(DesktopPetSettingsStore settingsStore, bool useLayeredWindow = true)
    {
        _settingsStore = settingsStore;
        _useLayeredWindow = useLayeredWindow;
        _settings = _settingsStore.Load();
        _windowProc = WndProc;
    }

    public event EventHandler<DesktopPetWindowResizedEventArgs>? Resized;

    public event EventHandler? OpenModelsDirectoryRequested;

    public event EventHandler? OpenConfigurationDirectoryRequested;

    public event EventHandler? OpenLogsDirectoryRequested;

    public event EventHandler? StartupToggleRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? Closing;

    /// <summary>Fired when the user picks a model from the tray menu. Arg is the model folder name.</summary>
    public event EventHandler<string>? ModelSwitchRequested;

    /// <summary>Fired when the user picks a GLB/VRM model from the tray menu. Arg is the model file name (without extension).</summary>
    public event EventHandler<string>? GltfModelSwitchRequested;

    /// <summary>Fired when the user scrolls the mouse wheel over the pet window. Delta is +1 (up) or -1 (down).</summary>
    public event EventHandler<int>? MouseWheelScrolled;

    /// <summary>
    /// Fired continuously while the user right-mouse-drags over the pet window.
    /// Args are (deltaX, deltaY) in screen pixels since the last event.
    /// </summary>
    public event EventHandler<(int DeltaX, int DeltaY)>? RightMouseDragged;

    /// <summary>
    /// Fired when the mouse enters or leaves the window client area.
    /// True = mouse entered (show border), False = mouse left (hide border).
    /// </summary>
    public event EventHandler<bool>? MouseHoverChanged;

    /// <summary>当前连接设置（供外部读取，用于打开设置对话框时填入初值）。</summary>
    public PetConnectionSettings ConnectionSettings => _settings.Connection;

    /// <summary>保存新的连接设置到持久化存储。</summary>
    public void SaveConnectionSettings(PetConnectionSettings connection)
    {
        _settings = _settings with { Connection = connection };
        _settingsStore.Save(_settings);
    }

    /// <summary>The currently displayed model name (shown with a checkmark in the menu).</summary>
    public string? CurrentModelName { get; set; }

    /// <summary>The currently displayed GLB/VRM model name (shown with a checkmark in the 3D model menu).</summary>
    public string? CurrentGltfModelName { get; set; }

    /// <summary>Provides the list of available model names shown in the "更换模型" submenu.</summary>
    public void SetAvailableModels(IEnumerable<string> modelNames)
    {
        _availableModels = modelNames.Take(MenuModelMax - MenuModelBase + 1).ToArray();
    }

    /// <summary>Provides the list of available GLB/VRM model names shown in the "3D 模型" submenu.</summary>
    public void SetAvailableGltfModels(IEnumerable<string> modelNames)
    {
        _availableGltfModels = modelNames.Take(MenuGltfModelMax - MenuGltfModelBase + 1).ToArray();
    }

    public nint Handle => _hwnd;

    public int Width => _settings.Window.Width;

    public int Height => _settings.Window.Height;

    public bool UsePlaceholderPaint { get; set; } = true;

    public int Run()
    {
        Create();
        Show();
        return RunMessageLoop();
    }

    public void Create()
    {
        if (_isCreated)
        {
            return;
        }

        _selfHandle = GCHandle.Alloc(this);
        RegisterWindowClass();
        try
        {
            CreateWindow();
            _isCreated = true;
        }
        catch
        {
            ReleaseSelfHandle();
            throw;
        }
    }

    public void Show()
    {
        EnsureCreated();
        Win32Native.ShowWindow(_hwnd, SW_SHOW);
        Win32Native.UpdateWindow(_hwnd);
    }

    public int RunMessageLoop()
    {
        while (Win32Native.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            Win32Native.TranslateMessage(ref message);
            Win32Native.DispatchMessageW(ref message);
        }

        return 0;
    }

    private void RegisterWindowClass()
    {
        _arrowCursor = Win32Native.LoadCursorW(0, Win32Native.IDC_ARROW);

        var className = Marshal.StringToHGlobalUni(WindowClassName);
        try
        {
            var wndClass = new Win32Native.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<Win32Native.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
                hInstance = Win32Native.GetModuleHandleW(0),
                hCursor = _arrowCursor,   // 设置默认箭头光标，避免显示等待转圈
                hbrBackground = 0,
                lpszClassName = className
            };

            var atom = Win32Native.RegisterClassExW(ref wndClass);
            if (atom == 0)
            {
                // Class may already be registered in test or restart scenarios.
            }
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    private void CreateWindow()
    {
        var displays = DisplayProvider.GetDisplays();
        var placement = WindowPlacementService.ResolveStartupPlacement(_settings.Window, displays);
        _settings = _settings with { Window = placement };

        var exStyle = Win32Native.WS_EX_TOOLWINDOW;
        if (_useLayeredWindow)
        {
            exStyle |= Win32Native.WS_EX_LAYERED;
        }

        if (placement.TopMost)
        {
            exStyle |= Win32Native.WS_EX_TOPMOST;
        }

        if (placement.ClickThrough)
        {
            exStyle |= Win32Native.WS_EX_TRANSPARENT;
        }

        // WS_POPUP only: borderless, no caption, no system buttons.
        // Resize / drag are handled entirely via WM_NCHITTEST.
        _hwnd = Win32Native.CreateWindowExW(
            exStyle,
            WindowClassName,
            "Desktop Pet",
            Win32Native.WS_POPUP,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            0,
            0,
            Win32Native.GetModuleHandleW(0),
            0);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException("Failed to create DesktopPet window.");
        }

        if (_useLayeredWindow)
        {
            // GDI / layered-window mode: whole-window alpha controlled by SetLayeredWindowAttributes.
            ApplyOpacity(placement.Opacity);
        }
        else
        {
            // D3D11 / Flip mode: extend DWM glass frame across the entire window so that
            // pixels with alpha=0 in the swap-chain back buffer show the desktop beneath.
            // The Flip present model (FlipDiscard) ensures DWM sees the actual rendered
            // pixels, unlike the old Discard path where DWM ignored them.
            var margins = new Win32Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Win32Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        }

        ApplyTopMost(placement.TopMost);
        _trayIcon.Add(_hwnd, TrayCallbackMessage, "Desktop Pet");
    }

    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return WndProcCore(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            DesktopPetWindowDiagnostics.Log(ex, $"Unhandled window message 0x{message:X}");
            return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private nint WndProcCore(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message is Win32Native.WM_CLOSE or Win32Native.WM_DESTROY)
        {
            DesktopPetWindowDiagnostics.LogInfo($"Window message 0x{message:X} received.");
        }

        switch (message)
        {
            case Win32Native.WM_NCCALCSIZE:
                // Return 0 when wParam=1: tell Windows to use the full window rect as
                // the client area, which eliminates all system-drawn borders/caption.
                if (wParam == 1)
                {
                    return 0;
                }
                return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
            case Win32Native.WM_NCACTIVATE:
                // Suppress the default non-client-area activation drawing (it would
                // briefly flash a system frame on older DWM builds).
                return 1;
            case Win32Native.WM_NCHITTEST:
                return HitTest(lParam);
            case Win32Native.WM_SETCURSOR:
            {
                // 强制整个客户区使用箭头光标，防止系统根据 HTCLIENT 显示默认的等待光标
                if ((lParam & 0xFFFF) == Win32Native.HTCLIENT)
                {
                    Win32Native.SetCursor(_arrowCursor);
                    return 1;
                }
                return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
            }
            case Win32Native.WM_LBUTTONDOWN:
            {
                // Simulate dragging by re-posting as a non-client left-button-down
                // on the caption. This lets the OS move the window natively without
                // us returning HTCAPTION from WM_NCHITTEST (which would block
                // WM_RBUTTONDOWN from reaching us as a client message).
                Win32Native.ReleaseCapture();
                Win32Native.SendMessageW(hwnd, (uint)Win32Native.WM_NCLBUTTONDOWN,
                    (nuint)Win32Native.HTCAPTION, lParam);
                return 0;
            }
            case Win32Native.WM_RBUTTONDOWN:
            {
                // Begin tracking a potential right-drag.
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                _rmbDown     = true;
                _rmbDragging = false;
                _rmbStartX   = x;
                _rmbStartY   = y;
                _rmbLastX    = x;
                _rmbLastY    = y;
                Win32Native.SetCapture(hwnd);
                return 0;
            }
            case Win32Native.WM_MOUSEMOVE:
            {
                // 鼠标首次进入窗口：订阅 WM_MOUSELEAVE 通知，触发边框显示
                if (!_mouseInWindow)
                {
                    _mouseInWindow = true;
                    var tme = new Win32Native.TRACKMOUSEEVENT
                    {
                        cbSize    = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32Native.TRACKMOUSEEVENT>(),
                        dwFlags   = Win32Native.TME_LEAVE,
                        hwndTrack = hwnd,
                        dwHoverTime = 0
                    };
                    Win32Native.TrackMouseEvent(ref tme);
                    MouseHoverChanged?.Invoke(this, true);
                }

                if (!_rmbDown) return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                var dx = x - _rmbStartX;
                var dy = y - _rmbStartY;
                if (!_rmbDragging && (Math.Abs(dx) > RmbDragThreshold || Math.Abs(dy) > RmbDragThreshold))
                {
                    _rmbDragging = true;
                }
                if (_rmbDragging)
                {
                    var moveDx = x - _rmbLastX;
                    var moveDy = y - _rmbLastY;
                    if (moveDx != 0 || moveDy != 0)
                    {
                        RightMouseDragged?.Invoke(this, (moveDx, moveDy));
                    }
                    _rmbLastX = x;
                    _rmbLastY = y;
                }
                return 0;
            }
            case Win32Native.WM_MOUSELEAVE:
            {
                // 鼠标离开窗口：隐藏边框
                _mouseInWindow = false;
                MouseHoverChanged?.Invoke(this, false);
                return 0;
            }
            case Win32Native.WM_RBUTTONUP:
            {
                // Save dragging state BEFORE we call ReleaseCapture.
                // ReleaseCapture() synchronously dispatches WM_CAPTURECHANGED; we mark
                // _rmbSelfRelease=true so that handler knows not to interfere.
                var wasDragging = _rmbDragging;
                _rmbDown        = false;
                _rmbDragging    = false;
                _rmbSelfRelease = true;
                Win32Native.ReleaseCapture();
                _rmbSelfRelease = false;
                // Only show the context menu if the button was released without dragging.
                if (!wasDragging)
                {
                    ShowContextMenu();
                }
                return 0;
            }
            case Win32Native.WM_CAPTURECHANGED:
            {
                // Ignore if we triggered the release ourselves (WM_RBUTTONUP path).
                if (_rmbSelfRelease) return 0;
                // Mouse capture lost externally — cancel drag without showing menu.
                _rmbDown     = false;
                _rmbDragging = false;
                return 0;
            }
            case TrayCallbackMessage:
                HandleTrayMessage((uint)lParam);
                return 0;
            case Win32Native.WM_ERASEBKGND:
                return 1;
            case Win32Native.WM_PAINT:
                if (UsePlaceholderPaint)
                {
                    PaintPlaceholder(hwnd);
                }
                else
                {
                    FinishPaint(hwnd);
                }

                return 0;
            case Win32Native.WM_MOUSEWHEEL:
            {
                // High word of wParam = signed wheel delta (positive = forward/zoom-in)
                var delta = (short)(wParam >> 16);
                MouseWheelScrolled?.Invoke(this, delta > 0 ? 1 : -1);
                return 0;
            }
            case Win32Native.WM_COMMAND:
                HandleMenuCommand((int)(wParam & 0xffff));
                return 0;
            case Win32Native.WM_MOVE:
                SaveCurrentPlacement();
                return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
            case Win32Native.WM_SIZE:
                SaveCurrentPlacement();
                Resized?.Invoke(this, new DesktopPetWindowResizedEventArgs(_settings.Window.Width, _settings.Window.Height));
                return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
            case Win32Native.WM_CLOSE:
                Closing?.Invoke(this, EventArgs.Empty);
                SaveCurrentPlacement();
                Win32Native.DestroyWindow(hwnd);
                return 0;
            case Win32Native.WM_DESTROY:
                SaveCurrentPlacement();
                _trayIcon.Dispose();
                ReleaseSelfHandle();
                Win32Native.PostQuitMessage(0);
                return 0;
            default:
                return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private nint HitTest(nint lParam)
    {
        // 边缘/角落返回对应的 resize hit code，使鼠标悬停时显示正确的缩放光标
        // 并让系统处理拖拽缩放。内部区域返回 HTCLIENT（左键拖动由 WM_LBUTTONDOWN 转发）。
        Win32Native.GetWindowRect(_hwnd, out var rect);
        var ptX = (short)(lParam & 0xFFFF);
        var ptY = (short)((lParam >> 16) & 0xFFFF);

        // 边缘感应厚度（像素），使用系统推荐的 resize frame 宽度
        const int EdgeThickness = 8;

        var left   = ptX - rect.Left   < EdgeThickness;
        var right  = rect.Right - ptX  < EdgeThickness;
        var top    = ptY - rect.Top    < EdgeThickness;
        var bottom = rect.Bottom - ptY < EdgeThickness;

        if (top    && left)  return Win32Native.HTTOPLEFT;
        if (top    && right) return Win32Native.HTTOPRIGHT;
        if (bottom && left)  return Win32Native.HTBOTTOMLEFT;
        if (bottom && right) return Win32Native.HTBOTTOMRIGHT;
        if (top)             return Win32Native.HTTOP;
        if (bottom)          return Win32Native.HTBOTTOM;
        if (left)            return Win32Native.HTLEFT;
        if (right)           return Win32Native.HTRIGHT;

        return Win32Native.HTCLIENT;
    }

    private void ShowContextMenu()
    {
        var menu = Win32Native.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            Append(menu, MenuShow, "显示");
            Append(menu, MenuHide, "隐藏");
            Separator(menu);
            Append(menu, MenuTopMost, _settings.Window.TopMost ? "取消置顶" : "置顶");
            Append(menu, MenuClickThrough, _settings.Window.ClickThrough ? "关闭点击穿透" : "开启点击穿透");
            Append(menu, MenuLockPosition, _settings.Window.Locked ? "解锁位置" : "锁定位置");
            Append(menu, MenuResetPlacement, "重置到右下角");
            Separator(menu);
            // ── 更换角色：2D 和 3D 模型合并为一个子菜单（互斥选择）──────────────
            if (_availableModels.Length > 0 || _availableGltfModels.Length > 0)
            {
                var characterSubMenu = Win32Native.CreatePopupMenu();
                if (_availableModels.Length > 0)
                {
                    for (var i = 0; i < _availableModels.Length; i++)
                    {
                        var name = _availableModels[i];
                        var isCurrent = string.Equals(name, CurrentModelName, StringComparison.OrdinalIgnoreCase);
                        Append(characterSubMenu, MenuModelBase + i, isCurrent ? $"✓ {name}" : name);
                    }
                }
                if (_availableGltfModels.Length > 0)
                {
                    if (_availableModels.Length > 0)
                    {
                        Separator(characterSubMenu);
                    }
                    for (var i = 0; i < _availableGltfModels.Length; i++)
                    {
                        var name = _availableGltfModels[i];
                        var isCurrent = string.Equals(name, CurrentGltfModelName, StringComparison.OrdinalIgnoreCase);
                        Append(characterSubMenu, MenuGltfModelBase + i, isCurrent ? $"✓ {name}" : name);
                    }
                }
                Win32Native.AppendMenuW(menu, MF_POPUP, (nuint)characterSubMenu, "更换角色");
                Separator(menu);
            }
            Append(menu, MenuOpenModelsDirectory, "打开模型目录");
            Append(menu, MenuOpenConfigurationDirectory, "打开配置目录");
            Append(menu, MenuOpenLogsDirectory, "打开日志目录");
            Separator(menu);
            Append(menu, MenuStartup, "切换开机启动");
            Append(menu, MenuSettings, "设置…");
            Separator(menu);
            Append(menu, MenuSizeSmall, "窗口大小：小");
            Append(menu, MenuSizeMedium, "窗口大小：中");
            Append(menu, MenuSizeLarge, "窗口大小：大");
            Separator(menu);
            Append(menu, MenuOpacity100, "透明度：100%");
            Append(menu, MenuOpacity85, "透明度：85%");
            Append(menu, MenuOpacity70, "透明度：70%");
            Separator(menu);
            Append(menu, MenuExit, "退出");

            Win32Native.GetCursorPos(out var point);
            var command = Win32Native.TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                0,
                _hwnd,
                0);

            if (command != 0)
            {
                HandleMenuCommand((int)command);
            }
        }
        finally
        {
            Win32Native.DestroyMenu(menu);
        }
    }

    private static void PaintPlaceholder(nint hwnd)
    {
        var hdc = Win32Native.BeginPaint(hwnd, out var paint);
        if (hdc == 0)
        {
            return;
        }

        var brush = Win32Native.CreateSolidBrush(0x00D7A64D);
        try
        {
            Win32Native.FillRect(hdc, ref paint.rcPaint, brush);
        }
        finally
        {
            if (brush != 0)
            {
                Win32Native.DeleteObject(brush);
            }

            Win32Native.EndPaint(hwnd, ref paint);
        }
    }

    private static void FinishPaint(nint hwnd)
    {
        var hdc = Win32Native.BeginPaint(hwnd, out var paint);
        if (hdc == 0)
        {
            return;
        }

        Win32Native.EndPaint(hwnd, ref paint);
    }

    private static void Append(nint menu, int command, string text)
    {
        Win32Native.AppendMenuW(menu, MF_STRING, (nuint)command, text);
    }

    private static void Separator(nint menu)
    {
        Win32Native.AppendMenuW(menu, MF_SEPARATOR, 0, null);
    }

    private void HandleMenuCommand(int command)
    {
        switch (command)
        {
            case MenuShow:
                _isHidden = false;
                Win32Native.ShowWindow(_hwnd, SW_SHOW);
                break;
            case MenuHide:
                _isHidden = true;
                Win32Native.ShowWindow(_hwnd, 0);
                break;
            case MenuTopMost:
                UpdateWindowSettings(_settings.Window with { TopMost = !_settings.Window.TopMost });
                ApplyTopMost(_settings.Window.TopMost);
                break;
            case MenuClickThrough:
                UpdateWindowSettings(_settings.Window with { ClickThrough = !_settings.Window.ClickThrough });
                ApplyClickThrough(_settings.Window.ClickThrough);
                break;
            case MenuLockPosition:
                UpdateWindowSettings(_settings.Window with { Locked = !_settings.Window.Locked });
                break;
            case MenuResetPlacement:
                ResetPlacement();
                break;
            case MenuOpenModelsDirectory:
                OpenModelsDirectoryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuOpenConfigurationDirectory:
                OpenConfigurationDirectoryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuOpenLogsDirectory:
                OpenLogsDirectoryRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuSizeSmall:
                ResizeTo(320, 440);
                break;
            case MenuSizeMedium:
                ResizeTo(420, 560);
                break;
            case MenuSizeLarge:
                ResizeTo(560, 720);
                break;
            case MenuOpacity100:
                SetOpacity(1.0);
                break;
            case MenuOpacity85:
                SetOpacity(0.85);
                break;
            case MenuOpacity70:
                SetOpacity(0.70);
                break;
            case MenuStartup:
                StartupToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuExit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                Closing?.Invoke(this, EventArgs.Empty);
                Win32Native.DestroyWindow(_hwnd);
                break;
            default:
                if (command >= MenuModelBase && command <= MenuModelMax)
                {
                    var modelIndex = command - MenuModelBase;
                    if (modelIndex < _availableModels.Length)
                    {
                        ModelSwitchRequested?.Invoke(this, _availableModels[modelIndex]);
                    }
                }
                else if (command >= MenuGltfModelBase && command <= MenuGltfModelMax)
                {
                    var modelIndex = command - MenuGltfModelBase;
                    if (modelIndex < _availableGltfModels.Length)
                    {
                        GltfModelSwitchRequested?.Invoke(this, _availableGltfModels[modelIndex]);
                    }
                }
                break;
        }
    }

    private void HandleTrayMessage(uint message)
    {
        switch (message)
        {
            case Win32Native.WM_LBUTTONDBLCLK:
                _isHidden = false;
                Win32Native.ShowWindow(_hwnd, SW_SHOW);
                Win32Native.UpdateWindow(_hwnd);
                break;
            case Win32Native.WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ResetPlacement()
    {
        var placement = WindowPlacementService.ResolveStartupPlacement(
            _settings.Window with
            {
                MonitorDeviceName = null,
                X = int.MaxValue / 2,
                Y = int.MaxValue / 2
            },
            DisplayProvider.GetDisplays(),
            _settings.Window.Width,
            _settings.Window.Height);

        UpdateWindowSettings(placement);
        Win32Native.SetWindowPos(
            _hwnd,
            placement.TopMost ? Win32Native.HWND_TOPMOST : Win32Native.HWND_NOTOPMOST,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            Win32Native.SWP_NOACTIVATE);
    }

    private void ResizeTo(int width, int height)
    {
        SaveCurrentPlacement();
        var placement = _settings.Window with
        {
            Width = width,
            Height = height
        };

        UpdateWindowSettings(placement);
        Win32Native.SetWindowPos(
            _hwnd,
            placement.TopMost ? Win32Native.HWND_TOPMOST : Win32Native.HWND_NOTOPMOST,
            0,
            0,
            width,
            height,
            Win32Native.SWP_NOMOVE | Win32Native.SWP_NOACTIVATE);
        Resized?.Invoke(this, new DesktopPetWindowResizedEventArgs(width, height));
    }

    private void SetOpacity(double opacity)
    {
        var placement = _settings.Window with
        {
            Opacity = opacity
        };

        UpdateWindowSettings(placement);
        ApplyOpacity(opacity);
    }

    private void ApplyOpacity(double opacity)
    {
        if (!_useLayeredWindow)
        {
            return;
        }

        var alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255), 1, 255);
        Win32Native.SetLayeredWindowAttributes(_hwnd, 0, alpha, Win32Native.LWA_ALPHA);
    }

    private void ApplyTopMost(bool topMost)
    {
        Win32Native.SetWindowPos(
            _hwnd,
            topMost ? Win32Native.HWND_TOPMOST : Win32Native.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            Win32Native.SWP_NOMOVE | Win32Native.SWP_NOSIZE | Win32Native.SWP_NOACTIVATE);
    }

    private void ApplyClickThrough(bool clickThrough)
    {
        var exStyle = Win32Native.GetWindowLongPtrW(_hwnd, Win32Native.GWL_EXSTYLE);
        if (clickThrough)
        {
            exStyle |= Win32Native.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle &= ~Win32Native.WS_EX_TRANSPARENT;
        }
        Win32Native.SetWindowLongPtrW(_hwnd, Win32Native.GWL_EXSTYLE, exStyle);
    }

    private void SaveCurrentPlacement()
    {
        if (_hwnd == 0 || _isHidden || !_settings.Window.RememberPlacement)
        {
            return;
        }

        if (!Win32Native.GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        var placement = _settings.Window with
        {
            X = rect.Left,
            Y = rect.Top,
            Width = Math.Max(1, rect.Right - rect.Left),
            Height = Math.Max(1, rect.Bottom - rect.Top),
            MonitorDeviceName = FindCurrentDisplayName(rect)
        };

        UpdateWindowSettings(placement);
    }

    private string? FindCurrentDisplayName(Win32Native.RECT rect)
    {
        var displays = DisplayProvider.GetDisplays();
        var centerX = rect.Left + (rect.Right - rect.Left) / 2;
        var centerY = rect.Top + (rect.Bottom - rect.Top) / 2;

        foreach (var display in displays)
        {
            if (centerX >= display.X
                && centerX < display.X + display.Width
                && centerY >= display.Y
                && centerY < display.Y + display.Height)
            {
                return display.DeviceName;
            }
        }

        return displays.FirstOrDefault(display => display.IsPrimary).DeviceName;
    }

    private void UpdateWindowSettings(PetWindowPlacement placement)
    {
        _settings = _settings with
        {
            Window = placement
        };

        _settingsStore.Save(_settings);
    }

    private void EnsureCreated()
    {
        if (!_isCreated || _hwnd == 0)
        {
            throw new InvalidOperationException("DesktopPet window has not been created.");
        }
    }

    private void ReleaseSelfHandle()
    {
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}
