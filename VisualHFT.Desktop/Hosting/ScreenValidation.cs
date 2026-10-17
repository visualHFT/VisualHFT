using MarketConnectors.Binance;
using VisualHFT.Desktop.Screens;
using VisualHFT.Studies.VPIN.Model;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using VisualHFT.TriggerEngine;

namespace VisualHFT.Desktop.Hosting;

public static class ScreenValidation
{
    public static int Run()
    {
        var failures = new List<string>();
        Check(failures, "depth", ValidateDepth);
        Check(failures, "flow", ValidateFlow);
        Check(failures, "reports", ValidateReports);
        Check(failures, "settings", ValidateSettings);
        Check(failures, "triggers", ValidateTriggers);
        Check(failures, "plugin-surface", ValidatePluginSurface);
        Check(failures, "aggregation", ValidateAggregation);
        Check(failures, "orders", ValidateOrders);
        Check(failures, "sessions", ValidateSessions);
        Check(failures, "study-follow", ValidateStudyFollow);
        Check(failures, "binance-futures", ValidateBinanceFutures);
        Check(failures, "vpin-floor", ValidateVpinFloor);
        Check(failures, "venue-mids", VenueMidTracker.Validate);
        Check(failures, "market-chrome", MarketChrome.Validate);
        Check(failures, "shell-chrome", ShellChrome.Validate);
        Check(failures, "session-analytics", SessionAnalytics.Validate);
        Check(failures, "blotter-paint", BlotterPaint.Validate);
        Check(failures, "ticker-search", TickerSearch.Validate);

        if (failures.Count == 0)
        {
            Console.WriteLine("screen-validation ok");
            return 0;
        }

        foreach (var failure in failures)
            Console.Error.WriteLine(failure);
        return 1;
    }

