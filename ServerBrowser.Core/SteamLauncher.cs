using System.Diagnostics;
using System.Net;

namespace ServerBrowser.Core;

public static class SteamLauncher
{
    public static string ConnectionUri(IPEndPoint endpoint, string password = "") =>
        "steam://connect/" + endpoint + (password.Length == 0 ? "" : "/" + Uri.EscapeDataString(password));
    public static void Connect(IPEndPoint endpoint, string password = "")
    {
        string uri = ConnectionUri(endpoint, password);
        if (OperatingSystem.IsLinux())
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(uri);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start xdg-open.");
        }
        else Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    }
}
