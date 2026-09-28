using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ServerBrowser.Desktop;

internal static class Dialogs
{
    public static async Task<bool> Form(Window owner, string title, params (string Label, Control Input)[] fields)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(22) };
        foreach (var (label, control) in fields)
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(control);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Name = "DialogAccept", Content = "OK", IsDefault = true };
        var cancel = new Button { Name = "DialogCancel", Content = "Cancel", IsCancel = true };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); panel.Children.Add(buttons);
        var dialog = new Window { Title = title, Width = 570, MaxHeight = 780, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel } };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }
    public static TextBox Text(string value = "", bool multiline = false) => new() { Text = value, AcceptsReturn = multiline, Height = multiline ? 150 : double.NaN, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
}
