namespace VisualHFT.PluginManager
{
    /// <summary>
    /// One selectable study-metric for any "pick a study" surface (trigger rules, etc.).
    /// A single study yields one descriptor; a multi-study yields one per child.
    /// The parent of a multi-study is never emitted as its own descriptor — it only
    /// appears as a <see cref="GroupName"/> header derived from its children.
    /// </summary>
    public sealed class StudyDescriptor
    {
        /// <summary>
        /// Stable matching key. Equals the value stored on a trigger condition's Plugin
        /// field and the key a metric is registered under. For a single study this is the
        /// plugin's unique id; for a multi-study child it is "{parentUniqueId}|{TileTitle}"
        /// so the key carries the metric identity (TileTitle), not an incidental hash.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>Label shown to the user (the study/child TileTitle).</summary>
        public required string DisplayName { get; init; }

        /// <summary>Parent name for grouping multi-study children; null for standalone studies.</summary>
        public string? GroupName { get; init; }

        /// <summary>
        /// The study's own rich tooltip (<c>IStudy.TileToolTip</c>, b/i/br HTML — render with
        /// <c>HtmlTooltip.Html</c>). For a multi-study child this is the CHILD's own tooltip,
        /// never the parent's. Empty when the study declares none.
        /// </summary>
        public string TileToolTip { get; init; } = string.Empty;

        public string ProviderName { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;

        /// <summary>False when the owning plugin's settings are incomplete (see ISetting.IsConfigured).</summary>
        public bool IsConfigured { get; init; }

        /// <summary>
        /// When <see cref="IsConfigured"/> is false, a short user-facing reason
        /// (e.g. "No symbol selected.") for display as a tooltip on the disabled row.
        /// Null when configured.
        /// </summary>
        public string? UnavailableReason { get; init; }
    }
}
