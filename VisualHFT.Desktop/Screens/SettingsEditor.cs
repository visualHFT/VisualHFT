using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using VisualHFT.Enums;
using VisualHFT.Studies.LOBImbalance.Model;

namespace VisualHFT.Desktop.Screens;

public enum SettingsFieldKind
{
    Text,
    Secret,
    Multiline,
    Bool,
    Choice
}

public sealed class SettingsChoice
{
    public SettingsChoice(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }
    public string Label { get; }
}

public sealed class SettingsPluginHint
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class SettingsFieldModel
{
    public required string Relative { get; init; }
    public required string Label { get; init; }
    public required string Hint { get; init; }
    public required string Section { get; init; }
    public required SettingsFieldKind Kind { get; init; }
    public required string Value { get; init; }
    public required string Path { get; init; }
    public string? ListPath { get; init; }
    public IReadOnlyList<SettingsChoice> Choices { get; init; } = Array.Empty<SettingsChoice>();
}

public sealed class SettingsSectionModel
{
    public required string Title { get; init; }
    public required IReadOnlyList<SettingsFieldModel> Fields { get; init; }
}

public sealed class SettingsGroupModel
{
    public required string Key { get; init; }
    public required string BaseTitle { get; init; }
    public string Title { get; set; } = "";
    public required string Kind { get; init; }
    public required string Bucket { get; init; }
    public required string Summary { get; init; }
    public required string StatusText { get; init; }
    public required string StatusTone { get; init; }
    public required string Disambiguator { get; init; }
    public required string ShortId { get; init; }
    public required IReadOnlyList<SettingsSectionModel> Sections { get; init; }
}

public static class SettingsEditor
{
    private const string ComponentHead = "settings.ComponentSettings.";

    private static readonly string[] SectionOrder = ["Credentials", "Connection", "Market", "General", "Advanced"];

    private static readonly string[] FieldOrder =
    [
        "ApiKey", "ApiSecret", "APIPassPhrase",
        "HostName", "WebSocketHostName", "WebSocketHostName_UserOrder", "Port",
        "Symbols", "FuturesSymbols", "Symbol", "DepthLevels", "UpdateIntervalMs", "AggregationLevel",
        "BucketVolSize", "NumberOfBuckets", "MaxShockMsTimeout", "Weighting",
        "UseCorrectedForm", "UseVolumeForm", "IsNonUS",
        "Provider.ProviderName", "ProviderName", "Provider.ProviderID", "ProviderId", "Provider.ProviderCode"
    ];

