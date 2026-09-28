namespace ServerBrowser.Core;

public static class PortableLauncher
{
    // A desktop file needs absolute Exec/Icon paths. Resolve them on the target
    // machine after extraction, not on the Windows machine producing the archive.
    public static string Write(string applicationDirectory)
    {
        string directory = Path.GetFullPath(applicationDirectory);
        string executable = Path.Combine(directory, "SteamServerBrowser");
        string icon = Path.Combine(directory, "SteamServerBrowser.png");
        string launcher = Path.Combine(directory, "SteamServerBrowser.desktop");
        string content = "[Desktop Entry]\nType=Application\nName=Steam Server Browser\n" +
            "Comment=Browse Steam game servers\nExec=" + ExecArgument(executable) + "\n" +
            "Icon=" + DesktopString(icon) + "\nPath=" + DesktopString(directory) + "\n" +
            "Terminal=false\nCategories=Game;Network;\nStartupWMClass=SteamServerBrowser\n";
        File.WriteAllText(launcher, content, new System.Text.UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return launcher;
    }

    public static string DesktopString(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    public static string ExecArgument(string value)
    {
        string quoted = "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
        return DesktopString(quoted);
    }
}
