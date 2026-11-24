using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CampChat;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CampChat;component/OverlayTheme.xaml", UriKind.Relative),
        });
        _ = app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await CheckAsync();
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                app.Shutdown(1);
            }
        });
        return app.Run();
    }

    private static async Task CheckAsync()
    {
        var settings = new MainWindow
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -25000, Top = -25000,
        };
        settings.Show();
        await UntilAsync(() => (bool)ReadField(settings, "_ready")!);
        if (Environment.GetEnvironmentVariable("CAMPCHAT_QA_PROFILE") == "1")
        {
            await MeasureIdleAsync(settings, "window open");
            HideWindow(settings);
            await MeasureIdleAsync(settings, "in tray");
            settings.Close();
            await UntilAsync(() => (bool)ReadField(settings, "_closeComplete")!);
            return;
        }
        await VerifyPagesAsync(settings);
        CheckResizableWindow(settings);
        var refresh = typeof(MainWindow).GetMethod("RefreshAdaptersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)refresh.Invoke(settings, null)!;
        Require(((ComboBox)settings.FindName("Adapters")).ItemsSource != null, "The async network refresh applies its result on the UI thread.");
        var assembly = typeof(MainWindow).Assembly;
        var mode = assembly.GetType("CampChat.ShortcutAction")!;
        var chordType = assembly.GetType("CampChat.HotkeyChord")!;
        var compose = Enum.Parse(mode, "Compose");
        var room = Enum.Parse(mode, "Room");
        var hotkeys = ReadField(settings, "_hotkeys")!;
        var current = hotkeys.GetType().GetProperty("Current")!;
        Require(current.GetValue(hotkeys) != null, "Default/fallback global shortcuts registered.");

        var composeButton = (Button)settings.FindName("ComposeKeyButton");
        var roomButton = (Button)settings.FindName("RoomKeyButton");
        Capture(settings, compose, chordType, 7, 0x75); // Ctrl+Alt+Shift+F6
        var firstButton = Keys(composeButton);
        Require(firstButton == "Ctrl Alt Shift F6", "Compose displays the assigned chord as keycaps.");
        Capture(settings, room, chordType, 7, 0x76); // Ctrl+Alt+Shift+F7
        Require(Keys(roomButton) == "Ctrl Alt Shift F7", "Settings accepts a different custom chord.");
        Capture(settings, room, chordType, 7, 0x75);
        Require(Keys(roomButton) == "Ctrl Alt Shift F7", "Duplicate chord keeps the existing registration.");

        using var other = new HwndSource(new HwndSourceParameters("CampChat conflict test"));
        Require(RegisterHotKey(other.Handle, 0x5210, 7, 0x74), "Could reserve a chord in another HWND.");
        try
        {
            Capture(settings, compose, chordType, 7, 0x74);
            Require(Keys(composeButton) == firstButton, "An OS-level hotkey conflict preserves the old binding.");
        }
        finally
        {
            UnregisterHotKey(other.Handle, 0x5210);
        }

        var captureEscape = hotkeys.GetType().GetMethod("CaptureEscape")!;
        var releaseEscape = hotkeys.GetType().GetMethod("ReleaseEscape")!;
        Require((bool)captureEscape.Invoke(hotkeys, null)!, "The composing window can reserve bare Escape.");
        try
        {
            Require(!RegisterHotKey(other.Handle, 0x5211, 0, 0x1b), "Bare Escape belongs to the composing shortcut while held.");
        }
        finally
        {
            releaseEscape.Invoke(hotkeys, null);
        }
        Require(RegisterHotKey(other.Handle, 0x5211, 0, 0x1b), "Escape can be used by another window after the composing shortcut releases it.");
        UnregisterHotKey(other.Handle, 0x5211);

        CheckDock(assembly);
        CheckBorderless(assembly);

        var layout = (ComboBox)settings.FindName("LayoutPicker");
        layout.SelectedIndex = 1;
        var filePath = Path.Combine(AppContext.BaseDirectory, "CampChat.settings.json");
        var settingsJson = File.ReadAllText(filePath);
        Require(settingsJson.Contains("\"Layout\": 1") && settingsJson.Contains("\"VirtualKey\": 117"),
            "Layout and assigned shortcut are persisted.");

        HideWindow(settings);
        Require(!settings.IsVisible, "The room window hides as a whole for gameplay.");
        Require(ReadField(settings, "_tray") != null, "A tray control remains available.");
        settings.Show();
        Require(settings.IsVisible, "Room settings reopen from the hidden state.");
        settings.Close();
        await UntilAsync(() => (bool)ReadField(settings, "_closeComplete")!);
        Console.WriteLine("PASS: isolated entry/room/settings pages, editable hotkeys, scoped Escape, snap, reversible borderless, native tray.");
    }

    private static void CheckResizableWindow(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var preferences = type.GetField("_preferences", flags)!;
        var original = preferences.GetValue(window);
        var remember = type.GetMethod("RememberWindowSize", flags)!;
        var previous = new Size(window.Width, window.Height);
        window.Width = previous.Width + 64;
        window.Height = previous.Height + 28;
        window.UpdateLayout();
        remember.Invoke(window, [previous]);
        using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CampChat.settings.json")));
        var size = saved.RootElement.GetProperty("WindowSize");
        Require(size.GetProperty("Width").GetDouble() == window.ActualWidth && size.GetProperty("Height").GetDouble() == window.ActualHeight,
            "Resize saves the new desktop dimensions next to the executable.");
        preferences.SetValue(window, original);
        window.Width = previous.Width;
        type.GetMethod("SavePreferences", flags)!.Invoke(window, null);
    }

    private static string Keys(Button button) => string.Join(" ", (IReadOnlyList<string>)button.Content);

    private static void HideWindow(MainWindow settings) =>
        typeof(MainWindow).GetMethod("DismissSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, [false]);

    private static async Task MeasureIdleAsync(MainWindow window, string state)
    {
        await Task.Delay(4500);
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        await Task.Delay(10000);
        process.Refresh();
        var cpuTime = process.TotalProcessorTime - cpu;
        var managedAllocations = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Console.WriteLine($"{state}, {clock.Elapsed.TotalSeconds:F2} s: CPU {cpuTime.TotalMilliseconds:F2} ms, "
            + $"WS {process.WorkingSet64 / 1048576.0:F1} MiB, private {process.PrivateMemorySize64 / 1048576.0:F1} MiB, "
            + $"managed allocated {managedAllocations / 1024.0:F1} KiB, managed live {GC.GetTotalMemory(false) / 1048576.0:F1} MiB.");
        GC.KeepAlive(window);
    }

    private static async Task VerifyPagesAsync(MainWindow window)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var pageType = type.GetNestedType("Page", BindingFlags.NonPublic)!;
        var change = type.GetMethod("ShowPage", Flags)!;
        var panels = new Dictionary<string, FrameworkElement>
        {
            ["Welcome"] = (FrameworkElement)window.FindName("WelcomePanel"),
            ["Create"] = (FrameworkElement)window.FindName("CreateFields"),
            ["Join"] = (FrameworkElement)window.FindName("JoinFields"),
            ["Room"] = (FrameworkElement)window.FindName("ChatPanel"),
            ["Preferences"] = (FrameworkElement)window.FindName("PreferencesPanel"),
        };
        var captureFolder = Environment.GetEnvironmentVariable("CAMPCHAT_QA_RENDER");
        var motionFolder = Environment.GetEnvironmentVariable("CAMPCHAT_QA_MOTION");
        var switchControl = (CheckBox)window.FindName("MotionEnabled");
        var stage = (FrameworkElement)window.FindName("Stage");
        bool IsSettled(string page) => panels.All(item => item.Value.IsVisible == (item.Key == page))
            && panels[page].Opacity == 1 && panels[page].RenderTransform == Transform.Identity;
        void ChangePage(string page)
        {
            change.Invoke(window, [Enum.Parse(pageType, page), ""]);
            if (page == "Create")
            {
                var choiceType = type.GetNestedType("AdapterChoice", BindingFlags.NonPublic)!;
                var sample = Activator.CreateInstance(choiceType, "无线网络", IPAddress.Parse("192.0.2.12"), 0)!;
                var adapters = (ComboBox)window.FindName("Adapters");
                adapters.ItemsSource = new[] { sample };
                adapters.SelectedIndex = 0;
                ((TextBlock)window.FindName("NetworkTip")).Text = "同一 Wi-Fi 选本地网卡，异地选 Radmin VPN 或 Tailscale。";
            }
        }
        async Task Open(string page)
        {
            ChangePage(page);
            await UntilAsync(() => IsSettled(page));
            if (!string.IsNullOrEmpty(captureFolder))
            {
                Directory.CreateDirectory(captureFolder);
                SaveFrame(TakeFrame(window), Path.Combine(captureFolder, $"{page}.png"));
            }
        }
        await Open("Welcome");
        window.UpdateLayout();
        var create = (Button)window.FindName("CreateEntry");
        var join = (Button)window.FindName("JoinEntry");
        Require(create.ActualWidth < stage.ActualWidth / 2 && create.ActualWidth == join.ActualWidth
                && create.FontSize == join.FontSize && create.FontWeight == join.FontWeight,
            "Home actions are content-sized and use equal typography.");
        var standardWidth = window.Width;
        window.Width = window.MinWidth;
        window.UpdateLayout();
        Require(join.TranslatePoint(new Point(), stage).Y > create.TranslatePoint(new Point(), stage).Y,
            "Home actions wrap at the minimum width.");
        window.Width = standardWidth;
        Require(window.TryFindResource("CreateRoomIcon") is Geometry && window.TryFindResource("PlusIcon") == null,
            "Create Room uses a dedicated pictogram.");
        foreach (var name in new[] { "HeaderPreferences", "HeaderHide", "HeaderClose" })
        {
            var control = (Button)window.FindName(name);
            Require(control.Content is System.Windows.Shapes.Path icon && icon.Data != null && icon.Width is >= 15 and <= 18,
                $"{name} uses a compact vector pictogram.");
        }
        await Open("Create");
        await Open("Join");
        var nickname = (TextBox)window.FindName("Nickname");
        var joinNickname = (TextBox)window.FindName("JoinNickname");
        joinNickname.SetCurrentValue(TextBox.TextProperty, "测试玩家");
        Require(nickname.Text == "测试玩家", "Both forms use the same nickname draft.");
        nickname.Text = "玩家";
        await Open("Room");
        var append = type.GetMethod("AddLine", Flags)!;
        for (var index = 0; index < 36; index++)
            append.Invoke(window, ["队友", $"第 {index + 1} 条测试消息"]);
        var history = (ScrollViewer)window.FindName("ChatScroll");
        window.UpdateLayout();
        await UntilAsync(() => history.ScrollableHeight > 0 && history.VerticalOffset > 0);
        var bar = (System.Windows.Controls.Primitives.ScrollBar)history.Template.FindName("PART_VerticalScrollBar", history);
        bar.ApplyTemplate();
        Require(bar.Width == 11 && bar.Template.FindName("PART_Track", bar) is System.Windows.Controls.Primitives.Track { Thumb: not null },
            "Long conversations show the styled scrollbar and thumb.");
        if (!string.IsNullOrEmpty(captureFolder))
            SaveFrame(TakeFrame(window), Path.Combine(captureFolder, "Room.png"));
        await Open("Preferences");

        if (!string.IsNullOrEmpty(motionFolder))
        {
            Require(SystemParameters.ClientAreaAnimation, "Windows animations are enabled for the recording.");
            switchControl.IsChecked = true;
            Directory.CreateDirectory(motionFolder);
            ChangePage("Welcome");
            await UntilAsync(() => IsSettled("Welcome"));
            SaveFrame(TakeFrame(window), Path.Combine(motionFolder, "00-Welcome.png"));
            var sequence = 0;
            var previous = "Welcome";
            foreach (var page in new[] { "Create", "Welcome", "Join", "Welcome", "Preferences", "Welcome" })
            {
                var frames = new List<(RenderTargetBitmap Bitmap, long Elapsed)>();
                var clock = Stopwatch.StartNew();
                var incoming = panels[page];
                var outgoing = panels[previous];
                var windowResizes = 0;
                void OnResize(object sender, SizeChangedEventArgs e) { if (e.HeightChanged) windowResizes++; }
                window.SizeChanged += OnResize;
                try
                {
                    ChangePage(page);
                    Require(incoming.IsVisible && incoming.Opacity < 0.05 && outgoing.IsVisible && outgoing.Opacity > 0.95,
                        $"The first {page} frame retains the live outgoing page and starts the incoming page at zero opacity.");
                    Require(!outgoing.IsHitTestVisible && incoming.IsHitTestVisible, "Only the destination page can receive clicks.");
                    var observedArrival = false;
                    var observedDeparture = false;
                    do
                    {
                        await Task.Delay(15);
                        observedArrival |= incoming.Opacity is > 0 and < 1;
                        observedDeparture |= outgoing.IsVisible && outgoing.Opacity is > 0 and < 1;
                        Require(incoming.Opacity + (outgoing.IsVisible ? outgoing.Opacity : 0) > 0.55,
                            $"The {page} transition never drops both live pages to near zero opacity.");
                        frames.Add((TakeFrame(window), clock.ElapsedMilliseconds));
                    }
                    while (clock.ElapsedMilliseconds < 445);
                    Require(observedArrival && observedDeparture && IsSettled(page), $"The {page} transition animates and settles.");
                    Require(windowResizes <= 1 && !DependencyPropertyHelper.GetValueSource(window, FrameworkElement.HeightProperty).IsAnimated,
                        $"The {page} transition resizes native bounds at most once.");
                }
                finally { window.SizeChanged -= OnResize; }
                sequence++;
                previous = page;
                foreach (var (bitmap, elapsed) in frames)
                    SaveFrame(bitmap, Path.Combine(motionFolder, $"{sequence:D2}-{page}-{elapsed:D4}.png"));
            }
        }
        await Open("Preferences");
        switchControl.IsChecked = true;
        switchControl.ApplyTemplate();
        var track = (Border)switchControl.Template.FindName("Track", switchControl);
        var thumb = (FrameworkElement)switchControl.Template.FindName("Thumb", switchControl);
        var toggle = (IToggleProvider)new CheckBoxAutomationPeer(switchControl).GetPattern(PatternInterface.Toggle)!;
        Require(track.ActualWidth < 40 && thumb.HorizontalAlignment == HorizontalAlignment.Right,
            "The compact switch moves its thumb right when on.");
        Require(((ScrollViewer)panels["Preferences"]).ScrollableHeight == 0,
            "Settings fit without scrolling at the default window size.");
        var fillScreen = (Button)window.FindName("BorderlessButton");
        var buttonText = (ContentPresenter)fillScreen.Template.FindName("ButtonContent", fillScreen);
        var labelCenter = buttonText.TranslatePoint(new Point(buttonText.ActualWidth / 2, 0), fillScreen);
        Require(Math.Abs(labelCenter.X - fillScreen.ActualWidth / 2) <= 0.5,
            "The fill-screen label is centered within the button.");
        var groups = new[] { (GroupBox)window.FindName("HotkeyGroup"), (GroupBox)window.FindName("DisplayGroup") };
        Require(groups.All(group =>
            group.Template.FindName("Outline", group) is Border { CornerRadius.TopLeft: 9, OpacityMask: not null }),
            "Both settings groups use continuous rounded outlines with a measured legend gap.");
        toggle.Toggle();
        Require(thumb.HorizontalAlignment == HorizontalAlignment.Left && switchControl.IsChecked == false,
            "Accessible toggle updates the off state immediately.");
        if (!string.IsNullOrEmpty(captureFolder))
            SaveFrame(TakeFrame(window), Path.Combine(captureFolder, "PreferencesOff.png"));
        ChangePage("Welcome");
        Require(IsSettled("Welcome"), "Reduced motion switches directly to the target page.");
        switchControl.IsChecked = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            for (var i = 0; i < 8; i++)
            {
                ChangePage("Create");
                await Task.Delay(28);
                ChangePage("Preferences");
                await Task.Delay(28);
                ChangePage("Welcome");
                await Task.Delay(28);
                ChangePage("Join");
                await Task.Delay(28);
            }
            await UntilAsync(() => IsSettled("Join"));
            Require(window.Height == 315 && panels["Join"].IsHitTestVisible, "Rapid route changes settle on the last page and its height.");
            Console.WriteLine("PASS: 32 interrupted transitions, live first frames, and a single resize per page change.");
        }
        await Open("Preferences");
    }

    private static RenderTargetBitmap TakeFrame(MainWindow window)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static void SaveFrame(RenderTargetBitmap bitmap, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    private static void CheckDock(Assembly assembly)
    {
        var dockType = assembly.GetType("CampChat.OverlayDock")!;
        var viewport = new Rect(500, 100, 1200, 650);
        var fromDrag = dockType.GetMethod("FromDrag")!;
        var place = dockType.GetMethod("Place")!;
        var left = fromDrag.Invoke(null, [viewport, new Rect(524, 350, 310, 45)])!;
        var right = fromDrag.Invoke(null, [viewport, new Rect(1375, 350, 310, 45)])!;
        var free = fromDrag.Invoke(null, [viewport, new Rect(900, 420, 310, 45)])!;
        Require(((Point)place.Invoke(left, [viewport, new Size(310, 45)])!).X == 520, "Leftward drag snaps to the left margin.");
        Require(((Point)place.Invoke(right, [viewport, new Size(310, 45)])!).X == 1370, "Rightward drag snaps to the right margin.");
        var freePoint = (Point)place.Invoke(free, [viewport, new Size(310, 45)])!;
        Require(Math.Abs(freePoint.X - 900) < 0.001 && Math.Abs(freePoint.Y - 420) < 0.001,
            "Middle positions stay where the user dragged them.");
    }

    private static void CheckBorderless(Assembly assembly)
    {
        var target = new Window { Title = "Racing format fixture", Width = 720, Height = 400, ShowActivated = false, ShowInTaskbar = false };
        var other = new Window { Title = "Strategy format fixture", Width = 610, Height = 360, ShowActivated = false, ShowInTaskbar = false };
        var hwnd = new WindowInteropHelper(target).EnsureHandle();
        var otherHwnd = new WindowInteropHelper(other).EnsureHandle();
        const long Frame = 0x00c40000;
        var initial = GetWindowLongPtr(hwnd, -16).ToInt64();
        var controllerType = assembly.GetType("CampChat.BorderlessGameWindow")!;
        var controller = Activator.CreateInstance(controllerType, nonPublic: true)!;
        var apply = controllerType.GetMethod("Apply")!;
        var restore = controllerType.GetMethod("Restore")!;
        Require((initial & Frame) != 0, "Window starts with a standard native frame.");
        Require(GetWindowRect(hwnd, out var original), "Read the original window bounds.");
        Require(GetWindowRect(otherHwnd, out var otherBounds), "Read the unselected window bounds.");
        try
        {
            var result = (string)apply.Invoke(controller, [hwnd])!;
            Require(result.Contains("已切换") && (GetWindowLongPtr(hwnd, -16).ToInt64() & Frame) == 0, "The borderless action removes the native frame.");
            Require(GetWindowRect(hwnd, out var fullscreen) && fullscreen.Width >= original.Width,
                "The Win32 window expands to the monitor.");
            apply.Invoke(controller, [otherHwnd]);
            Require(GetWindowRect(otherHwnd, out var stillOriginal) && stillOriginal.Equals(otherBounds),
                "Applying to another window first requires restoring the selected one.");
            Require((bool)restore.Invoke(controller, null)!, "The window can be restored.");
            Require(GetWindowLongPtr(hwnd, -16).ToInt64() == initial && GetWindowRect(hwnd, out var restored)
                && restored.Equals(original), "Frame and bounds return to the original window state.");
        }
        finally
        {
            restore.Invoke(controller, null);
            other.Close();
            target.Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);

    private static object? ReadField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static void Capture(MainWindow settings, object action, Type chordType, uint modifier, uint key)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("BeginCapture", Flags)!.Invoke(settings, [action]);
        typeof(MainWindow).GetMethod("CompleteCapture", Flags)!.Invoke(settings,
            [Activator.CreateInstance(chordType, modifier, key)]);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
