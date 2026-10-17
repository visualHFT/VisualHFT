using System.Collections;
using System.Net.Http;
using System.Reflection;
using Newtonsoft.Json.Linq;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;

namespace VisualHFT.Desktop.Screens;

public enum TickerKind
{
    Spot,
    Futures
}

public sealed class TickerHit
{
    public required string Venue { get; init; }
    public required string Native { get; init; }
    public required string Normalized { get; init; }
    public required TickerKind Kind { get; init; }
    public required string SettingEntry { get; init; }
    public string KindLabel => Kind == TickerKind.Futures ? "futures" : "spot";
    public string Label => Native + "  " + Normalized + "  " + KindLabel;
}

public static class TickerSearch
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, (DateTime Utc, List<TickerHit> Hits)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    public static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "";
        var chars = query.Where(ch => ch is not ' ' and not '-' and not '_' and not '/' and not '.').ToArray();
        return new string(chars).ToUpperInvariant();
    }

    public static bool SameInstrument(string? bookSymbol, string native, string normalized)
    {
        if (string.IsNullOrEmpty(bookSymbol))
            return false;
        return string.Equals(bookSymbol, native, StringComparison.OrdinalIgnoreCase)
            || string.Equals(bookSymbol, normalized, StringComparison.OrdinalIgnoreCase);
    }

    public static string MapQuote(string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
            return "USD";
        var q = quote.Trim().ToUpperInvariant();
        return q is "USDT" or "USDC" or "BUSD" or "USD" or "ZUSD" ? "USD" : q;
    }

    public static string SettingEntry(string native, string? @base, string? quote)
    {
        native = native.Trim();
        if (native.Contains('/', StringComparison.Ordinal))
            return native;
        var baseAsset = string.IsNullOrWhiteSpace(@base) ? native : @base.Trim().ToUpperInvariant();
        return native + "(" + baseAsset + "/" + MapQuote(quote) + ")";
    }

    public static (string Native, string Normalized) ParseSetting(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return ("", "");
        var parts = entry.Split('(', 2, StringSplitOptions.TrimEntries);
        var native = parts[0].Trim();
        var normalized = parts.Length > 1 ? parts[1].Trim().TrimEnd(')') : native;
        return (native, string.IsNullOrEmpty(normalized) ? native : normalized);
    }

    public static IReadOnlyList<TickerHit> Shortlist(IEnumerable<TickerHit> catalog, string? query, int limit = 40)
    {
        var needle = NormalizeQuery(query);
        if (needle.Length == 0)
            return Array.Empty<TickerHit>();

        return catalog
            .Select(hit => (hit, score: Score(hit, needle)))
            .Where(row => row.score > 0)
            .OrderByDescending(row => row.score)
            .ThenBy(row => row.hit.Kind)
            .ThenBy(row => row.hit.Native, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 80))
            .Select(row => row.hit)
            .ToList();
    }

    public static int Score(TickerHit hit, string needle)
    {
        var native = NormalizeQuery(hit.Native);
        var normalized = NormalizeQuery(hit.Normalized);
        var best = 0;
        best = Math.Max(best, Rank(native, needle));
        best = Math.Max(best, Rank(normalized, needle));
        return best;
    }

    private static int Rank(string hay, string needle)
    {
        if (hay.Length == 0)
            return 0;
        if (hay == needle)
            return 300;
        if (hay.StartsWith(needle, StringComparison.Ordinal))
            return 200 - Math.Min(hay.Length, 80);
        if (hay.Contains(needle, StringComparison.Ordinal))
            return 100 - Math.Min(hay.Length, 80);
        return 0;
    }

    public static List<TickerHit> FromSettings(IPlugin plugin)
    {
        var hits = new List<TickerHit>();
        var venue = VenueName(plugin);
        foreach (var entry in ReadList(plugin.Settings, "Symbols"))
        {
            var parsed = ParseSetting(entry);
            if (parsed.Native.Length == 0)
                continue;
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = parsed.Native,
                Normalized = parsed.Normalized,
                Kind = TickerKind.Spot,
                SettingEntry = entry
            });
        }
        foreach (var entry in ReadList(plugin.Settings, "FuturesSymbols"))
        {
            var parsed = ParseSetting(entry);
            if (parsed.Native.Length == 0)
                continue;
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = parsed.Native,
                Normalized = parsed.Normalized,
                Kind = TickerKind.Futures,
                SettingEntry = entry
            });
        }
        return hits;
    }

    public static string VenueName(IPlugin plugin) =>
        plugin.Settings?.Provider?.ProviderName
        ?? plugin.Name.Replace(" Plugin", "", StringComparison.OrdinalIgnoreCase).Trim();

    public static async Task<IReadOnlyList<TickerHit>> FetchAsync(IPlugin plugin, CancellationToken cancel = default)
    {
        var key = plugin.GetPluginUniqueID();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Utc < CacheFor && cached.Hits.Count > 0)
                return cached.Hits;
        }

        var venue = VenueName(plugin);
        var family = plugin.Name;
        List<TickerHit> hits;
        try
        {
            hits = await LoadRemote(family, venue, plugin, cancel).ConfigureAwait(false);
        }
        catch
        {
            hits = new List<TickerHit>();
        }

        if (hits.Count == 0)
            hits = FromSettings(plugin);

        lock (Cache)
            Cache[key] = (DateTime.UtcNow, hits);
        return hits;
    }

    public static bool EnsureSubscribed(IPlugin plugin, TickerHit hit)
    {
        var settings = plugin.Settings;
        if (settings == null)
            return false;

        var propertyName = hit.Kind == TickerKind.Futures && HasProperty(settings, "FuturesSymbols")
            ? "FuturesSymbols"
            : "Symbols";
        var property = settings.GetType().GetProperty(propertyName);
        if (property == null)
            return false;

        var current = property.GetValue(settings);
        List<string> list;
        if (current is List<string> typed)
            list = typed;
        else if (current is IEnumerable<string> enumerable)
            list = enumerable.ToList();
        else
            list = new List<string>();

        if (list.Any(entry => string.Equals(ParseSetting(entry).Native, hit.Native, StringComparison.OrdinalIgnoreCase)))
            return false;

        list.Add(hit.SettingEntry);
        if (!property.PropertyType.IsInstanceOfType(list))
        {
            var converted = Activator.CreateInstance(property.PropertyType) as IList;
            if (converted == null)
                return false;
            foreach (var item in list)
                converted.Add(item);
            property.SetValue(settings, converted);
        }
        else
        {
            property.SetValue(settings, list);
        }

        Persist(plugin, settings);
        return true;
    }

    public static bool RestrictTo(IPlugin plugin, TickerHit hit)
    {
        var settings = plugin.Settings;
        if (settings == null)
            return false;

        var hasFutures = HasProperty(settings, "FuturesSymbols");
        var changed = false;
        if (hit.Kind == TickerKind.Futures && hasFutures)
        {
            changed |= ReplaceList(settings, "FuturesSymbols", new[] { hit.SettingEntry });
            changed |= ReplaceList(settings, "Symbols", Array.Empty<string>());
        }
        else
        {
            changed |= ReplaceList(settings, "Symbols", new[] { hit.SettingEntry });
            if (hasFutures)
                changed |= ReplaceList(settings, "FuturesSymbols", Array.Empty<string>());
        }

        if (changed)
            Persist(plugin, settings);
        return changed;
    }

    private static bool ReplaceList(ISetting settings, string propertyName, IReadOnlyList<string> values)
    {
        var property = settings.GetType().GetProperty(propertyName);
        if (property == null || !property.CanWrite)
            return false;

        var current = property.GetValue(settings);
        List<string> existing;
        if (current is List<string> typed)
            existing = typed;
        else if (current is IEnumerable<string> enumerable)
            existing = enumerable.ToList();
        else
            existing = new List<string>();

        if (existing.Count == values.Count
            && existing.Zip(values).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)))
            return false;

        var list = values.ToList();
        if (!property.PropertyType.IsInstanceOfType(list))
        {
            var converted = Activator.CreateInstance(property.PropertyType) as IList;
            if (converted == null)
                return false;
            foreach (var item in list)
                converted.Add(item);
            property.SetValue(settings, converted);
        }
        else
        {
            property.SetValue(settings, list);
        }
        return true;
    }

    private static void Persist(IPlugin plugin, ISetting settings)
    {
        SettingsManager.Instance.SetSetting(SettingKey.PLUGIN, plugin.GetPluginUniqueID(), settings);
        var save = plugin.GetType().GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        save?.Invoke(plugin, null);
    }

    public static void Validate()
    {
        var sol = new TickerHit
        {
            Venue = "Binance",
            Native = "SOLUSDT",
            Normalized = "SOL/USD",
            Kind = TickerKind.Futures,
            SettingEntry = "SOLUSDT(SOL/USD)"
        };
        var btc = new TickerHit
        {
            Venue = "Binance",
            Native = "BTCUSDT",
            Normalized = "BTC/USD",
            Kind = TickerKind.Spot,
            SettingEntry = "BTCUSDT(BTC/USD)"
        };
        var ethbtc = new TickerHit
        {
            Venue = "Binance",
            Native = "ETHBTC",
            Normalized = "ETH/BTC",
            Kind = TickerKind.Spot,
            SettingEntry = "ETHBTC(ETH/BTC)"
        };
        var catalog = new[] { sol, btc, ethbtc };

        if (NormalizeQuery("sol usdt") != "SOLUSDT")
            throw new InvalidOperationException("Ticker search should ignore separators.");
        if (Shortlist(catalog, "").Count != 0)
            throw new InvalidOperationException("Empty query should not list the catalog.");
        var solHits = Shortlist(catalog, "sol");
        if (solHits.Count != 1 || solHits[0].Native != "SOLUSDT")
            throw new InvalidOperationException("Typing a ticker should shortlist that venue's matches.");
        var btcHits = Shortlist(catalog, "btc");
        if (btcHits[0].Native != "BTCUSDT" || btcHits.All(hit => hit.Native != "ETHBTC"))
            throw new InvalidOperationException("Prefix matches should rank above contains.");
        if (SettingEntry("SOLUSDT", "SOL", "USDT") != "SOLUSDT(SOL/USD)")
            throw new InvalidOperationException("USDT pairs should subscribe as SOLUSDT(SOL/USD).");
        if (SettingEntry("BTC/USD", "BTC", "USD") != "BTC/USD")
            throw new InvalidOperationException("Kraken-style pairs should keep the slash form.");
        var parsed = ParseSetting("tBTCUSD(BTC/USD)");
        if (parsed.Native != "tBTCUSD" || parsed.Normalized != "BTC/USD")
            throw new InvalidOperationException("Settings entries should split native from normalized.");
        if (MapQuote("USDC") != "USD")
            throw new InvalidOperationException("Stable quotes should normalize to USD.");
        if (!SameInstrument("DOT/USD", "DOTUSDT", "DOT/USD") || !SameInstrument("DOTUSDT", "DOTUSDT", "DOT/USD"))
            throw new InvalidOperationException("Books publish either the native or normalized ticker.");
        if (SameInstrument("BTC/USD", "DOTUSDT", "DOT/USD"))
            throw new InvalidOperationException("A DOT watch must not keep a BTC book.");
    }

    public static string StorePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualHFT", "watchlist.json");

    public static IReadOnlyList<WatchSnapshot> LoadWatchlist()
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path))
                return Array.Empty<WatchSnapshot>();
            var array = JArray.Parse(File.ReadAllText(path));
            var items = new List<WatchSnapshot>();
            foreach (var row in array)
            {
                var venue = (string?)row["venue"];
                var native = (string?)row["native"];
                var normalized = (string?)row["normalized"];
                var entry = (string?)row["entry"];
                if (string.IsNullOrEmpty(venue) || string.IsNullOrEmpty(native) || string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(entry))
                    continue;
                var kind = string.Equals((string?)row["kind"], "Futures", StringComparison.OrdinalIgnoreCase)
                    ? TickerKind.Futures
                    : TickerKind.Spot;
                items.Add(new WatchSnapshot(venue, native, normalized, kind, entry));
            }
            return items;
        }
        catch
        {
            return Array.Empty<WatchSnapshot>();
        }
    }

    public static void SaveWatchlist(IEnumerable<WatchSnapshot> items)
    {
        var dir = Path.GetDirectoryName(StorePath());
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var array = new JArray();
        foreach (var item in items)
        {
            array.Add(new JObject
            {
                ["venue"] = item.Venue,
                ["native"] = item.Native,
                ["normalized"] = item.Normalized,
                ["kind"] = item.Kind.ToString(),
                ["entry"] = item.SettingEntry
            });
        }
        File.WriteAllText(StorePath(), array.ToString());
    }

    private static async Task<List<TickerHit>> LoadRemote(string family, string venue, IPlugin plugin, CancellationToken cancel)
    {
        if (family.Contains("Binance", StringComparison.OrdinalIgnoreCase))
            return await FetchBinance(venue, IsNonUs(plugin), cancel).ConfigureAwait(false);
        if (family.Contains("Coinbase", StringComparison.OrdinalIgnoreCase))
            return await FetchCoinbase(venue, cancel).ConfigureAwait(false);
        if (family.Contains("Kraken", StringComparison.OrdinalIgnoreCase))
            return await FetchKraken(venue, cancel).ConfigureAwait(false);
        if (family.Contains("BitStamp", StringComparison.OrdinalIgnoreCase) || family.Contains("Bitstamp", StringComparison.OrdinalIgnoreCase))
            return await FetchBitstamp(venue, cancel).ConfigureAwait(false);
        if (family.Contains("Bitfinex", StringComparison.OrdinalIgnoreCase))
            return await FetchBitfinex(venue, cancel).ConfigureAwait(false);
        if (family.Contains("KuCoin", StringComparison.OrdinalIgnoreCase) || family.Contains("Kucoin", StringComparison.OrdinalIgnoreCase))
            return await FetchKucoin(venue, cancel).ConfigureAwait(false);
        if (family.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            return await FetchGemini(venue, cancel).ConfigureAwait(false);
        return new List<TickerHit>();
    }

    private static async Task<List<TickerHit>> FetchBinance(string venue, bool international, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var spotHost = international ? "https://api.binance.com" : "https://api.binance.us";
        await AddBinance(hits, venue, TickerKind.Spot, spotHost + "/api/v3/exchangeInfo", cancel).ConfigureAwait(false);
        try
        {
            await AddBinance(hits, venue, TickerKind.Futures, "https://fapi.binance.com/fapi/v1/exchangeInfo", cancel).ConfigureAwait(false);
        }
        catch
        {
            // USD-M is unavailable on Binance.US; spot catalog still stands.
        }
        return Dedup(hits);
    }

    private static async Task AddBinance(List<TickerHit> hits, string venue, TickerKind kind, string url, CancellationToken cancel)
    {
        var json = JObject.Parse(await Get(url, cancel).ConfigureAwait(false));
        var symbols = json["symbols"] as JArray;
        if (symbols == null)
            return;
        foreach (var row in symbols)
        {
            if (!string.Equals((string?)row["status"], "TRADING", StringComparison.OrdinalIgnoreCase))
                continue;
            var native = (string?)row["symbol"];
            if (string.IsNullOrEmpty(native))
                continue;
            var @base = (string?)row["baseAsset"] ?? native;
            var quote = (string?)row["quoteAsset"];
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = @base + "/" + MapQuote(quote),
                Kind = kind,
                SettingEntry = SettingEntry(native, @base, quote)
            });
        }
    }

    private static async Task<List<TickerHit>> FetchCoinbase(string venue, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var array = JArray.Parse(await Get("https://api.exchange.coinbase.com/products", cancel).ConfigureAwait(false));
        foreach (var row in array)
        {
            if (!string.Equals((string?)row["status"], "online", StringComparison.OrdinalIgnoreCase))
                continue;
            var native = (string?)row["id"];
            if (string.IsNullOrEmpty(native))
                continue;
            var @base = (string?)row["base_currency"] ?? native;
            var quote = (string?)row["quote_currency"];
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = @base + "/" + MapQuote(quote),
                Kind = TickerKind.Spot,
                SettingEntry = SettingEntry(native, @base, quote)
            });
        }
        return Dedup(hits);
    }

    private static async Task<List<TickerHit>> FetchKraken(string venue, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var json = JObject.Parse(await Get("https://api.kraken.com/0/public/AssetPairs", cancel).ConfigureAwait(false));
        var result = json["result"] as JObject;
        if (result == null)
            return hits;
        foreach (var property in result.Properties())
        {
            var row = (JObject)property.Value;
            var status = (string?)row["status"];
            if (!string.IsNullOrEmpty(status) && !string.Equals(status, "online", StringComparison.OrdinalIgnoreCase))
                continue;
            var native = (string?)row["wsname"] ?? (string?)row["altname"] ?? property.Name;
            if (string.IsNullOrEmpty(native))
                continue;
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = native.Contains('/') ? native : native,
                Kind = TickerKind.Spot,
                SettingEntry = native
            });
        }
        return Dedup(hits);
    }

    private static async Task<List<TickerHit>> FetchBitstamp(string venue, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var array = JArray.Parse(await Get("https://www.bitstamp.net/api/v2/trading-pairs-info/", cancel).ConfigureAwait(false));
        foreach (var row in array)
        {
            if (!string.Equals((string?)row["trading"], "Enabled", StringComparison.OrdinalIgnoreCase))
                continue;
            var native = ((string?)row["url_symbol"] ?? "").Trim();
            var name = (string?)row["name"];
            if (native.Length == 0)
                continue;
            var slash = name?.Replace(" ", "") ?? native;
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = slash.Contains('/') ? slash : native,
                Kind = TickerKind.Spot,
                SettingEntry = SettingEntry(native, slash.Contains('/') ? slash.Split('/')[0] : native, slash.Contains('/') ? slash.Split('/')[1] : "USD")
            });
        }
        return Dedup(hits);
    }

    private static async Task<List<TickerHit>> FetchBitfinex(string venue, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var array = JArray.Parse(await Get("https://api-pub.bitfinex.com/v2/tickers?symbols=ALL", cancel).ConfigureAwait(false));
        foreach (var row in array)
        {
            if (row is not JArray pair || pair.Count == 0)
                continue;
            var native = (string?)pair[0];
            if (string.IsNullOrEmpty(native) || native[0] != 't')
                continue;
            var rest = native[1..];
            string @base;
            string quote;
            if (rest.Length > 6 && rest.Contains(':'))
            {
                var parts = rest.Split(':');
                @base = parts[0];
                quote = parts.Length > 1 ? parts[1] : "USD";
            }
            else if (rest.Length >= 6)
            {
                @base = rest[..^3];
                quote = rest[^3..];
            }
            else
            {
                @base = rest;
                quote = "USD";
            }
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = @base + "/" + MapQuote(quote),
                Kind = TickerKind.Spot,
                SettingEntry = SettingEntry(native, @base, quote)
            });
        }
        return Dedup(hits);
    }

    private static async Task<List<TickerHit>> FetchKucoin(string venue, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var json = JObject.Parse(await Get("https://api.kucoin.com/api/v1/symbols", cancel).ConfigureAwait(false));
        var data = json["data"] as JArray;
        if (data == null)
            return hits;
        foreach (var row in data)
        {
            if (row["enableTrading"]?.Value<bool>() != true)
                continue;
            var native = (string?)row["symbol"];
            if (string.IsNullOrEmpty(native))
                continue;
            var @base = (string?)row["baseCurrency"] ?? native;
            var quote = (string?)row["quoteCurrency"];
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = @base + "/" + MapQuote(quote),
                Kind = TickerKind.Spot,
                SettingEntry = SettingEntry(native, @base, quote)
            });
        }
        return Dedup(hits);
    }

    private static async Task<List<TickerHit>> FetchGemini(string venue, CancellationToken cancel)
    {
        var hits = new List<TickerHit>();
        var array = JArray.Parse(await Get("https://api.gemini.com/v1/symbols", cancel).ConfigureAwait(false));
        foreach (var token in array)
        {
            var native = ((string?)token ?? "").ToUpperInvariant();
            if (native.Length == 0)
                continue;
            var quote = native.EndsWith("USD", StringComparison.Ordinal) ? "USD" : native.Length >= 3 ? native[^3..] : "USD";
            var @base = native.Length > quote.Length ? native[..^quote.Length] : native;
            hits.Add(new TickerHit
            {
                Venue = venue,
                Native = native,
                Normalized = @base + "/" + MapQuote(quote),
                Kind = TickerKind.Spot,
                SettingEntry = SettingEntry(native, @base, quote)
            });
        }
        return Dedup(hits);
    }

    private static List<TickerHit> Dedup(List<TickerHit> hits)
    {
        return hits
            .GroupBy(hit => hit.Kind.ToString() + "|" + hit.Native, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(hit => hit.Native, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> Get(string url, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VisualHFT");
        return client;
    }

    private static bool IsNonUs(IPlugin plugin)
    {
        var property = plugin.Settings?.GetType().GetProperty("IsNonUS");
        return property?.GetValue(plugin.Settings) is true;
    }

    private static bool HasProperty(object settings, string name) =>
        settings.GetType().GetProperty(name) != null;

    private static IEnumerable<string> ReadList(object? settings, string name)
    {
        if (settings == null)
            return Array.Empty<string>();
        var property = settings.GetType().GetProperty(name);
        if (property?.GetValue(settings) is IEnumerable<string> list)
            return list.Where(item => !string.IsNullOrWhiteSpace(item));
        return Array.Empty<string>();
    }
}

public readonly record struct WatchSnapshot(string Venue, string Native, string Normalized, TickerKind Kind, string SettingEntry);
