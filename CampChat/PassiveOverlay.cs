using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CampChat;

internal static class PassiveOverlay
{
    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20;
    private const long ToolWindow = 0x80;
    private const long NoActivate = 0x08000000;

    public static void Enable(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !window.AllowsTransparency)
            throw new InvalidOperationException("请在透明窗口创建后启用鼠标穿透。");

        Marshal.SetLastPInvokeError(0);
        var previous = GetWindowLongPtr(handle, ExtendedStyle).ToInt64();
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error);

        Marshal.SetLastPInvokeError(0);
        var result = SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(previous | Transparent | ToolWindow | NoActivate));
        error = Marshal.GetLastPInvokeError();
        if (result == IntPtr.Zero && error != 0)
            throw new Win32Exception(error);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
