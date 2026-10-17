using System.Globalization;
using Newtonsoft.Json.Linq;
using VisualHFT.PluginManager;
using VisualHFT.TriggerEngine;
using VisualHFT.TriggerEngine.Actions;

namespace VisualHFT.Desktop.Screens;

public readonly record struct BookLevel(double Price, double Size, double ActiveSize, bool IsBid);

public sealed class DepthMirrorRow
{
    public string BidSize { get; init; } = "";
    public string BidActive { get; init; } = "";
    public string BidPrice { get; init; } = "";
    public string AskPrice { get; init; } = "";
    public string AskActive { get; init; } = "";
    public string AskSize { get; init; } = "";
}

public sealed class DepthLadderRow
{
    public string Price { get; init; } = "";
    public string Size { get; init; } = "";
    public string Active { get; init; } = "";
    public bool IsBid { get; init; }
    public bool IsAsk => !IsBid;
}

public static class DepthBoard
{
    public static IReadOnlyList<DepthMirrorRow> Mirror(IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, int rows)
    {
        var bidSide = bids.Where(b => b.IsBid).OrderByDescending(b => b.Price).Take(rows).ToList();
        var askSide = asks.Where(a => !a.IsBid).OrderBy(a => a.Price).Take(rows).ToList();
        var count = Math.Max(bidSide.Count, askSide.Count);
        var result = new List<DepthMirrorRow>(count);
        for (var i = 0; i < count; i++)
        {
            var bid = i < bidSide.Count ? bidSide[i] : (BookLevel?)null;
            var ask = i < askSide.Count ? askSide[i] : (BookLevel?)null;
            result.Add(new DepthMirrorRow
            {
                BidSize = Format(bid?.Size),
                BidActive = Active(bid?.ActiveSize),
                BidPrice = Format(bid?.Price),
                AskPrice = Format(ask?.Price),
                AskActive = Active(ask?.ActiveSize),
                AskSize = Format(ask?.Size)
            });
        }
        return result;
    }

    public static IReadOnlyList<DepthLadderRow> Ladder(IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks, int rows)
    {
        var askSide = asks.Where(a => !a.IsBid).OrderByDescending(a => a.Price).Take(rows).ToList();
        var bidSide = bids.Where(b => b.IsBid).OrderByDescending(b => b.Price).Take(rows).ToList();
        return askSide.Concat(bidSide).Select(level => new DepthLadderRow
        {
            Price = Format(level.Price),
            Size = Format(level.Size),
            Active = level.ActiveSize == 0 ? "" : Format(level.ActiveSize),
            IsBid = level.IsBid
        }).ToList();
    }

    private static string Format(double? value) =>
        value is double number ? number.ToString("0.########", CultureInfo.InvariantCulture) : "";

    private static string Active(double? value) =>
        value is double number && number != 0 ? Format(number) : "";
}

public static class StudyTooltip
{
    public static string Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        var text = html
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase);
        text = Tag.Replace(text, "");
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim();
    }

    private static readonly System.Text.RegularExpressions.Regex Tag =
        new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);
}

public static class BookImbalance
{
    /// <summary>
    /// Signed size pressure across the visible book: (bid − ask) / (bid + ask).
    /// Matches Windows OrderFlowAnalysis.Calculate_OrderImbalance. Returns 0 when empty.
    /// </summary>
    public static double Of(IEnumerable<BookLevel> bids, IEnumerable<BookLevel> asks)
    {
        var bid = bids.Where(level => level.IsBid).Sum(level => level.Size);
        var ask = asks.Where(level => !level.IsBid).Sum(level => level.Size);
        var total = bid + ask;
        return total <= 0 ? 0 : (bid - ask) / total;
    }

    public static double Of(IEnumerable<double> bidSizes, IEnumerable<double> askSizes)
    {
        var bid = bidSizes.Sum();
        var ask = askSizes.Sum();
        var total = bid + ask;
        return total <= 0 ? 0 : (bid - ask) / total;
    }
}

/// <summary>
/// Size-weighted top-of-book mid: (ask × bid size + bid × ask size) / (bid size + ask size).
/// More resting size on the bid pulls this toward the ask. Equal sizes collapse to (B+A)/2.
/// </summary>
public static class Microprice
{
    public static double Of(double bid, double bidSize, double ask, double askSize)
    {
        if (bid <= 0 || ask <= 0)
            return 0;
        var total = bidSize + askSize;
        if (total <= 0)
            return (bid + ask) / 2.0;
        return (ask * bidSize + bid * askSize) / total;
    }
}

