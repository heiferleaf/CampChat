using System.Runtime.InteropServices;
using System.Windows.Input;

namespace CampChat;

internal enum ShortcutAction { Compose, Room }

internal readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;

    public bool IsValid => Modifiers is > 0 and <= (Alt | Control | Shift)
        && VirtualKey is > 0 and < 0xff and not 0x7b
        && KeyInterop.KeyFromVirtualKey((int)VirtualKey) is not
            (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed);

    public static HotkeyChord Capture(Key key, ModifierKeys modifiers) => new(
        (modifiers.HasFlag(ModifierKeys.Alt) ? Alt : 0)
        | (modifiers.HasFlag(ModifierKeys.Control) ? Control : 0)
        | (modifiers.HasFlag(ModifierKeys.Shift) ? Shift : 0), (uint)KeyInterop.VirtualKeyFromKey(key));

    public IReadOnlyList<string> KeyLabels()
    {
        var keys = new List<string>(4);
        if ((Modifiers & Control) != 0) keys.Add("Ctrl");
        if ((Modifiers & Alt) != 0) keys.Add("Alt");
        if ((Modifiers & Shift) != 0) keys.Add("Shift");
        var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        keys.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Oem3 => "反引号",
            Key.OemMinus or Key.Subtract => "减号",
            Key.OemPlus or Key.Add => "加号",
            _ => key.ToString(),
        });
        return keys;
    }

    public override string ToString() => string.Join(" ", KeyLabels());
}

internal sealed record ShortcutPreset(HotkeyChord Compose, HotkeyChord Room)
{
    public string ComposeLabel => Compose.ToString();
    public string RoomLabel => Room.ToString();
}

internal sealed class GlobalShortcuts(IntPtr handle) : IDisposable
{
    internal const int Message = 0x0312;
    internal const int EscapeId = 0x50ff;
    private const int FirstSlot = 0x5100;
    private const int LastSlot = FirstSlot + 3;
    private const uint NoRepeat = 0x4000;
    private const uint EscapeKey = 0x1b;
    private readonly Dictionary<HotkeyChord, int> _slots = [];
    private bool _escapeHeld;

    public static IReadOnlyList<ShortcutPreset> Presets { get; } =
    [
        new(new(HotkeyChord.Control | HotkeyChord.Shift, 0x77), new(HotkeyChord.Control | HotkeyChord.Shift, 0x78)),
        new(new(HotkeyChord.Control | HotkeyChord.Shift, 0x79), new(HotkeyChord.Control | HotkeyChord.Shift, 0x7a)),
        new(new(HotkeyChord.Control | HotkeyChord.Alt, 0x79), new(HotkeyChord.Control | HotkeyChord.Alt, 0x7a)),
    ];

    public ShortcutPreset? Current { get; private set; }

    public bool TryApply(ShortcutPreset preset)
    {
        if (Current == preset)
            return true;
        if (!preset.Compose.IsValid || !preset.Room.IsValid || preset.Compose == preset.Room)
            return false;

        // Reuse unchanged chords, including when the two actions swap keys.
        var next = new Dictionary<HotkeyChord, int>(_slots);
        var added = new List<HotkeyChord>(2);
        foreach (var chord in new[] { preset.Compose, preset.Room })
        {
            if (next.ContainsKey(chord))
                continue;
            var free = Enumerable.Range(FirstSlot, LastSlot - FirstSlot + 1).First(id => !next.ContainsValue(id));
            if (!RegisterHotKey(handle, free, chord.Modifiers | NoRepeat, chord.VirtualKey))
            {
                foreach (var registered in added)
                    UnregisterHotKey(handle, next[registered]);
                return false;
            }
            added.Add(chord);
            next.Add(chord, free);
        }

        foreach (var (chord, id) in _slots)
            if (chord != preset.Compose && chord != preset.Room)
                UnregisterHotKey(handle, id);
        _slots.Clear();
        _slots.Add(preset.Compose, next[preset.Compose]);
        _slots.Add(preset.Room, next[preset.Room]);
        Current = preset;
        return true;
    }

    public ShortcutAction? Resolve(int id)
    {
        if (Current == null)
            return null;
        if (id == _slots[Current.Compose])
            return ShortcutAction.Compose;
        return id == _slots[Current.Room] ? ShortcutAction.Room : null;
    }

    public bool CaptureEscape()
    {
        if (!_escapeHeld)
            _escapeHeld = RegisterHotKey(handle, EscapeId, NoRepeat, EscapeKey);
        return _escapeHeld;
    }

    public static bool EscapeIsDown => (GetAsyncKeyState((int)EscapeKey) & 0x8000) != 0;

    public void ReleaseEscape()
    {
        if (!_escapeHeld)
            return;
        UnregisterHotKey(handle, EscapeId);
        _escapeHeld = false;
    }

    public void Dispose()
    {
        ReleaseEscape();
        foreach (var id in _slots.Values)
            UnregisterHotKey(handle, id);
        _slots.Clear();
        Current = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
