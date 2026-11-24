using System.Runtime.InteropServices;

namespace CampChat;

internal sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8104;
    private readonly IntPtr _owner;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Action[] _actions;
    private readonly bool _ownsIcon;
    private IconData _data;

    public TrayIcon(IntPtr owner, Action openRoom, Action openSettings, Action close)
    {
        _owner = owner;
        _actions = [openRoom, openSettings, close];
        var exe = Environment.ProcessPath ?? "";
        var count = ExtractIconEx(exe, 0, out var large, out var small, 1);
        if (large != IntPtr.Zero)
            DestroyIcon(large);
        _ownsIcon = count > 0 && small != IntPtr.Zero;
        _data = new IconData
        {
            Size = Marshal.SizeOf<IconData>(), Window = owner, Id = 1, Flags = 7,
            Callback = Callback, Icon = _ownsIcon ? small : LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            Tip = "营地聊天", Info = "", Title = "",
        };
        Notify(0, ref _data);
    }

    public bool Route(int message, IntPtr lParam)
    {
        if (message != 0 && (uint)message == _taskbarCreated)
        {
            Notify(0, ref _data);
            return true;
        }
        if (message != Callback)
            return false;

        if (lParam.ToInt32() == 0x0202)
            _actions[0]();
        else if (lParam.ToInt32() == 0x0205)
            Menu();
        return true;
    }

    private void Menu()
    {
        if (!GetCursorPos(out var position))
            return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;
        try
        {
            AppendMenu(menu, 0, 1, "打开房间");
            AppendMenu(menu, 0, 2, "打开设置");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 3, "退出");
            SetForegroundWindow(_owner);
            var action = TrackPopupMenuEx(menu, 0x0102, position.X, position.Y, _owner, IntPtr.Zero);
            PostMessage(_owner, 0, IntPtr.Zero, IntPtr.Zero);
            if (action is >= 1 and <= 3)
                _actions[action - 1]();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Notify(2, ref _data);
        if (_ownsIcon)
            DestroyIcon(_data.Icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Notify(uint message, ref IconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr identifier);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? name);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