    private static readonly HashSet<string> RuntimeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Provider.Status",
        "Provider.LastUpdated",
        "Provider.Tooltip"
    };

    private sealed record FieldMeta(string Section, string Label, string Hint, SettingsFieldKind? Kind = null);

    private static readonly Dictionary<string, FieldMeta> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ApiKey"] = new("Credentials", "API key", "Signs private endpoints. Public market data works with this left blank.", SettingsFieldKind.Secret),
        ["ApiSecret"] = new("Credentials", "API secret", "Stored in the settings file on this machine.", SettingsFieldKind.Secret),
        ["APIPassPhrase"] = new("Credentials", "API passphrase", "Third credential, required by some venues.", SettingsFieldKind.Secret),
        ["HostName"] = new("Connection", "REST host", "Base URL for the venue's REST API."),
        ["WebSocketHostName"] = new("Connection", "WebSocket host", "Market-data websocket endpoint."),
        ["WebSocketHostName_UserOrder"] = new("Connection", "Order WebSocket", "Websocket used for the venue's private order stream."),
        ["Port"] = new("Connection", "Port", "TCP port for a raw websocket connector."),
        ["Symbols"] = new("Market", "Symbols", "One instrument per line, in the venue's own symbol format."),
        ["FuturesSymbols"] = new("Market", "Futures symbols", "USD-M perpetuals, one per line. These stream even when spot uses a US endpoint."),
        ["Symbol"] = new("Market", "Symbol", "Instrument this plugin publishes."),
        ["DepthLevels"] = new("Market", "Depth levels", "Price levels kept on each side of the book."),
        ["UpdateIntervalMs"] = new("Market", "Update interval", "Milliseconds between depth snapshots."),
        ["AggregationLevel"] = new("Market", "Aggregation", "Bar size for studies attached to this feed."),
        ["BucketVolSize"] = new("Market", "Bucket volume", "Volume that closes one VPIN bucket."),
        ["NumberOfBuckets"] = new("Market", "Buckets in window", "Rolling window. Corrected VPIN stays quiet until this many buckets close."),
        ["MaxShockMsTimeout"] = new("Market", "Shock timeout", "Milliseconds to wait for the book to recover after a shock."),
        ["Weighting"] = new("Market", "Weighting", "How depth is weighted in the imbalance."),
        ["UseCorrectedForm"] = new("Market", "Corrected form", "Classify trades with the tick rule, and publish only after a full window.", SettingsFieldKind.Bool),
        ["UseVolumeForm"] = new("Market", "Volume form", "Compare size posted with size executed, instead of message counts.", SettingsFieldKind.Bool),
        ["IsNonUS"] = new("Market", "Binance.com", "Use Binance.com instead of Binance.US.", SettingsFieldKind.Bool),
        ["Provider.ProviderName"] = new("Advanced", "Venue name", "Display name stored with this plugin."),
        ["ProviderName"] = new("Advanced", "Venue name", "Display name stored with this plugin."),
        ["Provider.ProviderID"] = new("Advanced", "Venue id", "Studies match a connector on this id."),
        ["ProviderId"] = new("Advanced", "Venue id", "Studies match a connector on this id."),
        ["Provider.ProviderCode"] = new("Advanced", "Venue code", "Same identity as the venue id for built-in connectors.")
    };

    public static IReadOnlyList<SettingsGroupModel> Organize(SettingsNode root, IReadOnlyDictionary<string, SettingsPluginHint>? plugins = null)
    {
        plugins ??= new Dictionary<string, SettingsPluginHint>();
        var grouped = new Dictionary<string, List<Leaf>>(StringComparer.Ordinal);
        var application = new List<Leaf>();
        foreach (var (path, node) in SettingsTree.Leaves(root))
        {
            var leaf = new Leaf(path, "", node.Value ?? "", node.Source?.Type ?? JTokenType.String);
            if (TryComponent(path, out var category, out var id, out var relative))
            {
                var key = category + "\u001f" + id;
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = new List<Leaf>();
                    grouped.Add(key, list);
                }
                list.Add(leaf with { Relative = relative });
                continue;
            }
            if (path.StartsWith("settings.", StringComparison.Ordinal) && path.Length > "settings.".Length)
                application.Add(leaf with { Relative = path["settings.".Length..] });
        }

        var groups = new List<SettingsGroupModel>();
        foreach (var (key, leaves) in grouped)
        {
            var split = key.Split('\u001f');
            groups.Add(BuildComponent(split[0], split[1], leaves, plugins));
        }
        if (application.Count > 0)
            groups.Add(BuildApplication(application));
        Disambiguate(groups);
        return groups
            .OrderBy(group => BucketRank(group.Bucket))
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static SettingsGroupModel BuildComponent(string category, string id, List<Leaf> leaves, IReadOnlyDictionary<string, SettingsPluginHint> plugins)
    {
        plugins.TryGetValue(id, out var hint);
        var provider = ValueOf(leaves, "Provider.ProviderName");
        if (provider.Length == 0)
            provider = ValueOf(leaves, "ProviderName");
        var title = FirstNonEmpty(hint?.Name, provider);
        if (title.Length == 0)
            title = KindOf(category) + " " + Short(id);
        var (status, tone) = ReadStatus(leaves);
        var summary = SummaryOf(leaves, hint?.Description);
        var symbol = ValueOf(leaves, "Symbol");
        if (symbol.Length == 0)
            symbol = FirstListItem(leaves, "Symbols");
        if (symbol.Length == 0)
            symbol = FirstListItem(leaves, "FuturesSymbols");
        return new SettingsGroupModel
        {
            Key = category + ":" + id,
            BaseTitle = title,
            Title = title,
            Kind = KindOf(category),
            Bucket = BucketOf(category),
            Summary = summary,
            StatusText = status,
            StatusTone = tone,
            Disambiguator = symbol,
            ShortId = Short(id),
            Sections = BuildSections(leaves)
        };
    }

    private static SettingsGroupModel BuildApplication(List<Leaf> leaves)
    {
        return new SettingsGroupModel
        {
            Key = "application",
            BaseTitle = "Application",
            Title = "Application",
            Kind = "Application",
            Bucket = "Application",
            Summary = "",
            StatusText = "",
            StatusTone = "",
            Disambiguator = "",
            ShortId = "",
            Sections = BuildSections(leaves)
        };
    }

    private static void Disambiguate(List<SettingsGroupModel> groups)
    {
        foreach (var set in groups.GroupBy(group => group.Title, StringComparer.OrdinalIgnoreCase).Where(set => set.Count() > 1))
        {
            foreach (var group in set)
            {
                var suffix = group.Disambiguator.Length > 0 ? group.Disambiguator : group.ShortId;
                if (suffix.Length > 0)
                    group.Title = group.BaseTitle + " · " + suffix;
            }
        }
        foreach (var set in groups.GroupBy(group => group.Title, StringComparer.OrdinalIgnoreCase).Where(set => set.Count() > 1))
        {
            foreach (var group in set)
                group.Title = group.BaseTitle + " · " + group.ShortId;
        }
    }

    private static IReadOnlyList<SettingsSectionModel> BuildSections(List<Leaf> leaves)
    {
        var fields = BuildFields(leaves);
        return fields
            .GroupBy(field => field.Section, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => Rank(SectionOrder, group.Key))
            .Select(group => new SettingsSectionModel
            {
                Title = group.Key,
                Fields = group.OrderBy(field => Rank(FieldOrder, field.Relative)).ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();
    }

    private static List<SettingsFieldModel> BuildFields(List<Leaf> leaves)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<SettingsFieldModel>();
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            if (TryListIndex(leaf.Relative, out var parent, out _))
                parents.Add(parent);
        }

        foreach (var parent in parents.OrderBy(name => name, StringComparer.Ordinal))
        {
            var prefix = parent + ".";
            var members = leaves.Where(leaf => leaf.Relative.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (members.Count == 0)
                continue;
            if (members.Any(leaf => !TryListIndex(leaf.Relative, out var owner, out _) || owner != parent))
                continue;
            if (members.Any(leaf => leaf.Type is not (JTokenType.String or JTokenType.Null)))
                continue;
            foreach (var member in members)
                consumed.Add(member.Path);
            var ordered = members.OrderBy(leaf => ListIndex(leaf.Relative)).ToList();
            var value = string.Join('\n', ordered.Select(leaf => leaf.Value));
            var listPath = StripIndex(ordered[0].Path);
            fields.Add(Describe(parent, value, listPath, listPath, JTokenType.String, list: true));
        }

        foreach (var leaf in leaves)
        {
            if (consumed.Contains(leaf.Path) || RuntimeFields.Contains(leaf.Relative))
                continue;
            fields.Add(Describe(leaf.Relative, Normalize(leaf), leaf.Path, null, leaf.Type, list: false));
        }
        return fields;
    }

    private static SettingsFieldModel Describe(string relative, string value, string path, string? listPath, JTokenType type, bool list)
    {
        Known.TryGetValue(relative, out var meta);
        var choices = list ? null : ChoicesFor(relative, type, value);
        var kind = SettingsFieldKind.Text;
        if (list)
            kind = SettingsFieldKind.Multiline;
        else if (meta?.Kind == SettingsFieldKind.Secret || IsSecretName(relative))
            kind = SettingsFieldKind.Secret;
        else if (choices != null)
            kind = SettingsFieldKind.Choice;
        else if (meta?.Kind == SettingsFieldKind.Bool || type == JTokenType.Boolean)
            kind = SettingsFieldKind.Bool;
        else if (meta?.Kind != null)
            kind = meta.Kind.Value;

        var section = meta?.Section;
        if (string.IsNullOrEmpty(section))
        {
            var dot = relative.IndexOf('.');
            section = dot > 0 ? Humanize(relative[..dot]) : "General";
            if (kind == SettingsFieldKind.Secret)
                section = "Credentials";
        }

        return new SettingsFieldModel
        {
            Relative = relative,
            Label = meta?.Label ?? Humanize(relative),
            Hint = meta?.Hint ?? "",
            Section = section,
            Kind = kind,
            Value = value,
            Path = path,
            ListPath = listPath,
            Choices = choices ?? Array.Empty<SettingsChoice>()
        };
    }

    private static IReadOnlyList<SettingsChoice>? ChoicesFor(string relative, JTokenType type, string stored)
    {
        var name = LeafName(relative);
        if (name.Equals("AggregationLevel", StringComparison.OrdinalIgnoreCase))
            return EnumChoices<AggregationLevel>(type, stored);
        if (name.Equals("Weighting", StringComparison.OrdinalIgnoreCase))
            return EnumChoices<ImbalanceWeighting>(type, stored);
        return null;
    }

    private static IReadOnlyList<SettingsChoice> EnumChoices<T>(JTokenType type, string stored) where T : struct, Enum
    {
        var numeric = type is JTokenType.Integer or JTokenType.Float
            || int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        var list = new List<SettingsChoice>();
        foreach (var value in Enum.GetValues<T>())
        {
            var number = Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            var name = value.ToString() ?? number;
            list.Add(new SettingsChoice(numeric ? number : name, DescriptionOf(value) ?? Humanize(name)));
        }
        if (!list.Any(choice => choice.Value == stored))
            list.Add(new SettingsChoice(stored, stored.Length == 0 ? "Not set" : stored));
        return list;
    }

    private static string? DescriptionOf(Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        return field?.GetCustomAttribute<DescriptionAttribute>()?.Description;
    }

    private static (string Text, string Tone) ReadStatus(List<Leaf> leaves)
    {
        var raw = ValueOf(leaves, "Provider.Status");
        if (!TrySession(raw, out var status))
        {
            var tooltip = ValueOf(leaves, "Provider.Tooltip");
            return tooltip.Length == 0 ? ("", "") : (tooltip, "idle");
        }
        var text = status switch
        {
            eSESSIONSTATUS.CONNECTING => "Connecting",
            eSESSIONSTATUS.CONNECTED => "Connected",
            eSESSIONSTATUS.CONNECTED_WITH_WARNINGS => "Connected with warnings",
            eSESSIONSTATUS.DISCONNECTED_FAILED => "Connection failed",
            eSESSIONSTATUS.DISCONNECTED => "Disconnected",
            _ => ""
        };
        var tone = status switch
        {
            eSESSIONSTATUS.CONNECTED => "ok",
            eSESSIONSTATUS.CONNECTING or eSESSIONSTATUS.CONNECTED_WITH_WARNINGS => "warn",
            eSESSIONSTATUS.DISCONNECTED_FAILED => "bad",
            _ => "idle"
        };
        return (text, tone);
    }

    private static bool TrySession(string value, out eSESSIONSTATUS status)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            && Enum.IsDefined(typeof(eSESSIONSTATUS), number))
        {
            status = (eSESSIONSTATUS)number;
            return true;
        }
        return Enum.TryParse(value, true, out status);
    }

    private static string SummaryOf(List<Leaf> leaves, string? description)
    {
        var symbols = ListValues(leaves, "Symbols");
        if (symbols.Count == 0)
            symbols = ListValues(leaves, "FuturesSymbols");
        if (symbols.Count > 0)
        {
            var shown = symbols.Count > 4
                ? string.Join(", ", symbols.Take(4)) + " +" + (symbols.Count - 4).ToString(CultureInfo.InvariantCulture)
                : string.Join(", ", symbols);
            return shown;
        }
        var symbol = ValueOf(leaves, "Symbol");
        if (symbol.Length > 0)
            return symbol;
        if (string.IsNullOrWhiteSpace(description))
            return "";
        var flat = string.Join(' ', description.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 90 ? flat : flat[..87] + "…";
    }

    private static List<string> ListValues(List<Leaf> leaves, string parent)
    {
        return leaves
            .Where(leaf => TryListIndex(leaf.Relative, out var owner, out _) && owner == parent)
            .OrderBy(leaf => ListIndex(leaf.Relative))
            .Select(leaf => leaf.Value)
            .Where(value => value.Length > 0)
            .ToList();
    }

    private static string FirstListItem(List<Leaf> leaves, string parent)
    {
        var values = ListValues(leaves, parent);
        return values.Count == 0 ? "" : values[0];
    }

    private static string ValueOf(List<Leaf> leaves, string relative)
    {
        foreach (var leaf in leaves)
        {
            if (leaf.Relative == relative)
                return leaf.Value;
        }
        return "";
    }

    private static string Normalize(Leaf leaf)
    {
        if (leaf.Type == JTokenType.Boolean && bool.TryParse(leaf.Value, out var flag))
            return flag ? "True" : "False";
        return leaf.Value;
    }

    private static bool TryComponent(string path, out string category, out string id, out string relative)
    {
        category = "";
        id = "";
        relative = "";
        if (!path.StartsWith(ComponentHead, StringComparison.Ordinal))
            return false;
        var parts = path[ComponentHead.Length..].Split('.');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;
        category = parts[0];
        id = parts[1];
        relative = string.Join('.', parts.Skip(2));
        return relative.Length > 0;
    }

    private static bool TryListIndex(string relative, out string parent, out int index)
    {
        parent = "";
        index = -1;
        var dot = relative.LastIndexOf('.');
        if (dot <= 0 || dot == relative.Length - 1)
            return false;
        if (!int.TryParse(relative[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;
        parent = relative[..dot];
        return true;
    }

    private static int ListIndex(string relative)
    {
        TryListIndex(relative, out _, out var index);
        return index;
    }

    private static string StripIndex(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot <= 0 ? path : path[..dot];
    }

    private static bool IsSecretName(string relative)
    {
        var name = LeafName(relative);
        return name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Token", StringComparison.OrdinalIgnoreCase);
    }

    private static string LeafName(string relative)
    {
        var dot = relative.LastIndexOf('.');
        return dot < 0 ? relative : relative[(dot + 1)..];
    }

    private static string KindOf(string category) => category switch
    {
        "PLUGIN" => "Connector",
        "TILE_STUDY" => "Study",
        _ => Humanize(category)
    };

    private static string BucketOf(string category) => category switch
    {
        "PLUGIN" => "Connectors",
        "TILE_STUDY" => "Studies",
        _ => "Other"
    };

    private static int BucketRank(string bucket) => bucket switch
    {
        "Connectors" => 0,
        "Studies" => 1,
        "Application" => 2,
        _ => 3
    };

    private static int Rank(IReadOnlyList<string> order, string name)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 1000;
    }

    private static string Short(string id) => id.Length <= 6 ? id : id[..6];

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        var leaf = name.Replace('_', ' ');
        var dot = leaf.LastIndexOf('.');
        if (dot >= 0 && dot < leaf.Length - 1)
            leaf = leaf[(dot + 1)..];
        var text = new System.Text.StringBuilder(leaf.Length + 8);
        for (var i = 0; i < leaf.Length; i++)
        {
            var c = leaf[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(leaf[i - 1]) || (i + 1 < leaf.Length && char.IsLower(leaf[i + 1]))))
                text.Append(' ');
            text.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return text.ToString();
    }

    private readonly record struct Leaf(string Path, string Relative, string Value, JTokenType Type);
}
