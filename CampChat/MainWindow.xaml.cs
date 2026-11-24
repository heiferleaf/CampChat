using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CampChat.Network;

namespace CampChat;

public partial class MainWindow : Window
{
    private enum Page { Welcome, Create, Join, Room, Preferences }
    private const string WritingHint = "按 Enter 发送，按 Esc 收起。";
    private const int PageExitMilliseconds = 245;
    private const int PageEnterMilliseconds = 385;
    private const int WelcomeHeight = 260;
    private const int WelcomeStatusHeight = 284;
    private readonly ObservableCollection<ChatLine> _history = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BorderlessGameWindow _borderless = new();
    private readonly FrameworkElement[] _pageViews;
    private OverlayPreferences _preferences = OverlayPreferences.Load();
    private IRoomSession? _room;
    private Task? _connecting;
    private HwndSource? _source;
    private GlobalShortcuts? _hotkeys;
    private CaptionWindow? _captions;
    private ComposerWindow? _quickComposer;
    private TrayIcon? _tray;
    private Page _page;
    private Page _roomPage;
    private int _pageTransition;
    private double _pageHeight = WelcomeHeight;
    private Size? _resizeStart;
    private ShortcutAction? _capture;
    private IntPtr _returnWindow;
    private long _sessionId;
    private string? _invite;
    private string _role = "";
    private int _people;
    private bool _ready;
    private bool _refreshingAdapters;
    private bool _sending;
    private bool _leaving;
    private bool _closing;
    private bool _closeComplete;

    public MainWindow()
    {
        InitializeComponent();
        _pageViews = [WelcomePanel, CreateFields, JoinFields, ChatPanel, PreferencesPanel];
        ChatHistory.ItemsSource = _history;
        Nickname.Text = "玩家";
        Activated += (_, _) => AlignOverlay();
        Deactivated += (_, _) =>
        {
            if (IsVisible && _room != null && !_closing)
                DismissSettings(restoreFocus: false);
        };
        var area = SystemParameters.WorkArea;
        if (_preferences.WindowSize is { IsValid: true } size)
        {
            Width = Math.Clamp(size.Width, MinWidth, Math.Max(MinWidth, area.Width));
            Height = Math.Clamp(size.Height, MinHeight, Math.Max(MinHeight, area.Height));
            _preferences = _preferences with { WindowSize = new ShellSize(Width, Height) };
        }
        else
            _preferences = _preferences with { WindowSize = null };
        Left = Math.Max(area.Left, area.Right - Width - 24);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
            return;
        if (_source?.CompositionTarget is { } target && Background is SolidColorBrush surface)
        {
            if (DesktopWindow.TryApplyAcrylic(this, _source))
                Background = (Brush)FindResource("AcrylicSurface");
            else
                target.BackgroundColor = surface.Color;
        }
        LayoutPicker.SelectedIndex = (int)_preferences.Layout;
        MotionEnabled.IsChecked = _preferences.Animate;
        InitializeTray();
        _ready = true;
        ShowPage(Page.Welcome, _hotkeys?.Current == null ? "默认快捷键被占用。可以从托盘打开设置，重新绑定。" : "");
        UpdateShortcutLabels();
        AlignOverlay();
    }

