using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 使用 Windows Job Object 跟踪子进程，确保父进程退出时所有子进程自动终止。
/// 即使主程序崩溃或被强制关闭，操作系统也会自动清理 Job 中的所有进程。
/// </summary>
/// <remarks>
/// 非 Windows 平台上为空操作（Linux/macOS 的 prctl/PR_SET_PDEATHSIG 由子进程自行设置）。
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ChildProcessTracker
{
    private static readonly nint s_jobHandle;

    static ChildProcessTracker()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 创建一个匿名 Job Object
        s_jobHandle = CreateJobObject(nint.Zero, null);
        if (s_jobHandle == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 Job Object");

        // 设置 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE：当最后一个 Job handle 关闭时终止所有进程
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOBOBJECTLIMIT.KILL_ON_JOB_CLOSE
            }
        };

        var length = Marshal.SizeOf(extendedInfo);
        var extendedInfoPtr = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

            if (!SetInformationJobObject(s_jobHandle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置 Job Object 信息");
        }
        finally
        {
            Marshal.FreeHGlobal(extendedInfoPtr);
        }
    }

    /// <summary>
    /// 将进程加入 Job Object。加入后，当主进程退出时该子进程会被自动终止。
    /// </summary>
    /// <param name="process">要跟踪的子进程。</param>
    public static void AddProcess(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows()) return;
        if (s_jobHandle == nint.Zero) return;

        if (!AssignProcessToJobObject(s_jobHandle, process.Handle))
        {
            // 不抛异常，仅记录失败（进程可能已退出或权限不足）
            Debug.WriteLine($"ChildProcessTracker: 无法将进程 {process.Id} 加入 Job Object，错误码 {Marshal.GetLastWin32Error()}");
        }
    }

    // ──────── P/Invoke ────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint hJob, JobObjectInfoType infoType, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    // ──────── 结构体 ────────

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [Flags]
    private enum JOBOBJECTLIMIT : uint
    {
        KILL_ON_JOB_CLOSE = 0x00002000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JOBOBJECTLIMIT LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
