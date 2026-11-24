using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace CampChat;

internal enum CaptionLayout { Team, Latest }

internal static class OverlayStyle
{
    public static readonly FontFamily InterfaceFont = new("Noto Sans SC, DengXian, Segoe UI");

    public static Brush FindBrush(string key) => (Brush)Application.Current.FindResource(key);

    public static Path Icon(string key, Brush? tint = null) => new()
    {
        Data = (Geometry)Application.Current.FindResource(key),
        Stroke = tint ?? FindBrush("Muted"),
        StrokeThickness = 1.6,
        Stretch = Stretch.Uniform,
        Width = 17,
        Height = 17,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public static Button IconButton(string icon, string name)
    {
        var button = new Button
        {
            Content = Icon(icon), Style = (Style)Application.Current.FindResource("IconButton"), ToolTip = name,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    public static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));

    public static void Appear(UIElement element, bool animate, double offset = 0)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!animate || !SystemParameters.ClientAreaAnimation)
            return;
        element.BeginAnimation(UIElement.OpacityProperty, Ease(0, 1, 150));
        if (offset != 0)
        {
            var translation = new TranslateTransform();
            element.RenderTransform = translation;
            translation.BeginAnimation(TranslateTransform.YProperty, Ease(offset, 0, 165));
        }
    }

    public static DoubleAnimation Ease(double from, double to, int milliseconds) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
}

internal sealed class CaptionWindow : Window
{
    private readonly StackPanel _lines = new() { Background = Brushes.Transparent, IsHitTestVisible = false };
    private readonly DispatcherTimer _expiry = new(DispatcherPriority.Background);
    private readonly List<(OverlayText Text, long Expires)> _messages = [];
    private Rect _area;
    private CaptionLayout _layout;
    private OverlayDock? _dock;
    private bool _compose;
    private bool _visible = true;

    public bool Animate { get; set; } = true;

    public CaptionWindow()
    {
        Title = "营地聊天 · 游戏字幕";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Content = _lines;
        _expiry.Tick += Expire;
        Closed += (_, _) => _expiry.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PassiveOverlay.Enable(this);
    }

    public void MoveTo(Rect area, CaptionLayout layout, bool compose, OverlayDock? dock = null)
    {
        if (area == _area && layout == _layout && compose == _compose && dock == _dock)
            return;
        _area = area;
        _layout = layout;
        _compose = compose;
        _dock = dock;
        Trim();
        Place();
        ScheduleExpiry();
    }

    private void Place()
    {
        var dock = _dock ?? OverlayDock.Default(_layout);
        Width = Math.Max(120, Math.Min(500, _area.Width * 0.48));
        var composerWidth = ComposerWindow.WidthFor(_area);
        var anchor = dock.Place(_area, new Size(composerWidth, ComposerWindow.CompactHeight));
        var rightAligned = dock.X > 0.5;
        _lines.HorizontalAlignment = rightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var targetX = rightAligned ? anchor.X + composerWidth - Width : anchor.X;
        Left = Math.Clamp(targetX, _area.Left, Math.Max(_area.Left, _area.Right - Width));

        var rows = Math.Max(1, _lines.Children.Count) * 28;
        var preferred = _compose ? anchor.Y - rows - 8 : anchor.Y + ComposerWindow.CompactHeight - rows;
        if (_compose && preferred < _area.Top)
            preferred = anchor.Y + ComposerWindow.CompactHeight + 9;
        Top = Math.Clamp(preferred, _area.Top, Math.Max(_area.Top, _area.Bottom - rows));
    }

    public void Add(string speaker, string message)
    {
        var text = new OverlayText { Speaker = speaker, Message = message, Margin = new Thickness(0, 0, 0, 1) };
        _lines.Children.Add(text);
        _messages.Add((text, Environment.TickCount64 + 7000));
        Trim();
        Place();
        if (_visible && !IsVisible)
            Show();
        OverlayStyle.Appear(text, Animate);
        ScheduleExpiry();
    }

    public void Clear()
    {
        _expiry.Stop();
        _lines.Children.Clear();
        _messages.Clear();
        if (IsVisible)
            Hide();
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible && _lines.Children.Count > 0 && !IsVisible)
            Show();
        else if (!visible && IsVisible)
            Hide();
    }

    private void Trim()
    {
        var max = _layout == CaptionLayout.Team ? 3 : 1;
        while (_lines.Children.Count > max)
        {
            var outgoing = _lines.Children[0];
            _lines.Children.RemoveAt(0);
            _messages.RemoveAll(item => ReferenceEquals(item.Text, outgoing));
        }
    }

    private void Expire(object? sender, EventArgs args)
    {
        var now = Environment.TickCount64;
        while (_messages.Count > 0 && _messages[0].Expires <= now)
        {
            var message = _messages[0];
            _messages.RemoveAt(0);
            if (Animate && SystemParameters.ClientAreaAnimation)
            {
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170));
                fade.Completed += (_, _) => RemoveLine(message.Text);
                message.Text.BeginAnimation(OpacityProperty, fade);
            }
            else
                RemoveLine(message.Text);
        }
        ScheduleExpiry();
    }

    private void ScheduleExpiry()
    {
        _expiry.Stop();
        if (_messages.Count == 0)
            return;
        var remaining = Math.Max(1, _messages[0].Expires - Environment.TickCount64);
        _expiry.Interval = TimeSpan.FromMilliseconds(remaining);
        _expiry.Start();
    }

    private void RemoveLine(OverlayText text)
    {
        _lines.Children.Remove(text);
        if (_lines.Children.Count == 0)
            Hide();
        else
            Place();
    }
}