    private void PrepareOverlay()
    {
        if (_captions != null)
            return;
        _captions = new CaptionWindow { Animate = _preferences.Animate };
        _quickComposer = new ComposerWindow
        {
            Animate = _preferences.Animate,
            SendRequested = SendRoomTextAsync,
        };
        _quickComposer.Sent += () => DismissComposer(restoreFocus: true);
        _quickComposer.Dismissed += () => DismissComposer(restoreFocus: true);
        _quickComposer.Deactivated += (_, _) => DismissComposer(restoreFocus: false);
        _quickComposer.Activated += (_, _) => _hotkeys?.CaptureEscape();
        _quickComposer.Dragged += DockOverlay;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this);
        _source.AddHook(WindowMessage);
        _hotkeys = new GlobalShortcuts(_source.Handle);
        if (!_hotkeys.TryApply(_preferences.Shortcuts))
            foreach (var preset in GlobalShortcuts.Presets)
                if (_hotkeys.TryApply(preset))
                    break;
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0231)
            _resizeStart = new Size(ActualWidth, ActualHeight);
        else if (message == 0x0232 && _resizeStart is { } before)
        {
            _resizeStart = null;
            _ = Dispatcher.InvokeAsync(() => RememberWindowSize(before), DispatcherPriority.Loaded);
        }
        if (_tray?.Route(message, lParam) == true)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (message == GlobalShortcuts.Message && wParam.ToInt32() == GlobalShortcuts.EscapeId
            && _quickComposer?.IsActive == true)
        {
            handled = true;
            _ = CloseAfterEscapeAsync();
            return IntPtr.Zero;
        }
        if (message == GlobalShortcuts.Message && _hotkeys?.Resolve(wParam.ToInt32()) is { } action)
        {
            handled = true;
            if (_capture != null && IsActive && _hotkeys.Current is { } current)
            {
                var chord = action == ShortcutAction.Compose ? current.Compose : current.Room;
                CompleteCapture(chord);
                return IntPtr.Zero;
            }
            RememberForeground();
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (action == ShortcutAction.Compose)
                    OpenComposer();
                else if (IsVisible && IsActive && _page == Page.Preferences)
                    DismissSettings(restoreFocus: true);
                else
                    OpenSettings();
            }, DispatcherPriority.Input);
        }
        return IntPtr.Zero;
    }

    private void RememberWindowSize(Size before)
    {
        if (_closing || Math.Abs(ActualWidth - before.Width) < 1 && Math.Abs(ActualHeight - before.Height) < 1)
            return;
        _pageHeight = ActualHeight;
        _preferences = _preferences with { WindowSize = new ShellSize(ActualWidth, ActualHeight) };
        SavePreferences();
    }

    private void InitializeTray()
    {
        _tray = new TrayIcon(_source!.Handle, () => Dispatcher.InvokeAsync(() => OpenRoom()),
            () => Dispatcher.InvokeAsync(OpenSettings), () => Dispatcher.InvokeAsync(Close));
    }

    private void RememberForeground()
    {
        var foreground = DesktopWindow.Foreground;
        if (DesktopWindow.IsExternal(foreground))
            _returnWindow = foreground;
    }

    private void AlignOverlay()
    {
        if (!_ready || _captions == null || _quickComposer == null || _closing)
            return;
        var dock = _preferences.ActiveDock;
        var area = dock == null ? DesktopWindow.Viewport(_returnWindow) : DesktopWindow.ScreenArea(ScreenReference());
        _quickComposer.MoveTo(area, _preferences.Layout, dock);
        _captions.MoveTo(area, _preferences.Layout, _quickComposer.IsVisible, dock);
        _captions.SetVisible(!IsVisible || !IsActive);
    }

    private IntPtr ScreenReference() => _returnWindow == IntPtr.Zero ? _source?.Handle ?? IntPtr.Zero : _returnWindow;

    private void DockOverlay(Rect bounds)
    {
        var dock = OverlayDock.FromDrag(DesktopWindow.ScreenArea(ScreenReference()), bounds);
        _preferences = _preferences.MoveActiveDock(dock);
        AlignOverlay();
        SavePreferences();
    }

    private void OpenComposer()
    {
        if (_closing || !_ready)
            return;
        if (_room == null)
        {
            OpenRoom("请先建房或加入房间。");
            return;
        }
        PrepareOverlay();
        if (_quickComposer == null)
            return;
        if (_quickComposer.IsVisible && _quickComposer.IsActive)
        {
            DismissComposer(restoreFocus: true);
            return;
        }
        RememberForeground();
        DismissSettings(restoreFocus: false);
        AlignOverlay();
        _quickComposer.Reveal();
        AlignOverlay();
    }

    private void DismissComposer(bool restoreFocus)
    {
        _hotkeys?.ReleaseEscape();
        if (_quickComposer?.IsVisible != true)
            return;
        if (restoreFocus && _quickComposer.IsActive)
            DesktopWindow.TryFocus(_returnWindow);
        _quickComposer.Hide();
        AlignOverlay();
    }

    private async Task CloseAfterEscapeAsync()
    {
        while (_quickComposer?.IsActive == true && GlobalShortcuts.EscapeIsDown)
            await Task.Delay(12);
        if (_quickComposer?.IsActive == true)
            DismissComposer(restoreFocus: true);
    }

    private void OpenSettings()
    {
        if (_closing)
            return;
        RememberForeground();
        RefreshGameWindows();
        if (_page != Page.Preferences)
        {
            _roomPage = _page;
            ShowPage(Page.Preferences);
        }
        OpenShell();
    }

    private void OpenRoom(string status = "")
    {
        if (_closing)
            return;
        if (_page == Page.Preferences)
            ShowPage(_room != null ? Page.Room : _roomPage == Page.Room ? Page.Welcome : _roomPage, status);
        else if (status.Length > 0)
            ShowPage(_page, status);
        OpenShell();
    }

    private void OpenShell()
    {
        if (_closing)
            return;
        RememberForeground();
        DismissComposer(restoreFocus: false);
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (!IsVisible)
        {
            if (_preferences.SettingsDock is { } dock)
            {
                var anchor = dock.Place(DesktopWindow.ScreenArea(ScreenReference(), workArea: true), new Size(Width, Height));
                Left = anchor.X;
                Top = anchor.Y;
            }
            else
            {
                var area = DesktopWindow.Viewport(_returnWindow);
                Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
                Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            }
            if (CanAnimatePage)
            {
                var view = _pageViews[(int)_page];
                view.Opacity = 0.75;
                view.RenderTransform = new TranslateTransform(7, 0);
                var transition = ++_pageTransition;
                AnimatePane(view, 1, 0, 260, () => FinishPageTransition(transition));
            }
            Show();
        }
        Activate();
        AlignOverlay();
        ScrollToEnd();
    }

    private void DismissSettings(bool restoreFocus)
    {
        if (!IsVisible)
            return;
        CancelCapture();
        if (restoreFocus && IsActive)
            DesktopWindow.TryFocus(_returnWindow);
        Hide();
        SettlePage();
        AlignOverlay();
    }

    private void ShowPage(Page page, string status = "")
    {
        var animate = page != _page && IsVisible && CanAnimatePage;
        var direction = page == Page.Welcome || _page == Page.Preferences ? -1 : 1;
        if (page != Page.Create)
            Adapters.IsDropDownOpen = false;
        if (page != Page.Preferences)
        {
            LayoutPicker.IsDropDownOpen = false;
            GameWindowPicker.IsDropDownOpen = false;
        }
        CancelCapture();
        Status.Text = status;
        _page = page;
        BackButton.Visibility = VisibleIf(page is Page.Join or Page.Create or Page.Preferences);
        HeaderPreferences.Visibility = VisibleIf(page != Page.Preferences);
        HeaderHide.Visibility = VisibleIf(page != Page.Preferences);
        HeaderTitle.FontSize = page == Page.Preferences ? 17 : 19;
        HeaderTitle.Text = page switch
        {
            Page.Create => "创建房间",
            Page.Join => "加入房间",
            Page.Room => "营地房间",
            Page.Preferences => "设置",
            _ => "营地聊天",
        };
        Title = page == Page.Welcome ? "营地聊天" : $"营地聊天（{HeaderTitle.Text}）";
        _pageHeight = _preferences.WindowSize?.Height ?? page switch
        {
            Page.Create => 341,
            Page.Join => 315,
            Page.Room => 448,
            Page.Preferences => 260,
            _ => status.Length == 0 ? WelcomeHeight : WelcomeStatusHeight,
        };
        if (animate)
            BeginPageTransition(direction);
        else
            SettlePage();
    }

    private bool CanAnimatePage => _preferences.Animate && SystemParameters.ClientAreaAnimation;

    private void BeginPageTransition(int direction)
    {
        var transition = ++_pageTransition;
        var incoming = _pageViews[(int)_page];
        // Enlarge once before revealing content. Keep the old bounds until a smaller page settles.
        if (_pageHeight > Height)
            Height = _pageHeight;

        if (Keyboard.FocusedElement is DependencyObject focus && Stage.IsAncestorOf(focus) && !incoming.IsAncestorOf(focus))
            Keyboard.ClearFocus();
        if (_page is Page.Create or Page.Join)
            SetupPanel.Visibility = Visibility.Visible;
        Panel.SetZIndex(SetupPanel, _page is Page.Create or Page.Join ? 1 : 0);

        foreach (var view in _pageViews)
        {
            var active = ReferenceEquals(view, incoming);
            SetPageInteraction(view, active);
            Panel.SetZIndex(view, active ? 1 : 0);
            if (active)
            {
                if (view.Visibility != Visibility.Visible)
                {
                    view.Opacity = 0;
                    view.RenderTransform = new TranslateTransform(12 * direction, 0);
                    view.Visibility = Visibility.Visible;
                }
                AnimatePane(view, 1, 0, PageEnterMilliseconds, () => FinishPageTransition(transition));
            }
            else if (view.Visibility == Visibility.Visible)
            {
                AnimatePane(view, 0, -12 * direction, PageExitMilliseconds);
            }
        }
    }

    private void FinishPageTransition(int transition)
    {
        if (_pageTransition == transition)
            SettlePage();
    }

    private void SettlePage()
    {
        ++_pageTransition;
        var incoming = _pageViews[(int)_page];
        foreach (var view in _pageViews)
        {
            var active = ReferenceEquals(view, incoming);
            view.Visibility = VisibleIf(active);
            view.Opacity = 1;
            view.BeginAnimation(OpacityProperty, null);
            view.RenderTransform = Transform.Identity;
            SetPageInteraction(view, active);
        }
        SetupPanel.Visibility = VisibleIf(_page is Page.Create or Page.Join);
        Height = _pageHeight;
    }

    private static void SetPageInteraction(FrameworkElement view, bool active)
    {
        view.IsHitTestVisible = active;
        KeyboardNavigation.SetTabNavigation(view, active ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private static void AnimatePane(FrameworkElement view, double opacity, double x, int duration, Action? done = null)
    {
        var transform = view.RenderTransform as TranslateTransform;
        var offset = new TranslateTransform(transform?.X ?? 0, 0);
        view.RenderTransform = offset;
        var ease = new QuadraticEase { EasingMode = opacity > 0 ? EasingMode.EaseOut : EasingMode.EaseInOut };
        var fade = new DoubleAnimation(view.Opacity, opacity, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = ease,
        };
        if (done != null)
            fade.Completed += (_, _) => done();
        view.BeginAnimation(OpacityProperty, fade);
        offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offset.X, x, fade.Duration)
        {
            EasingFunction = ease,
        });
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // During Chinese IME composition, WPF routes the confirmation key as ImeProcessed.
        if (_capture != null)
        {
            e.Handled = true;
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                CancelCapture();
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
                CompleteCapture(HotkeyChord.Capture(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers));
            return;
        }
        if (e.Key == Key.Escape && !Adapters.IsDropDownOpen && !LayoutPicker.IsDropDownOpen && !GameWindowPicker.IsDropDownOpen)
        {
            e.Handled = true;
            if (_page is Page.Create or Page.Join or Page.Preferences)
                BackClicked(sender, e);
            else
                DismissSettings(restoreFocus: true);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (Composer.IsKeyboardFocused)
            {
                e.Handled = true;
                _ = SendMessageAsync();
            }
            else if (_page == Page.Join && JoinCode.IsKeyboardFocused)
            {
                e.Handled = true;
                JoinClicked(sender, e);
            }
        }
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;
        DragMove();
        if (_source == null || !IsVisible)
            return;
        var area = DesktopWindow.ScreenArea(_source.Handle, workArea: true);
        var snapped = OverlayDock.FromDrag(area, new Rect(Left, Top, Width, Height));
        var anchor = snapped.Place(area, new Size(Width, Height));
        Left = anchor.X;
        Top = anchor.Y;
        _preferences = _preferences with { SettingsDock = snapped };
        SavePreferences();
    }

    private void HideClicked(object sender, RoutedEventArgs e) => DismissSettings(restoreFocus: true);
    private void PreferencesClicked(object sender, RoutedEventArgs e) => OpenSettings();
    private async void OpenCreateClicked(object sender, RoutedEventArgs e)
    {
        ShowPage(Page.Create);
        await RefreshAdaptersAsync();
    }
    private void OpenJoinClicked(object sender, RoutedEventArgs e) => ShowPage(Page.Join);
    private void BackClicked(object sender, RoutedEventArgs e)
    {
        if (_connecting != null)
            return;
        if (_page == Page.Preferences)
            OpenRoom();
        else
            ShowPage(Page.Welcome);
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAdaptersAsync();
    private void GameWindowsOpened(object? sender, EventArgs e) => RefreshGameWindows(suggestForeground: false);

    private void RefreshGameWindows(bool suggestForeground = true)
    {
        var previous = GameWindowPicker.SelectedItem as WindowTarget;
        var windows = DesktopWindow.VisibleWindows();
        var applied = _borderless.Target;
        GameWindowPicker.ItemsSource = windows;
        GameWindowPicker.SelectedItem = applied != IntPtr.Zero
            ? windows.FirstOrDefault(window => window.Handle == applied)
            : windows.FirstOrDefault(window => suggestForeground && window.Handle == _returnWindow)
                ?? windows.FirstOrDefault(window => previous != null && window.Handle == previous.Handle
                    && window.Thread == previous.Thread && window.Process == previous.Process);
        UpdateWindowAction();
    }

    private void GameWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            UpdateWindowAction();
    }

    private void UpdateWindowAction()
    {
        var applied = _borderless.Applied;
        GameWindowPicker.IsEnabled = !applied;
        BorderlessButton.IsEnabled = applied || GameWindowPicker.SelectedItem is WindowTarget { IsAvailable: true };
        BorderlessButton.Content = applied ? "还原窗口" : "铺满屏幕";
        BorderlessButton.ToolTip = applied ? "恢复所选窗口原来的位置、大小和边框。"
            : "先把游戏切到窗口模式，再将所选窗口铺满屏幕。";
    }

    private async void BorderlessClicked(object sender, RoutedEventArgs e)
    {
        if (_borderless.Applied)
        {
            Status.Text = _borderless.Restore() ? "已还原游戏窗口。" : "当前无法还原游戏窗口。";
        }
        else if (GameWindowPicker.SelectedItem is WindowTarget { IsAvailable: true } game)
        {
            Status.Text = _borderless.Apply(game.Handle);
            if (_borderless.Applied)
                _returnWindow = game.Handle;
        }
        else
        {
            RefreshGameWindows();
            Status.Text = "请在列表中选择正在运行的游戏窗口。";
            return;
        }
        UpdateWindowAction();
        await Task.Delay(150);
        AlignOverlay();
    }

    private void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || LayoutPicker.SelectedIndex < 0)
            return;
        _preferences = _preferences with { Layout = (CaptionLayout)LayoutPicker.SelectedIndex, Animate = MotionEnabled.IsChecked == true };
        if (!_preferences.Animate)
            SettlePage();
        if (_captions != null)
            _captions.Animate = _preferences.Animate;
        if (_quickComposer != null)
            _quickComposer.Animate = _preferences.Animate;
        AlignOverlay();
        SavePreferences();
    }

    private void CaptureComposeClicked(object sender, RoutedEventArgs e) => BeginCapture(ShortcutAction.Compose);
    private void CaptureRoomClicked(object sender, RoutedEventArgs e) => BeginCapture(ShortcutAction.Room);

    private void BeginCapture(ShortcutAction action)
    {
        _capture = action;
        UpdateShortcutLabels();
        Status.Text = "按下包含 Ctrl、Alt 或 Shift 的组合键，Esc 取消。";
    }

    private void CancelCapture()
    {
        if (_capture == null)
            return;
        _capture = null;
        UpdateShortcutLabels();
        Status.Text = "";
    }

    private void CompleteCapture(HotkeyChord chord)
    {
        if (_capture is not { } action || !chord.IsValid || _hotkeys == null)
            return;
        var prior = _hotkeys.Current ?? _preferences.Shortcuts;
        var next = action == ShortcutAction.Compose ? prior with { Compose = chord } : prior with { Room = chord };
        _capture = null;
        if (!_hotkeys.TryApply(next))
        {
            Status.Text = next.Compose == next.Room ? "两个功能需要不同的快捷键。"
                : "系统未能注册这个快捷键，原设置已保留。";
        }
        else
        {
            _preferences = _preferences with { Shortcuts = next };
            Status.Text = "快捷键已更新。";
            SavePreferences();
        }
        UpdateShortcutLabels();
    }

    private void SavePreferences()
    {
        if (!_preferences.Save())
            Status.Text = "设置已生效，但无法写入程序目录。";
    }

    private void UpdateShortcutLabels()
    {
        var active = _hotkeys?.Current;
        SetShortcutLabel(ComposeKeyButton, _capture == ShortcutAction.Compose ? null : active?.Compose,
            _capture == ShortcutAction.Compose);
        SetShortcutLabel(RoomKeyButton, _capture == ShortcutAction.Room ? null : active?.Room,
            _capture == ShortcutAction.Room);
    }

    private void SetShortcutLabel(Button button, HotkeyChord? chord, bool capturing)
    {
        if (chord is { } shortcut)
        {
            var keys = shortcut.KeyLabels();
            button.ContentTemplate = (DataTemplate)FindResource("ShortcutKeycaps");
            button.Content = keys;
            AutomationProperties.SetItemStatus(button, string.Join(" ", keys));
        }
        else
        {
            button.ContentTemplate = null;
            button.Content = capturing ? "等待按键" : "点击设置";
            AutomationProperties.SetItemStatus(button, (string)button.Content);
        }
    }

    private async Task RefreshAdaptersAsync()
    {
        if (_refreshingAdapters)
            return;
        _refreshingAdapters = true;
        if (Adapters.SelectedItem == null)
            NetworkTip.Text = "正在读取网卡…";
        try
        {
            var choices = await Task.Run(ReadAdapters);
            if (_closing)
                return;
            var previous = (Adapters.SelectedItem as AdapterChoice)?.Address;
            Adapters.ItemsSource = choices;
            Adapters.SelectedItem = choices.FirstOrDefault(choice => choice.Address.Equals(previous)) ?? choices.FirstOrDefault();
            NetworkTip.Text = choices.Length > 0
                ? "同一 Wi-Fi 选本地网卡，异地选 Radmin VPN 或 Tailscale。"
                : "未检测到可用的 IPv4 网卡。连上网络后点刷新。";
        }
        finally
        {
            _refreshingAdapters = false;
        }
    }

    private static AdapterChoice[] ReadAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                    .Where(info => RoomInvite.CanBind(info.Address) && !IPAddress.IsLoopback(info.Address))
                    .Select(info => new AdapterChoice(adapter.Name, info.Address, NetworkPriority(adapter, info.Address))))
                .OrderBy(choice => choice.Priority)
                .ThenBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static int NetworkPriority(NetworkInterface adapter, IPAddress address)
    {
        var description = adapter.Name + " " + adapter.Description;
        if (description.Contains("Radmin", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
            return 1;
        var octets = address.GetAddressBytes();
        if ((octets[0] == 169 && octets[1] == 254) || (octets[0] == 198 && octets[1] is 18 or 19))
            return 3;
        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            return 0;
        if (description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
            || description.Contains("vmware", StringComparison.OrdinalIgnoreCase)
            || description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
            || description.Contains("wintun", StringComparison.OrdinalIgnoreCase)
            || adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel)
            return 2;
        return 0;
    }

    private async void HostClicked(object sender, RoutedEventArgs e)
    {
        if (Adapters.SelectedItem is not AdapterChoice adapter)
        {
            Status.Text = "请先选择一个可用的网卡。";
            return;
        }

        await StartRoomAsync((name, notify) => Task.Run<IRoomSession>(
            () => new HostRoom(adapter.Address, HostRoom.DefaultPort, name, notify)), "正在创建房间…",
            "房主 · " + adapter.Name, HostButton);
    }

    private async void JoinClicked(object sender, RoutedEventArgs e)
    {
        RoomInvite invite;
        try
        {
            invite = RoomInvite.Parse(JoinCode.Text);
        }
        catch (FormatException error)
        {
            Status.Text = error.Message;
            return;
        }
        await StartRoomAsync(async (name, notify) =>
            await GuestRoom.JoinAsync(invite, name, notify, _shutdown.Token), "正在连接房主…", "已加入", JoinButton);
    }

    private async Task StartRoomAsync(Func<string, Action<RoomEvent>, Task<IRoomSession>> start,
        string status, string role, Button action)
    {
        if (_connecting != null || _room != null || _leaving || _closing)
            return;
        var name = Wire.Clean(Nickname.Text, Wire.MaxName);
        if (name == null)
        {
            Status.Text = "请填写 1–24 字的昵称。";
            return;
        }
        SetupPanel.IsEnabled = false;
        Status.Text = status;
        var label = action.Content;
        action.Content = action == HostButton ? "建房中…" : "连接中…";
        if (_preferences.Animate && SystemParameters.ClientAreaAnimation)
        {
            ConnectionProgress.Visibility = Visibility.Visible;
            ConnectionProgress.IsIndeterminate = true;
        }
        var id = ++_sessionId;
        _connecting = ConnectAsync(start, name, role, id);
        try
        {
            await _connecting;
        }
        finally
        {
            _connecting = null;
            SetupPanel.IsEnabled = true;
            action.Content = label;
            ConnectionProgress.IsIndeterminate = false;
            ConnectionProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ConnectAsync(Func<string, Action<RoomEvent>, Task<IRoomSession>> start,
        string name, string role, long id)
    {
        try
        {
            var room = await start(name, item =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.InvokeAsync(() => HandleRoomEvent(id, item));
            });

            if (_closing)
            {
                await room.DisposeAsync();
                return;
            }

            _room = room;
            PrepareOverlay();
            _invite = (room as HostRoom)?.InviteCode;
            _role = role;
            _people = _invite == null ? 0 : 1;
            _history.Clear();
            _captions?.Clear();
            _quickComposer?.ResetDraft();
            Composer.Clear();
            ShowPage(Page.Room);
            CopyInvite.Visibility = _invite == null ? Visibility.Collapsed : Visibility.Visible;
            Status.Text = _invite == null ? WritingHint : "复制房间码发给朋友。";
            UpdateRoomLabel();
            AlignOverlay();
            AddLine("房间", _invite == null ? "已连接到房主。" : "房间已创建。");
            (room as GuestRoom)?.Start();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
                                     or System.Security.Cryptography.CryptographicException || Wire.IsConnectionError(error))
        {
            if (_closing)
                return;
            Status.Text = error switch
            {
                AuthenticationException => "房主身份与房间码不符，请让房主重新复制房间码。",
                SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } =>
                    "本机 TCP 47821 已被占用，请退出另一个建房程序。",
                SocketException or OperationCanceledException or System.IO.IOException =>
                    "连接失败。请检查房主是否开着房间、网卡是否选对，以及 Windows 防火墙。",
                System.Security.Cryptography.CryptographicException => "Windows 无法建立加密房间。",
                _ => error.Message,
            };
        }
    }

    private void HandleRoomEvent(long id, RoomEvent item)
    {
        if (id != _sessionId || _room == null || _closing)
            return;
        switch (item.Kind)
        {
            case RoomEventKind.Count:
                _people = item.Count;
                UpdateRoomLabel();
                break;
            case RoomEventKind.Closed:
                _ = LeaveRoomAsync(item.Text);
                break;
            case RoomEventKind.Notice:
                AddLine("房间", item.Text);
                break;
            case RoomEventKind.Message:
                AddLine(item.Name ?? "玩家", item.Text);
                if (!IsActive && _quickComposer?.IsVisible != true)
                    RememberForeground();
                AlignOverlay();
                _captions?.Add(item.Name ?? "玩家", item.Text);
                break;
        }
    }

    private void UpdateRoomLabel() => RoomLabel.Text = _people > 0 ? $"{_role} · {_people} 人" : _role;

    private void AddLine(string name, string text)
    {
        var nearBottom = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 24;
        _history.Add(new ChatLine(name, DateTime.Now.ToString("HH:mm"), text));
        if (_history.Count > 150)
            _history.RemoveAt(0);
        if (nearBottom)
            ScrollToEnd();
    }

    private void ScrollToEnd() => _ = Dispatcher.InvokeAsync(ChatScroll.ScrollToEnd, DispatcherPriority.Loaded);

    private void CopyClicked(object sender, RoutedEventArgs e)
    {
        if (_invite == null)
            return;
        try
        {
            Clipboard.SetText(_invite);
            Status.Text = "已复制房间码。";
        }
        catch (ExternalException)
        {
            Status.Text = "剪贴板正被占用，请再试一次。";
        }
    }

    private async void SendClicked(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async Task SendMessageAsync()
    {
        var draft = Composer.Text;
        if (string.IsNullOrWhiteSpace(draft))
            return;
        if (await SendRoomTextAsync(draft) && Composer.Text == draft)
        {
            Composer.Clear();
            Status.Text = WritingHint;
        }
    }

    private async Task<bool> SendRoomTextAsync(string value)
    {
        var room = _room;
        if (room == null || _sending || _closing)
            return false;
        var text = Wire.Clean(value, Wire.MaxText);
        if (text == null)
        {
            Status.Text = "请输入 1–400 字的单行消息。";
            return false;
        }
        _sending = true;
        SendButton.IsEnabled = false;
        try
        {
            await room.SendAsync(text);
            return ReferenceEquals(room, _room);
        }
        catch (Exception error) when (Wire.IsConnectionError(error))
        {
            if (ReferenceEquals(room, _room))
                Status.Text = "发送失败。草稿已保留。";
            return false;
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
        }
    }

    private async void LeaveClicked(object sender, RoutedEventArgs e) => await LeaveRoomAsync("已离开房间。");

    private async Task LeaveRoomAsync(string status)
    {
        if (_room == null || _leaving)
            return;
        _leaving = true;
        var room = _room;
        _room = null;
        _invite = null;
        DismissComposer(restoreFocus: true);
        _quickComposer?.ResetDraft();
        _captions?.Clear();
        ++_sessionId;
        SetupPanel.IsEnabled = false;
        ShowPage(Page.Welcome, status);
        try
        {
            await room.DisposeAsync();
        }
        finally
        {
            _leaving = false;
            SetupPanel.IsEnabled = true;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeComplete)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        _shutdown.Cancel();
        // Complete the original Closing event before calling Close again.
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (_connecting != null)
            await _connecting;
        await LeaveRoomAsync("");
        _hotkeys?.Dispose();
        if (_source is { IsDisposed: false })
            _source.RemoveHook(WindowMessage);
        _tray?.Dispose();
        _quickComposer?.Close();
        _captions?.Close();
        _borderless.Restore();
        _shutdown.Dispose();
        _closeComplete = true;
        Close();
    }

    private sealed record AdapterChoice(string Name, IPAddress Address, int Priority)
    {
        public override string ToString() => $"{Name}　{Address}";
    }

    private sealed record ChatLine(string Name, string Time, string Text);

}
