using System.Runtime.InteropServices;

namespace CampChat;

// Changes only the Win32 frame and bounds of an already windowed game.
internal sealed class BorderlessGameWindow
{
    private const int WindowStyle = -16;
    private const long Frame = 0x00C40000;
    private const uint Maximized = 3;
    private const uint Minimized = 2;
    private const uint NormalNoActivate = 4;
    private const uint FrameChangedNoActivate = 0x0004 | 0x0010 | 0x0020 | 0x4000;
    private OriginalWindow? _original;

    public IntPtr Target => _original is { } state
        && DesktopWindow.WindowThread(state.Handle, out var process) == state.Thread && process == state.Process
            ? state.Handle : IntPtr.Zero;

    public bool Applied => Target != IntPtr.Zero;

    public string Apply(IntPtr window)
    {
        if (Applied)
            return "请先还原当前窗口。";
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        var thread = DesktopWindow.WindowThread(window, out var process);
        if (thread == 0 || !GetWindowPlacement(window, ref placement) || !GetWindowRect(window, out var bounds)
            || !DesktopWindow.TryMonitor(window, out var monitor))
            return "窗口已关闭或暂时无法调整，请刷新列表。";
        if (placement.ShowCommand == Minimized)
            return "请先从任务栏恢复所选窗口。";

        var style = GetWindowLongPtr(window, WindowStyle);
        if ((style.ToInt64() & Frame) == 0)
            return "所选窗口没有可移除的边框。若游戏使用独占全屏，请先切到窗口模式。";

        // Probe permission with the style change before altering the original placement.
        if (!SetStyle(window, new IntPtr(style.ToInt64() & ~Frame)))
            return "当前无法调整所选窗口。请检查游戏与营地聊天的运行权限。";

        _original = new OriginalWindow(window, thread, process, style, bounds, placement);
        if (placement.ShowCommand == Maximized)
        {
            placement.ShowCommand = NormalNoActivate;
            if (!SetWindowPlacement(window, ref placement))
                return Restore() ? "无法退出原来的最大化状态。" : "无法完成调整，请尝试还原窗口。";
        }
        if (!SetBounds(window, monitor.Bounds))
            return Restore() ? "无法把所选窗口铺满屏幕。" : "无法完成调整，请尝试还原窗口。";

        return "所选窗口已切换为无边框全屏。";
    }

    public bool Restore()
    {
        var window = Target;
        if (_original is not { } state || window == IntPtr.Zero)
        {
            _original = null;
            return true;
        }
        if (!SetStyle(window, state.Style))
            return false;

        var placement = state.Placement;
        var restored = placement.ShowCommand == Maximized
            ? SetBounds(window, state.Bounds) && SetWindowPlacement(window, ref placement)
            : SetBounds(window, state.Bounds);
        if (!restored)
            return false;
        _original = null;
        return true;
    }

    private sealed record OriginalWindow(IntPtr Handle, uint Thread, uint Process, IntPtr Style,
        DesktopWindow.NativeRect Bounds, WindowPlacement Placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public uint Flags, ShowCommand;
        public int MinX, MinY, MaxX, MaxY;
        public DesktopWindow.NativeRect NormalPosition;
    }

    private static bool SetBounds(IntPtr window, DesktopWindow.NativeRect bounds) =>
        SetWindowPos(window, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top, FrameChangedNoActivate);

    private static bool SetStyle(IntPtr window, IntPtr style)
    {
        var old = SetWindowLongPtr(window, WindowStyle, style);
        return old != IntPtr.Zero || Marshal.GetLastPInvokeError() == 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out DesktopWindow.NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);
}
