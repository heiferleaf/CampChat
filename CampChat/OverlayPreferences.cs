using System.IO;
using System.Text.Json;

namespace CampChat;

internal sealed record OverlayPreferences(ShortcutPreset Shortcuts, CaptionLayout Layout, bool Animate,
    OverlayDock? TeamDock = null, OverlayDock? LatestDock = null, OverlayDock? SettingsDock = null,
    ShellSize? WindowSize = null)
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "CampChat.settings.json");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
    };

    public OverlayDock? ActiveDock => Layout == CaptionLayout.Team ? TeamDock : LatestDock;

    public OverlayPreferences MoveActiveDock(OverlayDock dock) =>
        Layout == CaptionLayout.Team ? this with { TeamDock = dock } : this with { LatestDock = dock };

    public static OverlayPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<OverlayPreferences>(File.ReadAllText(FilePath), Json) is { } saved
                && saved.Shortcuts != null && saved.Shortcuts.Compose.IsValid && saved.Shortcuts.Room.IsValid
                && saved.Shortcuts.Compose != saved.Shortcuts.Room && Enum.IsDefined(saved.Layout))
                return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // An incomplete settings file must not prevent the tray app from starting.
        }
        return new OverlayPreferences(GlobalShortcuts.Presets[0], CaptionLayout.Team, true);
    }

    public bool Save()
    {
        try
        {
            var next = FilePath + ".tmp";
            File.WriteAllText(next, JsonSerializer.Serialize(this, Json));
            File.Move(next, FilePath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed record ShellSize(double Width, double Height)
{
    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}
