using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

Console.WriteLine("DesktopPet Win32 AOT probe starting.");

RunD3D11Probe();
Win32WindowProbe.Run();

Console.WriteLine("DesktopPet Win32 AOT probe completed.");

static void RunD3D11Probe()
{
    var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
    var result = D3D11.D3D11CreateDevice(
        (IDXGIAdapter?)null,
        DriverType.Hardware,
        DeviceCreationFlags.BgraSupport,
        featureLevels,
        out var device,
        out var selectedFeatureLevel,
        out var context);

    if (result.Failure)
    {
        Console.WriteLine($"d3d11: skipped ({result.Code})");
        return;
    }

    Console.WriteLine($"d3d11: {selectedFeatureLevel}");

    context.Dispose();
    device.Dispose();
}

internal static unsafe partial class Win32WindowProbe
{
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOW = 5;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int LWA_ALPHA = 0x00000002;

    public static void Run()
    {
        var className = "DesktopPetWin32AotProbeWindow";
        var hInstance = GetModuleHandle(null);

        fixed (char* classNamePtr = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = hInstance,
                lpszClassName = classNamePtr
            };

            var atom = RegisterClassExW(&wc);
            if (atom == 0)
            {
                Console.WriteLine($"win32-window: RegisterClassExW failed {GetLastError()}");
                return;
            }

            var hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW,
                classNamePtr,
                "DesktopPet Win32 AOT Probe",
                WS_POPUP | WS_VISIBLE,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                320,
                220,
                0,
                0,
                hInstance,
                null);

            if (hwnd == 0)
            {
                Console.WriteLine($"win32-window: CreateWindowExW failed {GetLastError()}");
                return;
            }

            SetLayeredWindowAttributes(hwnd, 0, 220, LWA_ALPHA);
            ShowWindow(hwnd, SW_SHOW);
            Console.WriteLine("win32-window: opened");

            RenderD3D11Frames(hwnd, 320, 220);

            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < 500)
            {
                while (PeekMessageW(out var msg, 0, 0, 0, 1))
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }

                Thread.Sleep(16);
            }

            DestroyWindow(hwnd);
            Console.WriteLine("win32-window: completed");
        }
    }

    private static void RenderD3D11Frames(nint hwnd, uint width, uint height)
    {
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var swapChainDescription = new SwapChainDescription
        {
            BufferDescription = new ModeDescription
            {
                Width = width,
                Height = height,
                Format = Format.B8G8R8A8_UNorm
            },
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            OutputWindow = hwnd,
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };

        var result = D3D11.D3D11CreateDeviceAndSwapChain(
            (IDXGIAdapter?)null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            swapChainDescription,
            out var swapChain,
            out var device,
            out var selectedFeatureLevel,
            out var context);

        if (result.Failure || swapChain is null || device is null || context is null)
        {
            Console.WriteLine($"d3d11-swapchain: failed ({result.Code})");
            return;
        }

        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        using var renderTarget = device.CreateRenderTargetView(backBuffer);

        for (var frame = 0; frame < 3; frame++)
        {
            var color = new Color4(0.25f + frame * 0.15f, 0.1f, 0.7f, 0.8f);
            context.ClearRenderTargetView(renderTarget, color);
            var present = swapChain.Present(1);
            if (present.Failure)
            {
                Console.WriteLine($"d3d11-swapchain: present failed ({present.Code})");
                break;
            }
        }

        Console.WriteLine($"d3d11-swapchain: presented with {selectedFeatureLevel}");

        context.Dispose();
        device.Dispose();
        swapChain.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg is WM_CLOSE)
        {
            DestroyWindow(hwnd);
            return 0;
        }

        if (msg is WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? lpModuleName);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetLastError();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* wndClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        int dwExStyle,
        char* lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        void* lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(MSG* lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }
}