public readonly record struct FlowPoint(DateTime Time, double Imbalance, double Mid, double Micro);

public sealed class FlowTracker
{
    private readonly List<FlowPoint> _points = new();
    private readonly object _gate = new();

    public void Add(string symbol, string provider, string native, string normalized, string selectedProvider, double imbalance, double mid, double micro, DateTime time)
    {
        if (!TickerSearch.SameInstrument(symbol, native, normalized))
            return;
        if (!string.IsNullOrEmpty(selectedProvider) && selectedProvider != "All"
            && !string.Equals(provider, selectedProvider, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (_points.Count > 0 && (time - _points[^1].Time).TotalMilliseconds <= 10)
                return;
            _points.Add(new FlowPoint(time, imbalance, mid, micro));
            if (_points.Count > 300)
                _points.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _points.Clear();
    }

    public IReadOnlyList<FlowPoint> Snapshot()
    {
        lock (_gate)
            return _points.ToList();
    }
}

public readonly record struct TradePrint(string Symbol, string Provider, double Price, double Size, bool? IsBuy, DateTime Time);
public readonly record struct PositionSnap(string Symbol, double Net, double Realized, double Open);

public sealed class SessionReport
{
    public int TradeCount { get; init; }
    public double Volume { get; init; }
    public int BuyCount { get; init; }
    public int SellCount { get; init; }
    public double AverageSize { get; init; }
    public double Realized { get; init; }
    public double Open { get; init; }
    public double Net { get; init; }
    public double MaxDrawdown { get; init; }
    public IReadOnlyList<double> Equity { get; init; } = Array.Empty<double>();

    public static SessionReport Compute(IReadOnlyList<TradePrint> trades, IReadOnlyList<PositionSnap> positions, IReadOnlyList<double> equity)
    {
        var volume = trades.Sum(t => t.Size);
        return new SessionReport
        {
            TradeCount = trades.Count,
            Volume = volume,
            BuyCount = trades.Count(t => t.IsBuy == true),
            SellCount = trades.Count(t => t.IsBuy == false),
            AverageSize = trades.Count == 0 ? 0 : volume / trades.Count,
            Realized = positions.Sum(p => p.Realized),
            Open = positions.Sum(p => p.Open),
            Net = positions.Sum(p => p.Net),
            MaxDrawdown = Drawdown(equity),
            Equity = equity
        };
    }

    public static double Drawdown(IReadOnlyList<double> equity)
    {
        if (equity.Count == 0)
            return 0;
        var peak = equity[0];
        var max = 0d;
        foreach (var value in equity)
        {
            if (value > peak)
                peak = value;
            var drop = peak - value;
            if (drop > max)
                max = drop;
        }
        return max;
    }
}

public sealed class SettingsNode
{
    public string Name { get; init; } = "";
    public string? Value { get; set; }
    public JToken? Source { get; set; }
    public bool IsContainer { get; init; }
    public bool IsArray { get; set; }
    public List<SettingsNode> Children { get; } = new();
    public bool IsLeaf => !IsContainer;
}

public static class SettingsTree
{
    public static SettingsNode Parse(string json)
    {
        var token = JToken.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return FromToken("settings", token);
    }

    public static string ToJson(SettingsNode root)
    {
        var token = ToToken(root);
        return token.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    public static IReadOnlyList<(string Path, SettingsNode Node)> Leaves(SettingsNode root)
    {
        var list = new List<(string, SettingsNode)>();
        Walk(root, root.Name, list);
        return list;
    }

    public static bool TrySet(SettingsNode root, string path, string value)
    {
        foreach (var (leafPath, node) in Leaves(root))
        {
            if (leafPath == path && node.IsLeaf)
            {
                node.Value = value;
                node.Source = Coerce(node.Source, value);
                return true;
            }
        }
        return false;
    }

    public static bool TryReplaceList(SettingsNode root, string path, IReadOnlyList<string> values)
    {
        var node = Find(root, path);
        if (node == null || !node.IsContainer)
            return false;
        node.IsArray = true;
        node.Children.Clear();
        for (var i = 0; i < values.Count; i++)
        {
            node.Children.Add(new SettingsNode
            {
                Name = i.ToString(CultureInfo.InvariantCulture),
                Value = values[i],
                Source = new JValue(values[i])
            });
        }
        return true;
    }

    private static SettingsNode? Find(SettingsNode node, string path)
    {
        var parts = path.Split('.');
        if (parts.Length == 0 || parts[0] != node.Name)
            return null;
        var current = node;
        for (var i = 1; i < parts.Length; i++)
        {
            SettingsNode? next = null;
            foreach (var child in current.Children)
            {
                if (child.Name == parts[i])
                {
                    next = child;
                    break;
                }
            }
            if (next == null)
                return null;
            current = next;
        }
        return current;
    }

    private static JToken Coerce(JToken? source, string value)
    {
        var type = source?.Type ?? JTokenType.String;
        if (type == JTokenType.Integer && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return new JValue(integer);
        if (type == JTokenType.Float && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return new JValue(number);
        if (type == JTokenType.Boolean && bool.TryParse(value, out var flag))
            return new JValue(flag);
        if (type == JTokenType.Null && value.Length == 0)
            return JValue.CreateNull();
        return new JValue(value);
    }

    private static void Walk(SettingsNode node, string path, List<(string, SettingsNode)> list)
    {
        if (node.IsLeaf)
        {
            list.Add((path, node));
            return;
        }
        foreach (var child in node.Children)
            Walk(child, path + "." + child.Name, list);
    }

    private static SettingsNode FromToken(string name, JToken token)
    {
        if (token is JObject obj)
        {
            var node = new SettingsNode { Name = name, IsContainer = true };
            foreach (var property in obj.Properties())
                node.Children.Add(FromToken(property.Name, property.Value));
            return node;
        }

        if (token is JArray array)
        {
            var node = new SettingsNode { Name = name, IsContainer = true, IsArray = true };
            for (var i = 0; i < array.Count; i++)
                node.Children.Add(FromToken(i.ToString(CultureInfo.InvariantCulture), array[i]));
            return node;
        }

        return new SettingsNode
        {
            Name = name,
            Value = token.Type == JTokenType.Null ? "" : token.ToString(),
            Source = token.DeepClone()
        };
    }

    private static JToken ToToken(SettingsNode node)
    {
        if (node.IsLeaf)
            return node.Source ?? new JValue(node.Value ?? "");
        if (node.IsArray || (node.Children.Count > 0 && node.Children.All(c => int.TryParse(c.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))))
        {
            var array = new JArray();
            foreach (var child in node.Children.OrderBy(c => int.Parse(c.Name, CultureInfo.InvariantCulture)))
                array.Add(ToToken(child));
            return array;
        }
        var obj = new JObject();
        foreach (var child in node.Children)
            obj[child.Name] = ToToken(child);
        return obj;
    }
}

public sealed class ConditionDraft
{
    public string StudyId { get; set; } = "";
    public ConditionOperator Operator { get; set; } = ConditionOperator.GreaterThan;
    public double Threshold { get; set; }
    public int WindowDuration { get; set; }
    public TimeWindowUnit WindowUnit { get; set; } = TimeWindowUnit.Seconds;
}

public sealed class HeaderDraft
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class TriggerDraft
{
    public long? RuleId { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public List<ConditionDraft> Conditions { get; } = new();
    public bool AlertInApp { get; set; } = true;
    public int AlertCooldownSeconds { get; set; } = 5;
    public bool UseWebhook { get; set; }
    public string WebhookUrl { get; set; } = "";
    public string WebhookMethod { get; set; } = "POST";
    public string BodyTemplate { get; set; } = "{\"rule\":\"{{rulename}}\",\"metric\":\"{{metric}}\",\"value\":{{value}},\"timestamp\":\"{{timestamp}}\"}";
    public int WebhookCooldownSeconds { get; set; } = 5;
    public TimeWindowUnit WebhookCooldownUnit { get; set; } = TimeWindowUnit.Seconds;
    public List<HeaderDraft> Headers { get; } = new();

    public static TriggerDraft FromRule(TriggerRule rule)
    {
        var draft = new TriggerDraft
        {
            RuleId = rule.RuleID,
            Name = rule.Name ?? "",
            IsEnabled = rule.IsEnabled
        };
        foreach (var condition in rule.Condition ?? new List<TriggerCondition>())
        {
            draft.Conditions.Add(new ConditionDraft
            {
                StudyId = condition.Plugin ?? "",
                Operator = condition.Operator,
                Threshold = condition.Threshold,
                WindowDuration = condition.Window?.Duration ?? 0,
                WindowUnit = condition.Window?.Unit ?? TimeWindowUnit.Seconds
            });
        }
        var alert = rule.Actions?.FirstOrDefault(a => a.Type == ActionType.UIAlert);
        draft.AlertInApp = alert != null;
        draft.AlertCooldownSeconds = alert?.CooldownDuration ?? 5;
        var hook = rule.Actions?.FirstOrDefault(a => a.Type == ActionType.RestApi && a.RestApi != null);
        if (hook?.RestApi != null)
        {
            draft.UseWebhook = true;
            draft.WebhookUrl = hook.RestApi.Url ?? "";
            draft.WebhookMethod = string.IsNullOrWhiteSpace(hook.RestApi.Method) ? "POST" : hook.RestApi.Method;
            draft.BodyTemplate = hook.RestApi.BodyTemplate ?? draft.BodyTemplate;
            draft.WebhookCooldownSeconds = hook.CooldownDuration;
            draft.WebhookCooldownUnit = hook.CooldownUnit;
            foreach (var header in hook.RestApi.Headers ?? new Dictionary<string, string>())
                draft.Headers.Add(new HeaderDraft { Name = header.Key, Value = header.Value });
        }
        return draft;
    }

    public string? Validate(IReadOnlyCollection<string> knownStudyIds)
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "Name is required.";
        if (Conditions.Count == 0)
            return "Add at least one condition.";
        foreach (var condition in Conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.StudyId))
                return "Each condition needs a study.";
            if (knownStudyIds.Count > 0 && !knownStudyIds.Contains(condition.StudyId))
                return "A condition references a study that is not loaded.";
            if (condition.WindowDuration < 0)
                return "Window duration cannot be negative.";
        }
        if (!AlertInApp && !UseWebhook)
            return "Choose an in-app alert, a webhook, or both.";
        if (UseWebhook)
        {
            if (!Uri.TryCreate(WebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return "Webhook URL must be an absolute http or https address.";
            if (string.IsNullOrWhiteSpace(BodyTemplate))
                return "Webhook body is required.";
        }
        return null;
    }

    public TriggerRule ToRule()
    {
        var actions = new List<TriggerAction>();
        if (AlertInApp)
        {
            actions.Add(new TriggerAction
            {
                ActionID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = ActionType.UIAlert,
                CooldownDuration = Math.Max(0, AlertCooldownSeconds),
                CooldownUnit = TimeWindowUnit.Seconds
            });
        }
        if (UseWebhook)
        {
            actions.Add(new TriggerAction
            {
                ActionID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1,
                Type = ActionType.RestApi,
                CooldownDuration = Math.Max(0, WebhookCooldownSeconds),
                CooldownUnit = WebhookCooldownUnit,
                RestApi = new RestApiAction
                {
                    Url = WebhookUrl.Trim(),
                    Method = string.IsNullOrWhiteSpace(WebhookMethod) ? "POST" : WebhookMethod.Trim(),
                    BodyTemplate = BodyTemplate,
                    Headers = Headers
                        .Where(h => !string.IsNullOrWhiteSpace(h.Name))
                        .ToDictionary(h => h.Name.Trim(), h => h.Value ?? "")
                }
            });
        }

        return new TriggerRule
        {
            RuleID = RuleId ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Name = Name.Trim(),
            IsEnabled = IsEnabled,
            Condition = Conditions.Select((condition, index) => new TriggerCondition
            {
                ConditionID = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + index,
                Plugin = condition.StudyId,
                Metric = "",
                Operator = condition.Operator,
                Threshold = condition.Threshold,
                Window = condition.WindowDuration > 0
                    ? new TimeWindow { Duration = condition.WindowDuration, Unit = condition.WindowUnit }
                    : null
            }).ToList(),
            Actions = actions
        };
    }
}

public static class PluginSurface
{
    public static string Describe(object? customUi) =>
        customUi == null
            ? "Metric tile. This plugin does not supply its own panel."
            : "Custom panel: " + customUi.GetType().FullName;
}
