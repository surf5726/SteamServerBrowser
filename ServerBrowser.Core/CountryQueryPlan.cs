using System.Net;

namespace ServerBrowser.Core;

public sealed record ServerQueryTarget(IPEndPoint Endpoint, string Country);

public sealed record CountryQueryPlan(IReadOnlyList<ServerQueryTarget> Targets, int Excluded, string? Warning)
{
    public static bool Matches(string country, string countries)
    {
        var selected = ServerFilter.Tokens(countries);
        return selected.Length == 0 || selected.Contains(country, StringComparer.OrdinalIgnoreCase);
    }

    public static CountryQueryPlan Create(IEnumerable<IPEndPoint> endpoints, string countries,
        ICountryLookup lookup, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var selected = ServerFilter.Tokens(countries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count > 0 && !lookup.IsAvailable)
            throw new InvalidOperationException("Country filtering requires an offline MMDB database. Select it in Options.");

        var targets = new List<ServerQueryTarget>();
        var cachedCountries = new Dictionary<IPAddress, string>();
        int excluded = 0;
        string? warning = null;
        // Finish local IP filtering before allowing any server information requests.
        foreach (var endpoint in endpoints.Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            if (!cachedCountries.TryGetValue(endpoint.Address, out var country))
            {
                try { country = lookup.Lookup(endpoint.Address); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (selected.Count > 0)
                        throw new InvalidOperationException("Country lookup failed; no servers were queried. " + ex.Message, ex);
                    country = "";
                    warning ??= "Country lookup failed: " + ex.Message;
                }
                cachedCountries.Add(endpoint.Address, country);
            }
            if (selected.Count == 0 || selected.Contains(country)) targets.Add(new(endpoint, country));
            else excluded++;
        }
        cancellation.ThrowIfCancellationRequested();
        return new(targets, excluded, warning);
    }
}
