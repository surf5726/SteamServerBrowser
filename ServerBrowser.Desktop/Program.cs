using Avalonia;

namespace ServerBrowser.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux())
        {
            try { ServerBrowser.Core.PortableLauncher.Write(AppContext.BaseDirectory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "SteamServerBrowser" }).LogToTrace();
}
