using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CampChat.Preview;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CampChat.Preview;component/OverlayIcons.xaml", UriKind.Relative),
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
        var assembly = typeof(PreviewWindow).Assembly;
        var captionsType = assembly.GetType("CampChat.CaptionWindow", true)!;
        var layoutType = assembly.GetType("CampChat.CaptionLayout", true)!;
        var caption = (Window)Activator.CreateInstance(captionsType, nonPublic: true)!;
        var viewport = new Rect(-28000, -28000, 850, 600);
        var moveTo = captionsType.GetMethod("MoveTo")!;
        var add = captionsType.GetMethod("Add")!;
        var areaContent = (System.Windows.Controls.StackPanel)caption.Content;
        moveTo.Invoke(caption, [viewport, Enum.Parse(layoutType, "Team"), false, null]);
        for (var i = 0; i < 5; i++)
            add.Invoke(caption, ["队员", $"测试来信 {i}"]);
        Require(areaContent.Children.Count == 3 && caption.IsVisible && !caption.IsActive, "Team mode retains the last three without taking focus.");

        var hwnd = new WindowInteropHelper(caption).Handle;
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        Require((style & 0x08080020L) == 0x08080020L, "The caption window is layered, click-through, and cannot activate.");
        var panelLeft = caption.Left;
        moveTo.Invoke(caption, [viewport, Enum.Parse(layoutType, "Latest"), false, null]);
        Require(caption.Left > panelLeft && areaContent.Children.Count == 1, "Latest mode places one row at the right.");
        var normalTop = caption.Top;
        moveTo.Invoke(caption, [viewport, Enum.Parse(layoutType, "Latest"), true, null]);
        Require(caption.Top < normalTop, "The message moves above the composer.");

        captionsType.GetProperty("Animate")!.SetValue(caption, false);
        add.Invoke(caption, ["队员", "减少动效测试。"]);
        Require(areaContent.Children.Count == 1 && areaContent.Children[0].Opacity == 1, "Disabled animations display immediately.");
        await Task.Delay(7350);
        Require(!caption.IsVisible, "Inactive overlay hides when messages expire.");
        caption.Close();
        Require((Geometry)Application.Current.FindResource("SendIcon") != null, "Vector icons loaded.");
        Console.WriteLine("PASS: both caption layouts, overlay transparency/no activation, input clearance, reduced motion, idle hide and vector icons.");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
