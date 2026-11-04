using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PodcastHelper.App;

internal sealed class FocusAttention : IDisposable
{
    private readonly Application application;
    private readonly Dispatcher dispatcher;
    private readonly Func<bool> appForeground;
    private readonly Action<bool> attention;
    private bool disposed, flashing;

    public FocusAttention(Application application, Window window) : this(application, window.Dispatcher, OwnProcessForeground, enabled => Flash(window, enabled)) { }

    internal FocusAttention(Application application, Dispatcher dispatcher, Func<bool> appForeground, Action<bool> attention)
    {
        this.application = application; this.dispatcher = dispatcher; this.appForeground = appForeground; this.attention = attention;
        application.Activated += Activated; application.Deactivated += Deactivated;
    }

    private void Activated(object? sender, EventArgs e) => SetFlashing(false);
    private void Deactivated(object? sender, EventArgs e)
    {
        // Смена собственного окна на модальное не означает уход из приложения.
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (disposed) return;
            SetFlashing(!appForeground());
        }));
    }

    private void SetFlashing(bool enabled)
    {
        if (enabled == flashing) return;
        flashing = enabled;
        attention(enabled);
    }

    private static bool OwnProcessForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static void Flash(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle,
            Flags = enabled ? 0x00000002u | 0x0000000Cu : 0, Count = enabled ? uint.MaxValue : 0 };
        // Возвращаемое значение — предыдущее состояние заголовка, а не признак успеха.
        _ = FlashWindowEx(ref info);
    }

    public void Dispose()
    {
        disposed = true; application.Activated -= Activated; application.Deactivated -= Deactivated;
        SetFlashing(false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public nint Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
