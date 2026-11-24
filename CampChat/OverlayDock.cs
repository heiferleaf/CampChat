using System.Windows;

namespace CampChat;

internal sealed record OverlayDock(double X, double Y)
{
    public const double Margin = 20;
    private const double SnapDistance = 38;

    public static OverlayDock Default(CaptionLayout layout) =>
        new(layout == CaptionLayout.Team ? 0 : 1, 1);

    public Point Place(Rect area, Size window)
    {
        var span = Travel(area, window);
        return new Point(area.Left + Margin + Math.Clamp(X, 0, 1) * span.Width,
            area.Top + Margin + Math.Clamp(Y, 0, 1) * span.Height);
    }

    public static OverlayDock FromDrag(Rect area, Rect window)
    {
        var span = Travel(area, window.Size);
        var leftGap = window.Left - area.Left;
        var rightGap = area.Right - window.Right;
        var x = leftGap <= SnapDistance ? 0 : rightGap <= SnapDistance ? 1
            : Relative(leftGap - Margin, span.Width);
        var y = Relative(window.Top - area.Top - Margin, span.Height);
        return new OverlayDock(x, y);
    }

    private static Size Travel(Rect area, Size window) =>
        new(Math.Max(0, area.Width - window.Width - Margin * 2),
            Math.Max(0, area.Height - window.Height - Margin * 2));

    private static double Relative(double distance, double span) =>
        span <= 0 ? 0 : Math.Clamp(distance / span, 0, 1);
}
