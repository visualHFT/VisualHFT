namespace VisualHFT.Desktop.Screens;

public readonly record struct NotificationNote(
    int Id,
    string Title,
    string Message,
    DateTime Time,
    string Type,
    bool IsRead);

public readonly record struct NotificationGroup(
    string Title,
    int Count,
    bool HasUnread,
    IReadOnlyList<NotificationNote> Items);

/// <summary>
/// Groups the notification inbox the way Windows <c>vmNotifications</c> collapses repeats:
/// same title is one group, newest message first, groups ordered by the newest item.
/// </summary>
public static class NotificationInbox
{
    public static int UnreadCount(IEnumerable<NotificationNote>? notes)
    {
        if (notes == null)
            return 0;
        var unread = 0;
        foreach (var note in notes)
        {
            if (!note.IsRead)
                unread++;
        }
        return unread;
    }

    public static IReadOnlyList<NotificationGroup> Group(IEnumerable<NotificationNote>? notes)
    {
        var buckets = new Dictionary<string, List<NotificationNote>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var note in notes ?? Array.Empty<NotificationNote>())
        {
            var title = note.Title ?? "";
            if (!buckets.TryGetValue(title, out var items))
            {
                items = new List<NotificationNote>();
                buckets[title] = items;
                order.Add(title);
            }
            items.Add(note);
        }

        var groups = new List<NotificationGroup>(buckets.Count);
        foreach (var title in order)
        {
            var items = buckets[title];
            items.Sort(NewestFirst);
            groups.Add(new NotificationGroup(title, items.Count, items.Exists(n => !n.IsRead), items));
        }

        groups.Sort(NewestGroupFirst);
        return groups;
    }

    private static int NewestFirst(NotificationNote left, NotificationNote right)
    {
        var byTime = right.Time.CompareTo(left.Time);
        return byTime != 0 ? byTime : right.Id.CompareTo(left.Id);
    }

    private static int NewestGroupFirst(NotificationGroup left, NotificationGroup right)
    {
        var leftTime = left.Items.Count == 0 ? DateTime.MinValue : left.Items[0].Time;
        var rightTime = right.Items.Count == 0 ? DateTime.MinValue : right.Items[0].Time;
        var byTime = rightTime.CompareTo(leftTime);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Title, right.Title);
    }
}

public readonly record struct PluginCard(string Name, string Kind, string Version, string Status);

public readonly record struct PluginCluster<T>(string Kind, IReadOnlyList<T> Items);

/// <summary>
/// Plugin Manager grouping: connectors, studies, everything else (including multi-study parents).
/// </summary>
public static class PluginCatalog
{
    public const string Connector = "MARKET_CONNECTOR";
    public const string Study = "STUDY";
    public const string Other = "OTHER";

    private static readonly string[] KindOrder = { Connector, Study, Other };

    public static string Bucket(string? kind)
    {
        var value = (kind ?? "").Trim();
        if (value.Equals(Connector, StringComparison.OrdinalIgnoreCase))
            return Connector;
        if (value.Equals(Study, StringComparison.OrdinalIgnoreCase))
            return Study;
        return Other;
    }

    public static IReadOnlyList<PluginCluster<PluginCard>> Group(IEnumerable<PluginCard>? plugins)
    {
        var buckets = new Dictionary<string, List<PluginCard>>(StringComparer.Ordinal);
        foreach (var kind in KindOrder)
            buckets[kind] = new List<PluginCard>();

        foreach (var plugin in plugins ?? Array.Empty<PluginCard>())
            buckets[Bucket(plugin.Kind)].Add(plugin);

        var clusters = new List<PluginCluster<PluginCard>>();
        foreach (var kind in KindOrder)
        {
            if (buckets[kind].Count > 0)
                clusters.Add(new PluginCluster<PluginCard>(kind, buckets[kind]));
        }
        return clusters;
    }

    public static IReadOnlyList<PluginCluster<T>> Group<T>(IEnumerable<T>? plugins, Func<T, string> kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var buckets = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        foreach (var name in KindOrder)
            buckets[name] = new List<T>();

        foreach (var plugin in plugins ?? Array.Empty<T>())
            buckets[Bucket(kind(plugin))].Add(plugin);

        var clusters = new List<PluginCluster<T>>();
        foreach (var name in KindOrder)
        {
            if (buckets[name].Count > 0)
                clusters.Add(new PluginCluster<T>(name, buckets[name]));
        }
        return clusters;
    }
}

public readonly record struct StudyCard(string OwnerName, string Title, bool IsChild);

public readonly record struct StudyCluster(string Owner, bool IsGroup, IReadOnlyList<StudyCard> Children);

/// <summary>
/// Metric-tile grouping for <c>IMultiStudy</c> parents: several children share an owner and render as one group.
/// A lone study stays ungrouped.
/// </summary>
public static class StudyClusters
{
    public static IReadOnlyList<StudyCluster> Group(IEnumerable<StudyCard>? studies)
    {
        var buckets = new List<(string Owner, List<StudyCard> Children)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var study in studies ?? Array.Empty<StudyCard>())
        {
            var owner = study.OwnerName ?? "";
            if (!index.TryGetValue(owner, out var i))
            {
                i = buckets.Count;
                index[owner] = i;
                buckets.Add((owner, new List<StudyCard>()));
            }
            buckets[i].Children.Add(study);
        }

