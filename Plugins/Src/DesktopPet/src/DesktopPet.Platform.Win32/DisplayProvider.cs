using DesktopPet.Abstractions;
using System.Runtime.InteropServices;

namespace DesktopPet.Platform.Win32;

public static unsafe class DisplayProvider
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        Win32Native.MonitorEnumProc callback = (nint monitor, nint hdc, ref Win32Native.RECT rect, nint data) =>
        {
            var info = new Win32Native.MONITORINFOEXW
            {
                cbSize = (uint)Marshal.SizeOf<Win32Native.MONITORINFOEXW>()
            };

            if (Win32Native.GetMonitorInfoW(monitor, ref info))
            {
                var deviceName = new string(info.szDevice);

                displays.Add(new DisplayInfo(
                    deviceName,
                    info.rcWork.Left,
                    info.rcWork.Top,
                    info.rcWork.Right - info.rcWork.Left,
                    info.rcWork.Bottom - info.rcWork.Top,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        Win32Native.EnumDisplayMonitors(0, 0, callback, 0);
        return displays;
    }
}