internal sealed class ComposerWindow : Window
{
    public const double CompactHeight = 45;
    private readonly TextBox _draft = new()
    {
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 400, FontSize = 15,
        Foreground = OverlayStyle.FindBrush("Paper"), CaretBrush = OverlayStyle.FindBrush("Amber"),
        FocusVisualStyle = null,
    };
    private readonly Border _frame;
    private readonly Button _send;
    private readonly Path _mark = OverlayStyle.Icon("DragHandleIcon");
    private readonly TextBlock _error = new()
    {
        Foreground = OverlayStyle.Solid(0xff, 0xff, 0xc0, 0xad), FontSize = 11,
        Margin = new Thickness(10, 1, 0, 3), Visibility = Visibility.Collapsed,
    };
    private bool _sending;

    public Func<string, Task<bool>>? SendRequested { get; set; }
    public event Action? Sent;
    public event Action? Dismissed;
    public event Action<Rect>? Dragged;
    public bool Animate { get; set; } = true;
    private bool _dragging;

    public ComposerWindow()
    {
        Title = "营地聊天 · 打字";
        Width = 400;
        Height = CompactHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = OverlayStyle.InterfaceFont;

        _send = OverlayStyle.IconButton("SendIcon", "发送（Enter）");
        _send.Click += (_, _) => Send();
        var messageArea = new Grid { Margin = new Thickness(10, 0, 8, 0) };
        var placeholder = new TextBlock
        {
            Text = "发给小队…", Foreground = OverlayStyle.FindBrush("Muted"),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
        };
        messageArea.Children.Add(placeholder);
        messageArea.Children.Add(_draft);
        _draft.TextChanged += (_, _) =>
            placeholder.Visibility = _draft.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_draft, "输入小队消息");
        var layout = new DockPanel { LastChildFill = true };
        _mark.Margin = new Thickness(12, 0, 0, 0);
        _mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_send, Dock.Right);
        var grip = new Border
        {
            Width = 32, Background = Brushes.Transparent, Child = _mark, Cursor = Cursors.SizeAll,
            ToolTip = "拖动输入条。靠近屏幕左右边缘时吸附。",
        };
        AutomationProperties.SetName(grip, "拖动输入条");
        grip.MouseLeftButtonDown += DragGrip;
        DockPanel.SetDock(grip, Dock.Left);
        layout.Children.Add(grip);
        layout.Children.Add(_send);
        layout.Children.Add(messageArea);
        layout.Height = 43;
        var content = new StackPanel();
        content.Children.Add(_error);
        content.Children.Add(layout);
        _frame = new Border
        {
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
            Background = OverlayStyle.Solid(0xba, 0x34, 0x2b, 0x23), Child = content,
        };
        Content = _frame;
    }

    public static double WidthFor(Rect area) => Math.Max(190, Math.Min(400, area.Width * 0.48));

    public void MoveTo(Rect area, CaptionLayout layout, OverlayDock? dock = null)
    {
        if (_dragging)
            return;
        Width = WidthFor(area);
        var anchor = (dock ?? OverlayDock.Default(layout)).Place(area, new Size(Width, Height));
        Left = anchor.X;
        Top = anchor.Y;
        var accent = OverlayStyle.FindBrush(layout == CaptionLayout.Team ? "Amber" : "SoftAccent");
        _frame.BorderBrush = accent;
        _mark.Stroke = accent;
        _draft.CaretBrush = accent;
        ((Path)_send.Content).Stroke = accent;
    }

    private void DragGrip(object sender, MouseButtonEventArgs args)
    {
        if (args.ButtonState != MouseButtonState.Pressed)
            return;
        args.Handled = true;
        _dragging = true;
        try
        {
            DragMove();
        }
        finally
        {
            _dragging = false;
        }
        if (IsVisible)
        {
            Dragged?.Invoke(new Rect(Left, Top, Width, Height));
            _draft.Focus();
        }
    }

    public void Reveal()
    {
        Show();
        Activate();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Keyboard.Focus(_draft);
            _draft.CaretIndex = _draft.Text.Length;
        }));
        OverlayStyle.Appear(_frame, Animate, 5);
    }

    public void ResetDraft()
    {
        _draft.Clear();
        SetError(null);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismissed?.Invoke();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Send();
        }
        base.OnPreviewKeyDown(e);
    }

    private async void Send()
    {
        if (_sending || string.IsNullOrWhiteSpace(_draft.Text) || SendRequested == null)
            return;
        var message = _draft.Text.Trim();
        SetError(null);
        _sending = true;
        _send.IsEnabled = false;
        try
        {
            if (await SendRequested(message))
            {
                _draft.Clear();
                Sent?.Invoke();
            }
            else
                SetError("发送失败，草稿已保留。");
        }
        finally
        {
            _sending = false;
            _send.IsEnabled = true;
        }
    }

    private void SetError(string? text)
    {
        var targetHeight = text == null ? CompactHeight : 64;
        Top += Height - targetHeight;
        Height = targetHeight;
        _error.Text = text ?? "";
        _error.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
