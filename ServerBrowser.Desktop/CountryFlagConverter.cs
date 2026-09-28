using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ServerBrowser.Desktop;

public sealed class CountryFlagConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string code = (value as string)?.Trim().ToLowerInvariant() ?? "";
        if (code.Length != 2 || code.Any(c => c is < 'a' or > 'z')) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(code, out var cached)) return cached;
            var uri = new Uri($"avares://SteamServerBrowser/Assets/Flags/{code}.png");
            Bitmap? flag = null;
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                flag = new Bitmap(stream);
            }
            Cache[code] = flag;
            return flag;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
