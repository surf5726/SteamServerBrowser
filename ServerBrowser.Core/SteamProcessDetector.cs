namespace ServerBrowser.Core;

public static class SteamProcessDetector
{
    public static bool IsGameRunning()
    {
        if (!OperatingSystem.IsLinux()) return false;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
            try
            {
                string environment = File.ReadAllText(Path.Combine(directory, "environ"));
                foreach (var entry in environment.Split('\0'))
                    if ((entry.StartsWith("SteamAppId=") || entry.StartsWith("SteamGameId=")) &&
                        ulong.TryParse(entry[(entry.IndexOf('=') + 1)..], out var id) && id > 0) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}
