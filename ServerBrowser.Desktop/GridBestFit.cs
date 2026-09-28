using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;
using ServerBrowser.Core;

namespace ServerBrowser.Desktop;

/// <summary>Measures unwrapped rendered text, including the cell/header theme chrome.</summary>
internal static class GridBestFit
{
    public static void Fit(DataGrid grid, IEnumerable<DataGridColumn> columns)
    {
        grid.UpdateLayout();
        var rows = grid.ItemsSource?.Cast<object>().ToList() ?? [];
        var realizedRows = grid.GetVisualDescendants().OfType<DataGridRow>().ToList();
        var cells = grid.GetVisualDescendants().OfType<DataGridCell>().ToList();
        var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().ToList();
        var defaultCell = cells.FirstOrDefault();
        double sortSpace = grid.TryFindResource("DataGridSortIconMinWidth", out var resource) && resource is double width ? width : 32;

        foreach (var column in columns.Where(c => c.IsVisible))
        {
            var header = headers.FirstOrDefault(h => Equals(h.Content, column.Header));
            var headerText = header?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            var content = realizedRows.Select(r => column.GetCellContent(r)).FirstOrDefault(c => c is not null);
            var text = content as TextBlock ?? content?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            var cell = content?.GetVisualAncestors().OfType<DataGridCell>().FirstOrDefault() ?? defaultCell;

            var cellProbe = Probe(text, cell?.FontFamily ?? grid.FontFamily, cell?.FontSize ?? 12,
                cell?.FontStyle ?? grid.FontStyle, cell?.FontWeight ?? grid.FontWeight, cell?.FontStretch ?? grid.FontStretch);
            if (text is null && column is DataGridTextColumn textColumn)
            {
                if (textColumn.IsSet(DataGridTextColumn.FontFamilyProperty)) cellProbe.FontFamily = textColumn.FontFamily;
                if (textColumn.IsSet(DataGridTextColumn.FontSizeProperty)) cellProbe.FontSize = textColumn.FontSize;
                if (textColumn.IsSet(DataGridTextColumn.FontStyleProperty)) cellProbe.FontStyle = textColumn.FontStyle;
                if (textColumn.IsSet(DataGridTextColumn.FontWeightProperty)) cellProbe.FontWeight = textColumn.FontWeight;
                if (textColumn.IsSet(DataGridTextColumn.FontStretchProperty)) cellProbe.FontStretch = textColumn.FontStretch;
            }
            var headerProbe = Probe(headerText, header?.FontFamily ?? grid.FontFamily, header?.FontSize ?? 12,
                header?.FontStyle ?? grid.FontStyle, header?.FontWeight ?? grid.FontWeight, header?.FontStretch ?? grid.FontStretch);

            var headerPadding = header?.Padding ?? new Thickness(4, 3);
            var headerBorder = header?.BorderThickness ?? new Thickness(0, 0, 1, 1);
            double headerChrome = Horizontal(headerPadding) + Horizontal(headerBorder) + sortSpace + 1
                + Horizontal(headerText?.Margin ?? default) + Horizontal(headerText?.Padding ?? default);
            double cellChrome = Horizontal(cell?.Padding ?? new Thickness(3, 1))
                + Horizontal(cell?.BorderThickness ?? default) + 1
                + Horizontal(text?.Padding ?? default)
                + Horizontal(text?.Margin ?? (column is DataGridTextColumn ? new Thickness(4, 0) : default));

            if (column.Tag is ServerColumn { Id: "Country" }) cellChrome += 20; // flag and its gap
            else if (column.Tag is ServerColumn { Id: "Favorite" }) cellChrome += 16;

            double required = Measure(headerProbe, Convert.ToString(column.Header, CultureInfo.CurrentCulture) ?? "") + headerChrome;
            var measured = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                string value = DisplayText(column, row);
                if (!measured.TryGetValue(value, out var length)) measured[value] = length = Measure(cellProbe, value);
                required = Math.Max(required, length + cellChrome);
            }
            // Round outwards, with a little room for glyph overhang and fractional display scaling.
            double fitted = Math.Max(column.MinWidth, Math.Max(24, Math.Ceiling(required) + 2));
            if (column.MaxWidth < fitted) column.MaxWidth = fitted;
            column.Width = new DataGridLength(fitted, DataGridLengthUnitType.Pixel, fitted, fitted);
            if (column.Tag is ServerColumn definition) definition.Width = fitted;
        }
        grid.InvalidateMeasure();
        grid.UpdateLayout();
    }

    private static TextBlock Probe(TextBlock? actual, FontFamily family, double size, FontStyle style, FontWeight weight, FontStretch stretch) => new()
    {
        FontFamily = actual?.FontFamily ?? family,
        FontSize = actual?.FontSize ?? size,
        FontStyle = actual?.FontStyle ?? style,
        FontWeight = actual?.FontWeight ?? weight,
        FontStretch = actual?.FontStretch ?? stretch,
        FontFeatures = actual?.FontFeatures,
        LetterSpacing = actual?.LetterSpacing ?? 0,
        FlowDirection = actual?.FlowDirection ?? Avalonia.Media.FlowDirection.LeftToRight,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.None,
        Padding = default,
        Margin = default
    };

    private static double Measure(TextBlock probe, string text)
    {
        probe.Text = text;
        probe.Measure(Size.Infinity);
        return Math.Max(probe.DesiredSize.Width, probe.TextLayout.WidthIncludingTrailingWhitespace);
    }

    private static double Horizontal(Thickness value) => value.Left + value.Right;

    private static string DisplayText(DataGridColumn column, object row)
    {
        object? value = null;
        if (column.Tag is ServerColumn definition && row is ServerEntry server)
        {
            if (definition.Id == "Favorite") return "";
            if (definition.Custom) value = definition.GetValue(server);
            else if (column is DataGridTextColumn { Binding: Binding b })
                value = typeof(ServerEntry).GetProperty(b.Path ?? "")?.GetValue(server);
            else value = definition.GetValue(server);
        }
        else if (column is DataGridTextColumn { Binding: Binding binding })
            value = row.GetType().GetProperty(binding.Path ?? "")?.GetValue(row);
        if (column is DataGridTextColumn { Binding: Binding { Converter: { } converter } converted })
            value = converter.Convert(value, typeof(string), converted.ConverterParameter, converted.ConverterCulture ?? CultureInfo.CurrentCulture);
        string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        if (column is DataGridTextColumn { Binding: Binding { StringFormat: { Length: > 0 } format } })
            text = string.Format(CultureInfo.CurrentCulture, format, value);
        return text;
    }
}
