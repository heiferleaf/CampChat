using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace CampChat;

internal static class DesktopWindow
{
    public static IntPtr Foreground => GetForegroundWindow();

    public static bool IsExternal(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(handle, out var process);
        return process != 0 && process != Environment.ProcessId;
    }

    public static IReadOnlyList<WindowTarget> VisibleWindows()
    {
        var candidates = new List<WindowTarget>();
        var shell = GetShellWindow();
        EnumWindows((window, _) =>
        {
            if (window == shell || !IsWindowVisible(window) || !IsExternal(window)
                || (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0))
                return true;

            var length = GetWindowTextLength(window);
            if (length <= 0)
                return true;
            var title = new StringBuilder(Math.Min(length, 511) + 1);
            if (GetWindowText(window, title, title.Capacity) > 0)
            {
                var thread = GetWindowThreadProcessId(window, out var process);
                if (thread != 0 && process != 0)
                    candidates.Add(new WindowTarget(window, thread, process, title.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return candidates;
    }

    internal static uint WindowThread(IntPtr window, out uint process) => GetWindowThreadProcessId(window, out process);

    public static bool TryFocus(IntPtr handle) =>
        IsExternal(handle) && IsWindow(handle) && !IsIconic(handle) && SetForegroundWindow(handle);

    public static bool AcrylicAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return false;
        const string personalization = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        return Registry.GetValue(personalization, "EnableTransparency", 1) is not 0;
    }

    public static bool TryApplyAcrylic(Window window, HwndSource source)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return false;

        const int cornerAttribute = 33;
        var roundCorner = 2;
        DwmSetWindowAttribute(source.Handle, cornerAttribute, ref roundCorner, sizeof(int));

        if (!AcrylicAvailable() || WindowChrome.GetWindowChrome(window) is not { } chrome
            || source.CompositionTarget is not { } target)
            return false;

        const int darkAttribute = 20;
        var dark = 1;
        DwmSetWindowAttribute(source.Handle, darkAttribute, ref dark, sizeof(int));

        var fallback = target.BackgroundColor;
        chrome.GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness;
        target.BackgroundColor = Colors.Transparent;

        const int backdropAttribute = 38;
        var desktopAcrylic = 3;
        if (DwmSetWindowAttribute(source.Handle, backdropAttribute, ref desktopAcrylic, sizeof(int)) == 0)
            return true;

        chrome.GlassFrameThickness = new Thickness(0);
        target.BackgroundColor = fallback;
        return false;
    }

    public static Rect Viewport(IntPtr target)
    {
        if (IsWindow(target) && !IsIconic(target) && GetClientRect(target, out var client)
            && client.Right >= 480 && client.Bottom >= 300)
        {
            var topLeft = new NativePoint();
            if (ClientToScreen(target, ref topLeft))
            {
                var scale = Scale(target);
                return new Rect(topLeft.X / scale, topLeft.Y / scale,
                    client.Right / scale, client.Bottom / scale);
            }
        }
        return SystemParameters.WorkArea;
    }

    public static Rect ScreenArea(IntPtr target, bool workArea = false)
    {
        if (!TryMonitor(target, out var monitor))
            return SystemParameters.WorkArea;
        var bounds = workArea ? monitor.Work : monitor.Bounds;
        var scale = Scale(target);
        return new Rect(bounds.Left / scale, bounds.Top / scale,
            (bounds.Right - bounds.Left) / scale, (bounds.Bottom - bounds.Top) / scale);
    }

    internal static bool TryMonitor(IntPtr window, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(window, 2);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static double Scale(IntPtr window)
    {
        var dpi = GetDpiForWindow(window);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}

internal sealed record WindowTarget(IntPtr Handle, uint Thread, uint Process, string Title)
{
    public bool IsAvailable => DesktopWindow.WindowThread(Handle, out var process) == Thread && process == Process;

    public override string ToString() => Title;
}
