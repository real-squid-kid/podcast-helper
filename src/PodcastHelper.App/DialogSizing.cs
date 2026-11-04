using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PodcastHelper.App;

// Ограничиваем форму рабочей областью её монитора, включая масштаб Windows.
// Поля прокручиваются, нижняя строка действий остаётся в доступной области.
internal static class DialogSizing
{
    public static Size AvailableSize(Window window)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (handle == 0 || !GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
            return new(Math.Max(280, SystemParameters.WorkArea.Width - 32), Math.Max(280, SystemParameters.WorkArea.Height - 32));
        double dpi = GetDpiForWindow(handle); if (dpi <= 0) dpi = 96;
        return new(Math.Max(280, (info.Work.Right - info.Work.Left) * 96 / dpi - 32), Math.Max(280, (info.Work.Bottom - info.Work.Top) * 96 / dpi - 32));
    }
    public static void Attach(Window window)
    {
        ModalInputGuard.Attach(window);
        bool fitting = false;
        window.SourceInitialized += (_, _) => Fit();
        window.LocationChanged += (_, _) => Fit();
        void Fit()
        {
            if (fitting) return;
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle == 0) return;
            var available = AvailableSize(window); double width = available.Width, height = available.Height;
            fitting = true;
            try
            {
                window.MinWidth = Math.Min(window.MinWidth, width); window.MinHeight = Math.Min(window.MinHeight, height);
                window.MaxWidth = width; window.MaxHeight = height;
                if (window.Width > width) window.Width = width;
                if (window.Height > height) window.Height = height;
            }
            finally { fitting = false; }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
