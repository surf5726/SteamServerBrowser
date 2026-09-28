using System.Globalization;
using Avalonia.Data.Converters;

namespace ServerBrowser.Desktop;

public sealed class PlayerTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimeSpan time) return "";
        long seconds = time.Ticks / TimeSpan.TicksPerSecond;
        long magnitude = Math.Abs(seconds);
        // Keep total hours (including durations over a day), but hide fractional seconds.
        return string.Create(CultureInfo.InvariantCulture,
            $"{(seconds < 0 ? "-" : "")}{magnitude / 3600:00}:{magnitude / 60 % 60:00}:{magnitude % 60:00}");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