        var clusters = new List<StudyCluster>(buckets.Count);
        foreach (var (owner, children) in buckets)
            clusters.Add(new StudyCluster(owner, children.Count > 1, children));
        return clusters;
    }
}

public readonly record struct SettingsOutlineRow(
    string Path,
    int Indent,
    string Name,
    string Value,
    bool IsContainer);

/// <summary>
/// Flatten a <see cref="SettingsNode"/> tree into indented rows for a TreeView-like list.
/// Does not mutate the node; <see cref="SettingsTree.Parse"/> / <see cref="SettingsTree.TrySet"/> stay authoritative.
/// </summary>
public static class SettingsOutline
{
    public static IReadOnlyList<SettingsOutlineRow> Flatten(SettingsNode? root)
    {
        var rows = new List<SettingsOutlineRow>();
        if (root == null)
            return rows;
        Walk(root, root.Name ?? "", 0, rows);
        return rows;
    }

    private static void Walk(SettingsNode node, string path, int indent, List<SettingsOutlineRow> rows)
    {
        rows.Add(new SettingsOutlineRow(
            path,
            indent,
            node.Name ?? "",
            node.Value ?? "",
            node.IsContainer));
        foreach (var child in node.Children)
        {
            var childPath = string.IsNullOrEmpty(path) ? child.Name : path + "." + child.Name;
            Walk(child, childPath, indent + 1, rows);
        }
    }
}

public static class ShellChrome
{
    public static void Validate()
    {
        ValidateNotifications();
        ValidatePlugins();
        ValidateStudies();
        ValidateSettings();
    }

