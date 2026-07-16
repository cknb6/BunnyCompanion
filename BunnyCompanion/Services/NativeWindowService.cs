using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace BunnyCompanion.Services;

public static class NativeWindowService
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const int WsExTransparent = 0x00000020;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimize = 0x20000000;
    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    public static void SetClickThrough(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var style = GetWindowLong(handle, GwlExStyle);
        var next = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        if (next != style)
            SetWindowLong(handle, GwlExStyle, next);
    }

    /// <summary>
    /// 强制清除 WS_EX_TRANSPARENT（修复「设置已关穿透但 HWND 仍穿透」导致点不上）。
    /// </summary>
    public static void EnsureClickThroughOff(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var style = GetWindowLong(handle, GwlExStyle);
        if ((style & WsExTransparent) == 0)
            return;
        SetWindowLong(handle, GwlExStyle, style & ~WsExTransparent);
    }

    /// <summary>当前窗口是否带穿透扩展样式。</summary>
    public static bool IsClickThroughStyle(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;
        return (GetWindowLong(handle, GwlExStyle) & WsExTransparent) != 0;
    }

    /// <summary>
    /// 判断前台是否为“真正全屏”（游戏/视频等）。
    /// 普通最大化窗口、桌面、任务栏、托盘菜单、本进程窗口均不算全屏。
    /// </summary>
    public static bool TryGetForegroundFullscreen(Window petWindow, out bool isFullscreen)
    {
        isFullscreen = false;
        var foreground = GetForegroundWindow();
        var petHandle = new WindowInteropHelper(petWindow).Handle;
        if (foreground == IntPtr.Zero)
            return true;
        if (foreground == petHandle)
            return false;

        GetWindowThreadProcessId(foreground, out var foregroundProcessId);
        if (foregroundProcessId == (uint)Environment.ProcessId)
            return false;

        var className = new StringBuilder(256);
        GetClassName(foreground, className, className.Capacity);
        var name = className.ToString();

        // 桌面、任务栏、托盘溢出、系统菜单、开始菜单等：不隐藏桌宠
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "XamlExplorerHostIslandWindow"
            or "Windows.UI.Core.CoreWindow" or "#32768" or "TaskListThumbnailWnd"
            or "MultitaskingViewFrame" or "ForegroundStaging")
            return true;

        if (name.Contains("Tray", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase))
            return true;

        var style = GetWindowLong(foreground, GwlStyle);
        if ((style & WsMinimize) == WsMinimize)
            return true;

        // 带标题栏+可调边框的常规最大化窗口：不隐藏
        if ((style & WsCaption) == WsCaption && (style & WsThickFrame) != 0)
            return true;

        if (!GetWindowRect(foreground, out var windowRect))
            return true;

        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return true;

        // 只在同一块显示器上隐藏桌宠；另一块屏幕全屏不会遮挡当前桌宠。
        var petMonitor = MonitorFromWindow(petHandle, MonitorDefaultToNearest);
        if (petMonitor == IntPtr.Zero || petMonitor != monitor)
            return true;

        // 窗口几乎铺满整块物理显示器时才视为全屏
        const int tolerance = 4;
        isFullscreen = windowRect.Left <= info.Monitor.Left + tolerance
                       && windowRect.Top <= info.Monitor.Top + tolerance
                       && windowRect.Right >= info.Monitor.Right - tolerance
                       && windowRect.Bottom >= info.Monitor.Bottom - tolerance;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }
}