    private static void Check(List<string> failures, string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine("ok " + name);
        }
        catch (Exception ex)
        {
            failures.Add(name + ": " + ex.Message);
        }
    }

    private static void ValidateDepth()
    {
        var bids = new[]
        {
            new BookLevel(100, 2, 0.5, true),
            new BookLevel(99, 4, 0, true)
        };
        var asks = new[]
        {
            new BookLevel(101, 3, 1, false),
            new BookLevel(102, 5, 0, false)
        };
        var mirror = DepthBoard.Mirror(bids, asks, 2);
        if (mirror.Count != 2 || mirror[0].BidPrice != "100" || mirror[0].AskPrice != "101" || mirror[0].BidActive != "0.5")
            throw new InvalidOperationException("Mirror book did not pair the best bid and ask.");
        var ladder = DepthBoard.Ladder(bids, asks, 2);
        if (ladder.Count != 4 || ladder[0].Price != "102" || !ladder[^1].IsBid || ladder[^1].Price != "99")
            throw new InvalidOperationException("Ladder did not order asks above bids.");
    }

    private static void ValidateFlow()
    {
        var flow = new FlowTracker();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        flow.Add("BTC/USD", "Binance", "BTCUSDT", "BTC/USD", "Binance", 0.2, 100, 100.1, t0);
        flow.Add("BTCUSDT", "Binance", "BTCUSDT", "BTC/USD", "Binance", 0.9, 101, 101.1, t0.AddMilliseconds(5));
        flow.Add("ETH/USD", "Binance", "BTCUSDT", "BTC/USD", "Binance", 0.1, 50, 50.1, t0.AddSeconds(1));
        flow.Add("BTC/USD", "Binance", "BTCUSDT", "BTC/USD", "Binance", -0.4, 102, 102.2, t0.AddMilliseconds(20));
        var points = flow.Snapshot();
        if (points.Count != 2 || points[1].Imbalance != -0.4 || points[1].Mid != 102 || points[1].Micro != 102.2)
            throw new InvalidOperationException("Flow tracker did not apply the 10ms gate and symbol filter.");

        for (var i = 0; i < 400; i++)
            flow.Add("BTC/USD", "Binance", "BTCUSDT", "BTC/USD", "Binance", i, 100 + i, 100 + i, t0.AddSeconds(10 + i));
        if (flow.Snapshot().Count != 300)
            throw new InvalidOperationException("Flow tracker exceeded 300 points.");

        var imbalance = BookImbalance.Of(new[] { 10d, 4d }, new[] { 2d, 2d });
        if (Math.Abs(imbalance - 0.5555555555555556) > 1e-9)
            throw new InvalidOperationException("Book imbalance did not weight bid size against ask size.");
        if (BookImbalance.Of(Array.Empty<double>(), Array.Empty<double>()) != 0)
            throw new InvalidOperationException("An empty book should report zero imbalance.");

        if (Math.Abs(Microprice.Of(109.78, 1, 109.79, 1) - 109.785) > 1e-9)
            throw new InvalidOperationException("Equal sizes should collapse microprice to the simple mid.");
        var micro = Microprice.Of(109.78, 100, 109.79, 10);
        if (micro <= 109.785 || micro >= 109.79)
            throw new InvalidOperationException("More bid size should pull microprice toward the ask.");
    }

    private static void ValidateReports()
    {
        var trades = new[]
        {
            new TradePrint("BTC/USD", "Binance", 100, 2, true, DateTime.UtcNow),
            new TradePrint("BTC/USD", "Binance", 101, 1, false, DateTime.UtcNow)
        };
        var positions = new[] { new PositionSnap("BTC/USD", 1, 12.5, -2) };
        var equity = new double[] { 0, 10, 8, 15, 4 };
        var report = SessionReport.Compute(trades, positions, equity);
        if (report.TradeCount != 2 || report.Volume != 3 || report.BuyCount != 1 || report.SellCount != 1)
            throw new InvalidOperationException("Trade totals are wrong.");
        if (Math.Abs(report.AverageSize - 1.5) > 0.0001 || report.Realized != 12.5 || report.Open != -2 || report.Net != 1)
            throw new InvalidOperationException("Position totals are wrong.");
        if (Math.Abs(report.MaxDrawdown - 11) > 0.0001)
            throw new InvalidOperationException("Drawdown should be 11 (peak 15 to 4).");
    }

    private static void ValidateSettings()
    {
        var tree = SettingsTree.Parse("{\"theme\":\"dark\",\"limits\":{\"depth\":10}}");
        if (!SettingsTree.TrySet(tree, "settings.theme", "light"))
            throw new InvalidOperationException("Could not edit the theme leaf.");
        if (!SettingsTree.TrySet(tree, "settings.limits.depth", "25"))
            throw new InvalidOperationException("Could not edit a nested leaf.");
        var again = SettingsTree.Parse(SettingsTree.ToJson(tree));
        var leaves = SettingsTree.Leaves(again).ToDictionary(x => x.Path, x => x.Node.Value);
        if (leaves["settings.theme"] != "light" || leaves["settings.limits.depth"] != "25")
            throw new InvalidOperationException("Settings did not round-trip.");
        var typed = SettingsTree.Parse("{\"depth\":10,\"flag\":true}");
        if (!SettingsTree.TrySet(typed, "settings.depth", "25"))
            throw new InvalidOperationException("Could not edit a numeric leaf.");
        var json = SettingsTree.ToJson(typed);
        if (json.Contains("\"25\"") || !json.Contains("true"))
            throw new InvalidOperationException("Settings save changed a number or boolean into text.");

        var stored = SettingsTree.Parse("""
            {
              "ComponentSettings": {
                "PLUGIN": {
                  "abc": {
                    "ApiKey": "k",
                    "ApiSecret": "s",
                    "Symbols": ["tBTCUSD(BTC/USD)", "tETHUSD(ETH/USD)"],
                    "DepthLevels": 25,
                    "AggregationLevel": 0,
                    "Provider": {
                      "ProviderID": 2,
                      "ProviderCode": 2,
                      "ProviderName": "Bitfinex",
                      "Status": 0,
                      "LastUpdated": "0001-01-01T00:00:00",
                      "Tooltip": "Connecting..."
                    }
                  }
                },
                "TILE_STUDY": {
                  "def": {
                    "Symbol": "BTC/USD",
                    "UseCorrectedForm": true,
                    "BucketVolSize": 1.5,
                    "Provider": { "ProviderName": "Binance" }
                  },
                  "ghi": {
                    "Symbol": "ETH/USD",
                    "Provider": { "ProviderName": "Binance" }
                  }
                }
              }
            }
            """);
        var hints = new Dictionary<string, SettingsPluginHint>
        {
            ["abc"] = new() { Name = "Bitfinex" },
            ["def"] = new() { Name = "VPIN", Description = "Volume-synchronized probability of informed trading." },
            ["ghi"] = new() { Name = "VPIN" }
        };
        var groups = SettingsEditor.Organize(stored, hints);
        if (groups.Count != 3 || groups[0].Title != "Bitfinex" || groups[0].Bucket != "Connectors")
            throw new InvalidOperationException("Settings should open on the connector, not the raw hash.");
        if (groups[1].Title != "VPIN · BTC/USD" || groups[2].Title != "VPIN · ETH/USD")
            throw new InvalidOperationException("Two studies with the same name should be distinguished by symbol.");
        var connector = groups[0].Sections.SelectMany(section => section.Fields).ToList();
        if (connector.Any(field => field.Relative is "Provider.Status" or "Provider.LastUpdated" or "Provider.Tooltip"))
            throw new InvalidOperationException("Live session fields should not be editable settings.");
        var secret = connector.Single(field => field.Label == "API secret");
        if (secret.Kind != SettingsFieldKind.Secret)
            throw new InvalidOperationException("API secrets should be masked.");
        var symbols = connector.Single(field => field.Label == "Symbols");
        if (symbols.Kind != SettingsFieldKind.Multiline || symbols.Value != "tBTCUSD(BTC/USD)\ntETHUSD(ETH/USD)" || symbols.ListPath == null)
            throw new InvalidOperationException("Symbol lists should be one field, one instrument per line.");
        var depth = connector.Single(field => field.Label == "Depth levels");
        if (depth.Value != "25")
            throw new InvalidOperationException("Depth levels should stay a number.");
        var aggregation = connector.Single(field => field.Label == "Aggregation");
        if (aggregation.Kind != SettingsFieldKind.Choice || aggregation.Value != "0" || aggregation.Choices[0].Label != "No Aggregation")
            throw new InvalidOperationException("Aggregation should be a labeled choice.");
        if (groups[0].StatusText != "Connecting")
            throw new InvalidOperationException("Connector status should read as Connecting.");
        var advanced = groups[0].Sections.Single(section => section.Title == "Advanced");
        if (advanced.Fields.All(field => field.Label != "Venue id"))
            throw new InvalidOperationException("Venue id should stay available under Advanced.");
        var corrected = groups[1].Sections.SelectMany(section => section.Fields).Single(field => field.Label == "Corrected form");
        if (corrected.Kind != SettingsFieldKind.Bool || corrected.Value != "True")
            throw new InvalidOperationException("Boolean settings should stay booleans.");
        if (!SettingsTree.TryReplaceList(stored, symbols.ListPath, new[] { "BTC/USD" }))
            throw new InvalidOperationException("Could not replace the symbol list.");
        var rewritten = SettingsTree.ToJson(stored);
        if (!rewritten.Contains("\"BTC/USD\"") || rewritten.Contains("tETHUSD") || rewritten.Contains("\"25\"") || !rewritten.Contains("\"Tooltip\": \"Connecting...\""))
            throw new InvalidOperationException("Saving a symbol list should keep numbers, secrets, and live status intact.");
        if (!SettingsTree.TryReplaceList(stored, symbols.ListPath, Array.Empty<string>()) || !SettingsTree.ToJson(stored).Contains("[]"))
            throw new InvalidOperationException("Clearing a symbol list should keep it an array.");
    }

    private static void ValidateTriggers()
    {
        var draft = new TriggerDraft
        {
            Name = "imbalance and vpin",
            AlertInApp = true,
            AlertCooldownSeconds = 3,
            UseWebhook = true,
            WebhookUrl = "https://example.test/hook",
            WebhookMethod = "POST",
            BodyTemplate = "{\"v\":{{value}}}",
            WebhookCooldownSeconds = 30,
            WebhookCooldownUnit = TimeWindowUnit.Seconds
        };
        draft.Headers.Add(new HeaderDraft { Name = "X-Desk", Value = "alpha" });
        draft.Conditions.Add(new ConditionDraft
        {
            StudyId = "study-a",
            Operator = ConditionOperator.GreaterThan,
            Threshold = 0.7
        });
        draft.Conditions.Add(new ConditionDraft
        {
            StudyId = "study-b",
            Operator = ConditionOperator.CrossesBelow,
            Threshold = 0.2,
            WindowDuration = 3,
            WindowUnit = TimeWindowUnit.Seconds
        });

        var error = draft.Validate(new[] { "study-a", "study-b" });
        if (error != null)
            throw new InvalidOperationException(error);

        var originalPath = TriggerEngineService.TriggerEngineConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "vhft-screen-" + Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(directory, TriggerEngineService.TriggerEngineConfigFileName);
        Directory.CreateDirectory(directory);
        try
        {
            TriggerEngineService.TriggerEngineConfigFilePath = temp;
            TriggerEngineService.ClearAllRules();
            TriggerEngineService.AddOrUpdateRule(draft.ToRule());
            var loaded = TriggerEngineService.GetRules().Single();
            var roundTrip = TriggerDraft.FromRule(loaded);
            if (roundTrip.Conditions.Count != 2 || roundTrip.Conditions[1].WindowDuration != 3)
                throw new InvalidOperationException("Sustained window was not stored.");
            if (!roundTrip.AlertInApp || !roundTrip.UseWebhook || roundTrip.Headers.Count != 1 || roundTrip.Headers[0].Value != "alpha")
                throw new InvalidOperationException("Alert and webhook headers were not stored.");
            if (roundTrip.Conditions[0].Operator != ConditionOperator.GreaterThan || roundTrip.Conditions[1].Operator != ConditionOperator.CrossesBelow)
                throw new InvalidOperationException("Operators were not stored.");
        }
        finally
        {
            TriggerEngineService.TriggerEngineConfigFilePath = originalPath;
            TriggerEngineService.LoadAllRules();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static void ValidatePluginSurface()
    {
        if (PluginSurface.Describe(null) != "Metric tile. This plugin does not supply its own panel.")
            throw new InvalidOperationException("Null custom UI should stay on the metric tile.");
        if (!PluginSurface.Describe("panel").Contains("System.String"))
            throw new InvalidOperationException("A custom panel should name its runtime type.");
    }

    private static void ValidateAggregation()
    {
        var series = new BookPriceSeries { Level = AggregationLevel.Ms100 };
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var resting = new[] { new RestingLevel(100, 2, true), new RestingLevel(101, 3, false) };
        if (!series.TryAdd(t0, 99, 101, 100, resting))
            throw new InvalidOperationException("The first book sample should be kept.");
        if (series.TryAdd(t0.AddMilliseconds(40), 98, 102, 100, resting))
            throw new InvalidOperationException("A sample inside the 100ms bucket should be dropped.");
        if (!series.TryAdd(t0.AddMilliseconds(100), 97, 103, 100, resting))
            throw new InvalidOperationException("A sample on the next bucket should be kept.");
        var frame = series.Frame();
        if (frame.Bids.Count != 2 || frame.Bids[0] != 99 || frame.Asks[1] != 103)
            throw new InvalidOperationException("Bid and ask lines did not keep the first sample in each bucket.");
        if (frame.Marks.Count != 4 || frame.Marks.Any(mark => mark.Index > 1))
            throw new InvalidOperationException("Resting size should be stored on the kept samples only.");
        var quote = TopOfBook.Split(12345.67, 2, 2);
        if (quote.Whole != "12,345" || quote.Fraction != "67" || string.IsNullOrEmpty(quote.Size))
            throw new InvalidOperationException("Top of book split was " + quote.Whole + " / " + quote.Fraction);
        var scale = BookPriceScale.Of(new[] { 109.19 }, new[] { 109.20 }, new[] { 109.195 });
        if (!scale.Contains(109.19) || !scale.Contains(109.20) || scale.Contains(109.10) || scale.Contains(109.29))
            throw new InvalidOperationException("Book price scale should fit the spread, not the full ladder.");
    }

    private static void ValidateOrders()
    {
        var session = new DateTime(2026, 6, 1);
        if (!OrderBlotter.Matches(eORDERSTATUS.NEW, session, "Working", session))
            throw new InvalidOperationException("A new order should be working.");
        if (OrderBlotter.Matches(eORDERSTATUS.FILLED, session, "Working", session))
            throw new InvalidOperationException("A filled order should leave the working blotter.");
        if (!OrderBlotter.Matches(eORDERSTATUS.PARTIALFILLED, session, "Filled", session))
            throw new InvalidOperationException("A partial fill should appear in the filled blotter.");
        if (!OrderBlotter.Matches(eORDERSTATUS.CANCELED, session, "Cancelled", session))
            throw new InvalidOperationException("A canceled order should appear in the cancelled blotter.");
        if (OrderBlotter.Matches(eORDERSTATUS.NEW, session.AddDays(-1), "All", session))
            throw new InvalidOperationException("Yesterday's order should stay off today's blotter.");
        if (Math.Abs(OrderBlotter.FillPercent(1, 4) - 25) > 0.001)
            throw new InvalidOperationException("Fill percent should be filled quantity over order quantity.");
        var average = OrderBlotter.AverageFill(0, new[] { (100m, 1m), (110m, 1m) });
        if (Math.Abs(average - 105) > 0.001)
            throw new InvalidOperationException("Average fill should weight execution prices.");
    }

    private static void ValidateSessions()
    {
        if (!SessionControl.TryNext(eSESSIONSTATUS.CONNECTED, out var connect) || connect)
            throw new InvalidOperationException("A connected venue should offer disconnect.");
        if (!SessionControl.TryNext(eSESSIONSTATUS.DISCONNECTED_FAILED, out connect) || !connect)
            throw new InvalidOperationException("A failed venue should offer connect.");
        if (SessionControl.TryNext((eSESSIONSTATUS)99, out _))
            throw new InvalidOperationException("An unknown session status should not toggle.");
        if (SessionControl.Color(eSESSIONSTATUS.CONNECTING) != "#F5C16C" || SessionControl.Color(eSESSIONSTATUS.DISCONNECTED) != "#FF6B6B")
            throw new InvalidOperationException("Session colors do not match the Windows status lights.");
    }

    private static void ValidateStudyFollow()
    {
        var plugin = new StubPlugin();
        var settings = new StubSettings();
        if (!StudyFollow.ShouldFollow(plugin, settings))
            throw new InvalidOperationException("An unconfigured study should follow the symbol on screen.");
        StudyFollow.Apply(plugin, settings, "BTC/USD", 7, "Coinbase");
        if (settings.Symbol != "BTC/USD" || settings.Provider.ProviderID != 7)
            throw new InvalidOperationException("The study was not pointed at the selected venue.");
        settings.Symbol = "ETH/USD";
        StudyFollow.Apply(plugin, settings, "BTC/USD", 7, "Coinbase");
        if (settings.Symbol != "BTC/USD")
            throw new InvalidOperationException("A study must follow the ticker selected in the watchlist.");
        var tip = StudyTooltip.Plain("The <b>VPIN</b> tile measures imbalance.<br/>Zero is balanced.");
        if (!tip.Contains("VPIN") || tip.Contains("<b>") || !tip.Contains('\n'))
            throw new InvalidOperationException("Study tooltips should drop HTML tags and keep line breaks.");
    }

    private static void ValidateBinanceFutures()
    {
        var parsed = BinanceFuturesFeed.Parse(new[] { "ETHUSDT(ETH/USD)", " SOLUSDT(SOL/USD) ", "XRPUSDT(XRP/USD)" });
        if (parsed["ETHUSDT"] != "ETH/USD" || parsed["SOLUSDT"] != "SOL/USD" || parsed["XRPUSDT"] != "XRP/USD")
            throw new InvalidOperationException("Futures symbols were not normalized.");
        if (BinanceFuturesFeed.DepthLimit(10) != 10 || BinanceFuturesFeed.DepthLimit(25) != 20)
            throw new InvalidOperationException("Futures depth limit was not snapped to a Binance value.");
        if (BinanceFuturesFeed.UpdateInterval(100) != 100)
            throw new InvalidOperationException("Futures update interval should stay at 100ms.");

        var bridge = BinanceFuturesFeed.Decide(100, 90, 110, 0, synced: false);
        var stale = BinanceFuturesFeed.Decide(100, 80, 90, 0, synced: false);
        var chained = BinanceFuturesFeed.Decide(110, 111, 120, 110, synced: true);
        var gap = BinanceFuturesFeed.Decide(110, 111, 120, 100, synced: true);
        if (bridge != BinanceFuturesFeed.BookStep.Apply || stale != BinanceFuturesFeed.BookStep.Drop
            || chained != BinanceFuturesFeed.BookStep.Apply || gap != BinanceFuturesFeed.BookStep.Gap)
            throw new InvalidOperationException("Futures book sync did not bridge the snapshot and chain updates.");
    }

    private static void ValidateVpinFloor()
    {
        if (VpinVolumeFloor.Effective(1, 1) != 20)
            throw new InvalidOperationException("A one-lot bucket must be raised to twenty prints.");
        if (VpinVolumeFloor.Effective(50, 1) != 50)
            throw new InvalidOperationException("A bucket that already holds enough prints should stay put.");
        var sizer = new VpinVolumeFloor();
        for (var i = 0; i < VpinVolumeFloor.SampleCount; i++)
            sizer.Add(i < 20 ? 0.5m : 1.5m);
        if (!sizer.IsReady || sizer.MedianPrint != 1.0m || sizer.Recommend(1) != 20)
            throw new InvalidOperationException("VPIN must size the bucket from the median print, not the factory default of 1.");
    }

    private sealed class StubSettings : VisualHFT.UserSettings.ISetting
    {
        public string Symbol { get; set; } = "";
        public Provider Provider { get; set; } = new();
        public AggregationLevel AggregationLevel { get; set; }
    }

    private sealed class StubPlugin : IPlugin
    {
        public string Name { get; set; } = "stub";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public ePluginType PluginType => ePluginType.STUDY;
        public VisualHFT.UserSettings.ISetting Settings { get; set; } = new StubSettings();
        public ePluginStatus Status { get; set; }
        public Action CloseSettingWindow { get; set; } = null!;
        public eLicenseLevel RequiredLicenseLevel => eLicenseLevel.COMMUNITY;
        public string GetPluginUniqueID() => "stub";
        public object GetUISettings() => null!;
        public object GetCustomUI() => null!;
    }
}
