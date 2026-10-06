using Avalonia;

namespace FeatherCad.App;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // files opened while FeatherCAD already runs open there, in tabs
        var files = args.Where(File.Exists).ToArray();
        if (files.Length > 0 && SingleInstance.TrySend(files)) return;

        // The kernel uses iterative algorithms but booleans on big models can recurse in places;
        // run the UI on a thread with a generous stack.
        var t = new Thread(() => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args), 64 * 1024 * 1024);
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software } })
            .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software } })
            .WithInterFont()
            .LogToTrace();
}
