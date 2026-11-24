using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CampChat;

internal sealed class OverlayText : FrameworkElement
{
    private static readonly Typeface Face = new(OverlayStyle.InterfaceFont,
        FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Brush TextBrush = FrozenBrush(0xff, 0xff, 0xf3, 0xe4);
    private static readonly Brush SpeakerBrush = FrozenBrush(0xff, 0xec, 0xb7, 0x80);
    private static readonly Pen Outline = MakeOutline();
    private const double TextSize = 18;
    private const double Inset = 3;
    private FormattedText? _formatted;
    private Geometry? _outlineGeometry;
    private double _formattedWidth;
    private double _formattedDpi;
    private string? _formattedMessage;
    private string? _formattedSpeaker;

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(OverlayText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpeakerProperty =
        DependencyProperty.Register(nameof(Speaker), typeof(string), typeof(OverlayText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string Speaker
    {
        get => (string)GetValue(SpeakerProperty);
        set => SetValue(SpeakerProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format(availableSize.Width);
        return new Size(Math.Min(availableSize.Width, text.Width + Inset * 2), text.Height + Inset * 2);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var text = Format(ActualWidth);
        var baseline = new Point(Inset, Inset);
        if (_outlineGeometry == null)
        {
            _outlineGeometry = text.BuildGeometry(baseline);
            if (_outlineGeometry.CanFreeze)
                _outlineGeometry.Freeze();
        }
        drawingContext.DrawGeometry(null, Outline, _outlineGeometry);
        drawingContext.DrawText(text, baseline);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private FormattedText Format(double width)
    {
        var maxWidth = Math.Max(1, (double.IsInfinity(width) ? 640 : width) - Inset * 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var speaker = Speaker;
        var message = Message;
        if (_formatted != null && maxWidth == _formattedWidth && dpi == _formattedDpi
            && message == _formattedMessage && speaker == _formattedSpeaker)
            return _formatted;

        var prefix = string.IsNullOrWhiteSpace(speaker) ? "" : speaker + "  ";
        var text = new FormattedText(prefix + message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, TextSize, TextBrush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (prefix.Length > 0)
        {
            text.SetForegroundBrush(SpeakerBrush, 0, prefix.Length);
            text.SetFontWeight(FontWeights.SemiBold, 0, prefix.Length);
        }
        _formatted = text;
        _outlineGeometry = null;
        _formattedWidth = maxWidth;
        _formattedDpi = dpi;
        _formattedMessage = message;
        _formattedSpeaker = speaker;
        return text;
    }

    private static Brush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen MakeOutline()
    {
        var pen = new Pen(FrozenBrush(0xee, 0x0d, 0x0f, 0x12), 2.6)
        {
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
