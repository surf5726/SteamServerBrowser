using System.Globalization;
using QueryMaster;

namespace ServerBrowser.Core;

public sealed class ServerColumn
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Field { get; set; } = "";
    public bool Custom { get; set; }
    public bool Numeric { get; set; }
    public bool Visible { get; set; } = true;
    public double Width { get; set; } = 120;
    public int Order { get; set; }

    public object? GetValue(ServerEntry row)
    {
        object? value;
        if (Field.StartsWith("Rule.", StringComparison.Ordinal))
            value = row.Rules.FirstOrDefault(r => string.Equals(r.Name, Field[5..], StringComparison.OrdinalIgnoreCase)).Value;
        else if (Field.StartsWith("Info.Extra.", StringComparison.Ordinal))
            value = row.Info is null ? null : typeof(ExtraInfo).GetProperty(Field[11..])?.GetValue(row.Info.Extra);
        else if (Field.StartsWith("Info.", StringComparison.Ordinal))
            value = row.Info is null ? null : typeof(ServerInfo).GetProperty(Field[5..])?.GetValue(row.Info);
        else value = typeof(ServerEntry).GetProperty(Field)?.GetValue(row);
        if (!Numeric || value is null) return value;
        return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}
