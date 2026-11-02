using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EagleBoards.App;

/// <summary>
/// Keeps a window that sizes to its content on its own monitor: never taller
/// than that monitor's work area, and moved up when it grows past the bottom
/// (an error shown, a list of matches), so its buttons stay in view.
/// SystemParameters.WorkArea is the primary monitor's alone, and a smaller
/// or more scaled second screen ran a dialog off its edge.
/// </summary>
internal static class ScreenFit
{
    public static void Keep(Window window)
    {
        window.SourceInitialized += (_, _) => Fit(window);
        window.DpiChanged += (_, _) => Fit(window);
        window.LocationChanged += (_, _) => Fit(window);
        window.SizeChanged += (_, _) => Fit(window);
    }

    private static void Fit(Window window)
    {
        if (WorkArea(window) is not { } work)
        {
            window.MaxHeight = SystemParameters.WorkArea.Height;
            return;
        }

        if (window.MaxHeight != work.Height)
        {
            window.MaxHeight = work.Height;
        }

        if (window.ActualHeight > 0 && window.Top + window.ActualHeight > work.Bottom)
        {
            window.Top = Math.Max(work.Top, work.Bottom - window.ActualHeight);
        }
    }

    /// <summary>The work area of the monitor the window is on, in its DIPs; null before it has a handle.</summary>
    private static Rect? WorkArea(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */), ref info))
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var r = info.Work;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Bounds Monitor;
        public Bounds Work;
        public uint Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
