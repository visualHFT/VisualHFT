using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;

namespace VisualHFT.Desktop.Hosting;

/// <summary>
/// Dashboard studies always emit for the ticker focused in the watchlist.
/// Extra study settings (bucket size, weighting) stay on the plugin; symbol and venue do not.
/// </summary>
public static class StudyFollow
{
    public static void MarkExplicit(IPlugin plugin)
    {
        _ = plugin;
    }

    public static bool ShouldFollow(IPlugin plugin, ISetting? settings)
    {
        _ = plugin;
        _ = settings;
        return true;
    }

    public static bool Apply(IPlugin plugin, ISetting settings, string symbol, int providerId, string providerName)
    {
        _ = plugin;
        if (string.IsNullOrEmpty(symbol) || providerId <= 0 || string.IsNullOrWhiteSpace(providerName))
            return false;

        var same = string.Equals(settings.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
            && settings.Provider?.ProviderID == providerId
            && string.Equals(settings.Provider.ProviderName, providerName, StringComparison.OrdinalIgnoreCase);
        if (same)
            return false;

        settings.Symbol = symbol;
        settings.Provider ??= new Provider();
        settings.Provider.ProviderID = providerId;
        settings.Provider.ProviderName = providerName;
        return true;
    }

    public static bool TryResolveConnector(IEnumerable<IPlugin> plugins, string providerName, out int providerId, out string resolvedName)
    {
        foreach (var plugin in plugins)
        {
            if (plugin.PluginType != ePluginType.MARKET_CONNECTOR)
                continue;
            var provider = plugin.Settings?.Provider;
            if (provider == null || provider.ProviderID <= 0 || string.IsNullOrWhiteSpace(provider.ProviderName))
                continue;
            if (!string.Equals(provider.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
                continue;
            providerId = provider.ProviderID;
            resolvedName = provider.ProviderName;
            return true;
        }

        providerId = 0;
        resolvedName = "";
        return false;
    }
}
