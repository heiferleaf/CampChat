using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CampChat.Preview;

public partial class PreviewWindow : Window
{
    private CaptionWindow? _captions;
    private ComposerWindow? _composer;
    private Window? _settings;
    private GlobalShortcuts? _hotkeys;
    private HwndSource? _source;
    private CaptionLayout _layout;
    private OverlayDock? _teamDock;
    private OverlayDock? _latestDock;
    private bool _closing;
    private bool _animate = true;
    private int _clicks;
    private int _previewMessages;

    public PreviewWindow() => InitializeComponent();

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this);
        _hotkeys = new GlobalShortcuts(_source.Handle);
        _source.AddHook(WindowMessage);
        foreach (var preset in GlobalShortcuts.Presets)
            if (_hotkeys.TryApply(preset))
                break;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _captions = new CaptionWindow();
        _composer = new ComposerWindow();
        _composer.SendRequested = message =>
        {
            _captions.Add("我", message);
            return Task.FromResult(true);
        };
        _composer.Sent += () => CloseComposer(restoreFocus: true);
        _composer.Dismissed += () => CloseComposer(restoreFocus: true);
        _composer.Activated += (_, _) => _hotkeys?.CaptureEscape();
        _composer.Deactivated += (_, _) => CloseComposer(restoreFocus: false);
        _composer.Dragged += bounds =>
        {
            var dock = OverlayDock.FromDrag(Viewport(), bounds);
            if (_layout == CaptionLayout.Team)
                _teamDock = dock;
            else
                _latestDock = dock;
            AlignSurfaces();
        };
        var configured = (_hotkeys?.Current ?? GlobalShortcuts.Presets[0]);
        Hint.Text = $"按 {configured.ComposeLabel} 打字，按 {configured.RoomLabel} 打开设置。点击字幕位置可测试鼠标穿透。";
        AlignSurfaces();
    }

    private IntPtr WindowMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == GlobalShortcuts.Message && wParam.ToInt32() == GlobalShortcuts.EscapeId
            && _composer?.IsActive == true)
        {
            handled = true;
            _ = CloseAfterEscapeAsync();
            return IntPtr.Zero;
        }
        if (message == GlobalShortcuts.Message && _hotkeys?.Resolve(wParam.ToInt32()) is { } action)
        {
            handled = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (action == ShortcutAction.Compose)
                    ToggleComposer();
                else
                    ToggleSettings();
            }, DispatcherPriority.Input);
        }
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (_hotkeys?.Current != null || Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift))
            return;
        if (args.Key == Key.F8)
        {
            args.Handled = true;
            ToggleComposer();
        }
        else if (args.Key == Key.F9)
        {
            args.Handled = true;
            ToggleSettings();
        }
    }

    private void GameClick(object sender, MouseButtonEventArgs e)
    {
        Scene.Focus();
        GameStatus.Text = $"游戏收到 {++_clicks} 次点击";
    }

    private async void PlayMessages(object sender, RoutedEventArgs e)
    {
        if (_captions == null)
            return;
        var version = ++_previewMessages;
        var sample = new[] { ("阿岚", "门口有补给。"), ("老魏", "我拿护盾，右侧集合。"), ("阿岚", "等我两秒。") };
        Activate();
        Scene.Focus();
        foreach (var (name, text) in sample)
        {
            if (version != _previewMessages || _closing || WindowState == WindowState.Minimized)
                return;
            _captions.Add(name, text);
            await Task.Delay(210);
        }
    }

    private Rect Viewport()
    {
        var coordinates = PresentationSource.FromVisual(Scene)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = coordinates.Transform(Scene.PointToScreen(new Point(0, 0)));
        var bottomRight = coordinates.Transform(Scene.PointToScreen(new Point(Scene.ActualWidth, Scene.ActualHeight)));
        return new Rect(topLeft, bottomRight);
    }

    private void AlignSurfaces()
    {
        if (_closing || _composer == null || _captions == null || !Scene.IsLoaded
            || WindowState == WindowState.Minimized)
            return;
        var area = Viewport();
        var dock = _layout == CaptionLayout.Team ? _teamDock : _latestDock;
        _captions.MoveTo(area, _layout, _composer.IsVisible, dock);
        _composer.MoveTo(area, _layout, dock);
        if (_settings is { IsVisible: true })
        {
            _settings.Left = area.Left + (area.Width - _settings.Width) / 2;
            _settings.Top = area.Top + (area.Height - _settings.Height) / 2;
        }
    }

    private void OnWindowMoved(object? sender, EventArgs args) => AlignSurfaces();
    private void OnWindowResized(object sender, SizeChangedEventArgs args) => AlignSurfaces();

    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (WindowState == WindowState.Minimized)
        {
            _captions?.Clear();
            CloseComposer(restoreFocus: false);
            _settings?.Hide();
        }
        else
            AlignSurfaces();
    }

    private void OpenComposerClicked(object sender, RoutedEventArgs args) => ToggleComposer();
    private void OpenSettingsClicked(object sender, RoutedEventArgs args) => ToggleSettings();

    private void ToggleComposer()
    {
        if (_closing || _composer == null)
            return;
        if (_composer.IsVisible && _composer.IsActive)
        {
            CloseComposer(restoreFocus: true);
            return;
        }
        _settings?.Hide();
        RestorePreview();
        AlignSurfaces();
        _composer.Reveal();
        AlignSurfaces();
    }

    private async Task CloseAfterEscapeAsync()
    {
        while (_composer?.IsActive == true && GlobalShortcuts.EscapeIsDown)
            await Task.Delay(12);
        if (_composer?.IsActive == true)
            CloseComposer(restoreFocus: true);
    }

    private void CloseComposer(bool restoreFocus)
    {
        _hotkeys?.ReleaseEscape();
        _composer?.Hide();
        AlignSurfaces();
        if (restoreFocus)
            FocusGame();
    }

    private void ToggleSettings()
    {
        if (_closing)
            return;
        if (_settings is { IsVisible: true, IsActive: true })
        {
            CloseSettings();
            return;
        }
        CloseComposer(restoreFocus: false);
        RestorePreview();
        _settings ??= BuildSettings();
        var area = Viewport();
        _settings.Left = area.Left + (area.Width - _settings.Width) / 2;
        _settings.Top = area.Top + (area.Height - _settings.Height) / 2;
        _settings.Show();
        _settings.Activate();
        OverlayStyle.Appear((UIElement)_settings.Content, _animate, 5);
        AlignSurfaces();
    }

    private void CloseSettings()
    {
        _settings?.Hide();
        FocusGame();
    }

    private Window BuildSettings()
    {
        var window = new Window
        {
            Title = "营地聊天 · 房间设置", Width = 390, Height = 263, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowInTaskbar = false, Foreground = OverlayStyle.FindBrush("Paper"),
            FontFamily = OverlayStyle.InterfaceFont, FontSize = 14,
        };
        var body = new StackPanel { Margin = new Thickness(17, 11, 17, 14) };
        var header = new DockPanel();
        var close = OverlayStyle.IconButton("CloseIcon", "关闭设置（Esc）");
        close.Click += (_, _) => CloseSettings();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var figure = OverlayStyle.Icon("SettingsIcon", OverlayStyle.FindBrush("Amber"));
        figure.Margin = new Thickness(0, 0, 9, 0);
        DockPanel.SetDock(figure, Dock.Left);
        header.Children.Add(figure);
        header.Children.Add(new TextBlock
        {
            Text = "房间设置", VerticalAlignment = VerticalAlignment.Center, FontSize = 19,
            FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = "在游戏中显示", Margin = new Thickness(0, 12, 0, 6), Foreground = OverlayStyle.FindBrush("Muted"),
        });
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        var left = new Button { Content = "左下角显示最近三条", Margin = new Thickness(0, 0, 7, 0) };
        var right = new Button { Content = "右侧显示最新一条" };
        choices.Children.Add(left);
        choices.Children.Add(right);
        body.Children.Add(choices);
        void Select(CaptionLayout selected)
        {
            _layout = selected;
            left.BorderBrush = OverlayStyle.FindBrush(selected == CaptionLayout.Team ? "Amber" : "Line");
            right.BorderBrush = OverlayStyle.FindBrush(selected == CaptionLayout.Latest ? "SoftAccent" : "Line");
            AlignSurfaces();
        }
        left.Click += (_, _) => Select(CaptionLayout.Team);
        right.Click += (_, _) => Select(CaptionLayout.Latest);
        Select(_layout);
        var keys = _hotkeys?.Current ?? GlobalShortcuts.Presets[0];
        body.Children.Add(new TextBlock
        {
            Text = "打字聊天   " + keys.ComposeLabel, Margin = new Thickness(0, 13, 0, 3),
        });
        body.Children.Add(new TextBlock { Text = "房间设置   " + keys.RoomLabel });
        var motion = new CheckBox
        {
            Content = "界面动效", IsChecked = _animate, Style = (Style)FindResource("PreferenceSwitch"),
            Margin = new Thickness(0, 8, 0, 0), Width = 171, HorizontalAlignment = HorizontalAlignment.Left,
        };
        motion.Checked += (_, _) => SetMotion(true);
        motion.Unchecked += (_, _) => SetMotion(false);
        body.Children.Add(motion);
        window.Content = new Border
        {
            CornerRadius = new CornerRadius(11), Background = OverlayStyle.Solid(0xf5, 0x35, 0x2d, 0x26),
            BorderThickness = new Thickness(1), BorderBrush = OverlayStyle.FindBrush("Line"), Child = body,
        };
        window.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                args.Handled = true;
                CloseSettings();
            }
        };
        return window;
    }

    private void SetMotion(bool enabled)
    {
        _animate = enabled;
        if (_captions != null)
            _captions.Animate = enabled;
        if (_composer != null)
            _composer.Animate = enabled;
    }

    private void RestorePreview()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
    }

    private void FocusGame()
    {
        if (_closing)
            return;
        Activate();
        Scene.Focus();
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _closing = true;
        _hotkeys?.Dispose();
        _source?.RemoveHook(WindowMessage);
        _captions?.Close();
        _composer?.Close();
        _settings?.Close();
    }
}