    private static void ValidateNotifications()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);
        var notes = new[]
        {
            new NotificationNote(1, "Feed error", "first", t0, "ERROR", false),
            new NotificationNote(2, "Trigger", "fired", t1, "TRIGGER_ACTION", false),
            new NotificationNote(3, "Feed error", "latest", t2, "ERROR", true),
            new NotificationNote(4, "Feed error", "middle", t1, "WARNING", false)
        };

        if (NotificationInbox.UnreadCount(notes) != 3)
            throw new InvalidOperationException("Unread badge should count unread notes, not groups.");
        if (NotificationInbox.UnreadCount(null) != 0)
            throw new InvalidOperationException("A missing inbox has zero unread.");

        var groups = NotificationInbox.Group(notes);
        if (groups.Count != 2)
            throw new InvalidOperationException("Notifications should group by title only.");
        if (groups[0].Title != "Feed error" || groups[0].Count != 3 || !groups[0].HasUnread)
            throw new InvalidOperationException("The newest title should lead, with a count and unread flag.");
        if (groups[0].Items[0].Id != 3 || groups[0].Items[1].Id != 4 || groups[0].Items[2].Id != 1)
            throw new InvalidOperationException("Items inside a group must stay newest-first.");
        if (groups[1].Title != "Trigger" || groups[1].Count != 1 || !groups[1].HasUnread || groups[1].Items[0].Id != 2)
            throw new InvalidOperationException("A single later title should follow as its own group.");
        if (NotificationInbox.Group(Array.Empty<NotificationNote>()).Count != 0)
            throw new InvalidOperationException("An empty inbox should group to nothing.");
    }

    private static void ValidatePlugins()
    {
        var plugins = new[]
        {
            new PluginCard("Binance", "MARKET_CONNECTOR", "1.0", "STARTED"),
            new PluginCard("VPIN", "STUDY", "1.1", "STARTED"),
            new PluginCard("Market Resilience", "MULTI_STUDY", "2.0", "STOPPED"),
            new PluginCard("WebSocket", "market_connector", "1.2", "STARTED"),
            new PluginCard("Unknown", "UNKNOWN", "", "LOADED")
        };

        var clusters = PluginCatalog.Group(plugins);
        if (clusters.Count != 3)
            throw new InvalidOperationException("Plugins should cluster into connector, study, and other.");
        if (clusters[0].Kind != PluginCatalog.Connector || clusters[0].Items.Count != 2)
            throw new InvalidOperationException("Market connectors should be the first cluster.");
        if (clusters[0].Items[0].Name != "Binance" || clusters[0].Items[1].Name != "WebSocket")
            throw new InvalidOperationException("Connector order should follow input order.");
        if (clusters[1].Kind != PluginCatalog.Study || clusters[1].Items.Count != 1 || clusters[1].Items[0].Name != "VPIN")
            throw new InvalidOperationException("Studies should be their own cluster.");
        if (clusters[2].Kind != PluginCatalog.Other || clusters[2].Items.Count != 2)
            throw new InvalidOperationException("Multi-study and unknown kinds should land in OTHER.");
        if (clusters[2].Items[0].Kind != "MULTI_STUDY" || clusters[2].Items[1].Kind != "UNKNOWN")
            throw new InvalidOperationException("OTHER should keep the original plugin kind on the card.");
        if (PluginCatalog.Bucket("STUDY") != PluginCatalog.Study || PluginCatalog.Bucket(null) != PluginCatalog.Other)
            throw new InvalidOperationException("Kind buckets did not normalize STUDY vs OTHER.");
        if (PluginCatalog.Group(Array.Empty<PluginCard>()).Count != 0)
            throw new InvalidOperationException("An empty catalog should omit empty clusters.");

        var generic = PluginCatalog.Group(plugins, card => card.Kind);
        if (generic.Count != 3 || generic[0].Items[0].Name != "Binance")
            throw new InvalidOperationException("The generic catalog grouper should match PluginCard grouping.");
    }

    private static void ValidateStudies()
    {
        var studies = new[]
        {
            new StudyCard("Market Resilience", "Bias", true),
            new StudyCard("Market Resilience", "Recovery", true),
            new StudyCard("VPIN", "VPIN", false),
            new StudyCard("LOB Imbalance", "LOB Imbalance", true)
        };

        var clusters = StudyClusters.Group(studies);
        if (clusters.Count != 3)
            throw new InvalidOperationException("Study clustering should keep owner order.");
        if (clusters[0].Owner != "Market Resilience" || !clusters[0].IsGroup || clusters[0].Children.Count != 2)
            throw new InvalidOperationException("A multi-study parent with several children is a group.");
        if (clusters[0].Children[0].Title != "Bias" || clusters[0].Children[1].Title != "Recovery")
            throw new InvalidOperationException("Grouped children should keep input order.");
        if (clusters[1].Owner != "VPIN" || clusters[1].IsGroup || clusters[1].Children.Count != 1)
            throw new InvalidOperationException("A standalone study must stay ungrouped.");
        if (clusters[2].Owner != "LOB Imbalance" || clusters[2].IsGroup)
            throw new InvalidOperationException("A single child of a parent is not a group.");
        if (StudyClusters.Group(Array.Empty<StudyCard>()).Count != 0)
            throw new InvalidOperationException("No studies should produce no clusters.");
    }

    private static void ValidateSettings()
    {
        var tree = SettingsTree.Parse("{\"theme\":\"dark\",\"limits\":{\"depth\":10}}");
        var rows = SettingsOutline.Flatten(tree);
        if (rows.Count != 4)
            throw new InvalidOperationException("The outline should keep containers and leaves.");
        if (rows[0].Path != "settings" || rows[0].Indent != 0 || rows[0].Name != "settings" || !rows[0].IsContainer)
            throw new InvalidOperationException("The settings root should be indent 0.");
        if (rows[1].Path != "settings.theme" || rows[1].Indent != 1 || rows[1].Name != "theme" || rows[1].Value != "dark" || rows[1].IsContainer)
            throw new InvalidOperationException("A leaf should keep its path, indent, and value.");
        if (rows[2].Path != "settings.limits" || rows[2].Indent != 1 || !rows[2].IsContainer)
            throw new InvalidOperationException("A nested object should appear as a container.");
        if (rows[3].Path != "settings.limits.depth" || rows[3].Indent != 2 || rows[3].Value != "10" || rows[3].IsContainer)
            throw new InvalidOperationException("A nested leaf should indent one more than its parent.");

        if (!SettingsTree.TrySet(tree, "settings.theme", "light"))
            throw new InvalidOperationException("Flatten must not break SettingsTree.TrySet.");
        if (!SettingsTree.TrySet(tree, "settings.limits.depth", "25"))
            throw new InvalidOperationException("Flatten must not break nested SettingsTree.TrySet.");

        var again = SettingsTree.Parse(SettingsTree.ToJson(tree));
        var leaves = SettingsTree.Leaves(again).ToDictionary(x => x.Path, x => x.Node.Value);
        if (leaves["settings.theme"] != "light" || leaves["settings.limits.depth"] != "25")
            throw new InvalidOperationException("Settings did not round-trip after outlining.");

        var outlineLeaves = SettingsOutline.Flatten(again).Where(row => !row.IsContainer).ToList();
        if (outlineLeaves.Count != leaves.Count || outlineLeaves.Any(row => !leaves.ContainsKey(row.Path)))
            throw new InvalidOperationException("Outline leaf paths must match SettingsTree.Leaves.");

        var empty = SettingsOutline.Flatten(SettingsTree.Parse("{}"));
        if (empty.Count != 1 || !empty[0].IsContainer || empty[0].Indent != 0)
            throw new InvalidOperationException("An empty object should still outline the root container.");
        if (SettingsOutline.Flatten(null).Count != 0)
            throw new InvalidOperationException("A null tree should flatten to no rows.");

        var typed = SettingsTree.Parse("{\"depth\":10,\"flag\":true}");
        if (!SettingsTree.TrySet(typed, "settings.depth", "25"))
            throw new InvalidOperationException("Could not edit a numeric leaf after outlining.");
        var json = SettingsTree.ToJson(typed);
        if (json.Contains("\"25\"", StringComparison.Ordinal) || !json.Contains("true", StringComparison.Ordinal))
            throw new InvalidOperationException("Settings save changed a number or boolean into text.");
    }
}
