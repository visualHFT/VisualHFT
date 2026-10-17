using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Studies;
using VisualHFT.DataRetriever;
using VisualHFT.Desktop.Hosting;
using VisualHFT.Desktop.Screens;
using VisualHFT.Enums;
using VisualHFT.Helpers;
using VisualHFT.Model;
using VisualHFT.PluginManager;
using HostPlugins = VisualHFT.PluginManager.PluginManager;
using VisualHFT.TriggerEngine;
using VisualHFT.UserSettings;

namespace VisualHFT.Desktop.ViewModels;

public sealed class TerminalViewModel : ObservableObject, IDisposable
{
    private const int LadderDepth = 24;
    private readonly DispatcherTimer _timer;
    private readonly object _gate = new();
    private readonly MarketIngress _ingress = new();
    private IReadOnlyList<TradePrint> _visibleTrades = Array.Empty<TradePrint>();
    private readonly Dictionary<string, List<double>> _midHistory = new();
    private readonly Dictionary<string, PositionRow> _positions = new();
    private readonly FlowTracker _flow = new();
    private readonly BookPriceSeries _prices = new();
    private readonly SpreadTracker _spreads = new();
    private readonly Dictionary<string, DateTime> _midStamp = new();
    private readonly List<OrderSnap> _orders = new();
    private AggregationLevel _aggregation = AggregationLevel.Ms100;
    private readonly List<double> _equity = new();
    private readonly Dictionary<string, PositionSnap> _positionSnaps = new();
    private SettingsNode? _settingsRoot;
    private bool _dirty = true;
    private bool _disposed;
    private string _statusText = "Starting…";
    private string _selectedSymbol = "";
    private string _selectedProvider = "All";
    private string _midText = "—";
    private string _spreadText = "—";
    private string _lastTradeText = "—";
    private IBrush _lastTradeBrush = Brushes.LightGray;
    private string _reportText = "Waiting for trades and positions.";
    private string _settingsQuery = "";
    private string _settingsNavStatus = "No settings file yet.";
    private string _settingsBanner = "";
    private string _catalogLine = "No settings file yet.";
    private bool _showSettingsSaved;
    private bool _settingsDirty;
    private bool _settingsSavedNote;
    private SettingsGroupRow? _selectedSettings;
    private string _aggregationLabel = AggregationChoices.Label(AggregationLevel.Ms100);
    private string _orderFilter = "Working";
    private string _bidRest = "";
    private string _bidDecimals = "";
    private string _bidLast = "";
    private string _bidSize = "";
    private string _askRest = "";
    private string _askDecimals = "";
    private string _askLast = "";
    private string _askSize = "";
    private double _imbalance;
    private BookLayout _bookLayout = BookLayout.SideBySide;
    private DateTime _sessionDate = DateTime.Now.Date;
    private IReadOnlyList<ChartSeries> _spreadSeries = Array.Empty<ChartSeries>();
    private ObservableCollection<PluginGroupRow> _pluginGroups = new();
    private ObservableCollection<StudyGroupRow> _studyGroups = new();
    private ObservableCollection<SettingsOutlineRow> _settingsOutline = new();
    private int _unread;
    private BookPriceFrame _priceFrame = new();
    private IReadOnlyList<ChartSeries> _flowImbalance = Array.Empty<ChartSeries>();
    private IReadOnlyList<ChartSeries> _flowMid = Array.Empty<ChartSeries>();
    private string _flowImbalanceText = "—";
    private string _flowMidText = "—";
    private IReadOnlyList<ChartSeries> _equitySeries = Array.Empty<ChartSeries>();
    private string _selectedExchange = "";
    private string _tickerQuery = "";
    private string _tickerStatus = "Select an exchange, then type a ticker.";
    private string _catalogStatus = "";
    private bool _showTickerHits;
    private bool _hasTickerFocus;
    private bool _hasWatchlist;
    private readonly Dictionary<string, IPlugin> _exchangePlugins = new(StringComparer.OrdinalIgnoreCase);
    private List<TickerHit> _catalog = new();
    private int _catalogVersion;
    private int _focusVersion;
    private bool _tickersBound;
    private bool _marketIdle = true;
    private string _focusNative = "";
    private string _focusNormalized = "";
    private string _focusVenue = "";

    public TerminalViewModel()
    {
        Symbols = new ObservableCollection<string>();
        Providers = new ObservableCollection<string> { "All" };
        Asks = new ObservableCollection<BookRow>();
        Bids = new ObservableCollection<BookRow>();
        Venues = new ObservableCollection<VenueRow>();
        Studies = new ObservableCollection<StudyRow>();
        StudyGroups = new ObservableCollection<StudyGroupRow>();
        Positions = new ObservableCollection<PositionRow>();
        Notifications = new ObservableCollection<NotificationRow>();
        Plugins = new ObservableCollection<PluginRow>();
        PluginGroups = new ObservableCollection<PluginGroupRow>();
        Rules = new ObservableCollection<RuleRow>();
        DepthMirror = new ObservableCollection<DepthMirrorRow>();
        DepthLadder = new ObservableCollection<DepthLadderRow>();
        SettingsConnectors = new ObservableCollection<SettingsGroupRow>();
        SettingsStudies = new ObservableCollection<SettingsGroupRow>();
        SettingsApplication = new ObservableCollection<SettingsGroupRow>();
        SettingsOther = new ObservableCollection<SettingsGroupRow>();
        Sessions = new ObservableCollection<ProviderSessionRow>();
        TradeTape = new ObservableCollection<TradeTapeRow>();
        Orders = new ObservableCollection<OrderRow>();
        AggregationLevels = new ObservableCollection<string>(AggregationChoices.Labels);
        Exchanges = new ObservableCollection<string>();
        Watchlist = new ObservableCollection<WatchItemRow>();
        TickerHits = new ObservableCollection<TickerHit>();
        DepthBids = Array.Empty<DepthPoint>();
        DepthAsks = Array.Empty<DepthPoint>();
        MidSeries = Array.Empty<ChartSeries>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => Flush();
    }

    public ObservableCollection<string> Symbols { get; }
    public ObservableCollection<string> Providers { get; }
    public ObservableCollection<BookRow> Asks { get; }
    public ObservableCollection<BookRow> Bids { get; }
    public ObservableCollection<VenueRow> Venues { get; }
    public ObservableCollection<StudyRow> Studies { get; }
    public ObservableCollection<StudyGroupRow> StudyGroups { get; }
    public ObservableCollection<PositionRow> Positions { get; }
    public ObservableCollection<NotificationRow> Notifications { get; }
    public ObservableCollection<PluginRow> Plugins { get; }
    public ObservableCollection<PluginGroupRow> PluginGroups { get; }
    public ObservableCollection<RuleRow> Rules { get; }
    public ObservableCollection<DepthMirrorRow> DepthMirror { get; }
    public ObservableCollection<DepthLadderRow> DepthLadder { get; }
    public ObservableCollection<SettingsGroupRow> SettingsConnectors { get; }
    public ObservableCollection<SettingsGroupRow> SettingsStudies { get; }
    public ObservableCollection<SettingsGroupRow> SettingsApplication { get; }
    public ObservableCollection<SettingsGroupRow> SettingsOther { get; }
    public ObservableCollection<ProviderSessionRow> Sessions { get; }
    public ObservableCollection<TradeTapeRow> TradeTape { get; }
    public ObservableCollection<OrderRow> Orders { get; }
    public ObservableCollection<string> AggregationLevels { get; }
    public ObservableCollection<string> Exchanges { get; }
    public ObservableCollection<WatchItemRow> Watchlist { get; }
    public ObservableCollection<TickerHit> TickerHits { get; }

    public IReadOnlyList<DepthPoint> DepthBids { get; private set; }
    public IReadOnlyList<DepthPoint> DepthAsks { get; private set; }
    public IReadOnlyList<ChartSeries> MidSeries { get; private set; }
    public IReadOnlyList<ChartSeries> FlowImbalance { get => _flowImbalance; private set => Set(ref _flowImbalance, value); }
    public IReadOnlyList<ChartSeries> FlowMid { get => _flowMid; private set => Set(ref _flowMid, value); }
    public string FlowImbalanceText { get => _flowImbalanceText; private set => Set(ref _flowImbalanceText, value); }
    public string FlowMidText { get => _flowMidText; private set => Set(ref _flowMidText, value); }
    public IReadOnlyList<ChartSeries> EquitySeries { get => _equitySeries; private set => Set(ref _equitySeries, value); }
    public IReadOnlyList<ChartSeries> SpreadSeries { get => _spreadSeries; private set => Set(ref _spreadSeries, value); }
    public BookPriceFrame PriceFrame { get => _priceFrame; private set => Set(ref _priceFrame, value); }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string SelectedExchange
    {
        get => _selectedExchange;
        set
        {
            if (!Set(ref _selectedExchange, value ?? ""))
                return;
            TickerQuery = "";
            _ = LoadTickerCatalogAsync();
        }
    }
    public string TickerQuery
    {
        get => _tickerQuery;
        set
        {
            if (!Set(ref _tickerQuery, value ?? ""))
                return;
            FilterTickerHits();
        }
    }
    public string TickerStatus { get => _tickerStatus; private set => Set(ref _tickerStatus, value); }
    public string CatalogStatus { get => _catalogStatus; private set => Set(ref _catalogStatus, value); }
    public bool ShowTickerHits { get => _showTickerHits; private set => Set(ref _showTickerHits, value); }
    public bool HasTickerFocus { get => _hasTickerFocus; private set => Set(ref _hasTickerFocus, value); }
    public bool HasWatchlist { get => _hasWatchlist; private set => Set(ref _hasWatchlist, value); }
    public string SelectedSymbol
    {
        get => _selectedSymbol;
        set
        {
            if (!Set(ref _selectedSymbol, value))
                return;
            _flow.Clear();
            _prices.Clear();
            _spreads.Clear();
            _dirty = true;
        }
    }
    public string SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value))
                return;
            _ingress.SetProviderFilter(value);
            _flow.Clear();
            _prices.Clear();
            _spreads.Clear();
            _dirty = true;
        }
    }
    public string SelectedAggregation
    {
        get => _aggregationLabel;
        set
        {
            if (string.IsNullOrEmpty(value) || !AggregationChoices.TryGet(value, out var level))
                return;
            if (!Set(ref _aggregationLabel, value))
                return;
            _aggregation = level;
            _prices.Level = level;
            _spreads.Level = level;
            lock (_gate)
            {
                _midHistory.Clear();
                _midStamp.Clear();
            }
            _dirty = true;
        }
    }
    public string OrderFilter
    {
        get => _orderFilter;
        set
        {
            if (!Set(ref _orderFilter, value))
                return;
            PublishOrders();
        }
    }
    public string BidRest { get => _bidRest; private set => Set(ref _bidRest, value); }
    public string BidDecimals { get => _bidDecimals; private set => Set(ref _bidDecimals, value); }
    public string BidLast { get => _bidLast; private set => Set(ref _bidLast, value); }
    public string BidSize { get => _bidSize; private set => Set(ref _bidSize, value); }
    public string AskRest { get => _askRest; private set => Set(ref _askRest, value); }
    public string AskDecimals { get => _askDecimals; private set => Set(ref _askDecimals, value); }
    public string AskLast { get => _askLast; private set => Set(ref _askLast, value); }
    public string AskSize { get => _askSize; private set => Set(ref _askSize, value); }
    public double Imbalance { get => _imbalance; private set => Set(ref _imbalance, value); }
    public BookLayout BookLayout { get => _bookLayout; private set => Set(ref _bookLayout, value); }
    public bool ShowSideBook => BookLayout == BookLayout.SideBySide;
    public bool ShowMirrorBook => BookLayout == BookLayout.Mirror;
    public bool ShowLadderBook => BookLayout == BookLayout.Ladder;
    public string BookLayoutLabel => BookLayout.ToString();

    public void CycleBookLayout()
    {
        BookLayout = BookLayoutCycle.Next(BookLayout);
        Raise(nameof(ShowSideBook));
        Raise(nameof(ShowMirrorBook));
        Raise(nameof(ShowLadderBook));
        Raise(nameof(BookLayoutLabel));
    }

    public DateTime SessionDate { get => _sessionDate; set { if (Set(ref _sessionDate, value.Date)) PublishOrders(); } }
    public DateTimeOffset? SessionPicked
    {
        get
        {
            var day = _sessionDate.Date;
            return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
        }
        set
        {
            if (value == null)
                return;
            SessionDate = value.Value.Date;
            Raise(nameof(SessionPicked));
        }
    }
    public int UnreadCount { get => _unread; private set => Set(ref _unread, value); }
    public string MidText { get => _midText; private set => Set(ref _midText, value); }
    public string SpreadText { get => _spreadText; private set => Set(ref _spreadText, value); }
    public string LastTradeText { get => _lastTradeText; private set => Set(ref _lastTradeText, value); }
    public IBrush LastTradeBrush { get => _lastTradeBrush; private set => Set(ref _lastTradeBrush, value); }
    public string ReportText { get => _reportText; private set => Set(ref _reportText, value); }
    public string SettingsQuery
    {
        get => _settingsQuery;
        set
        {
            if (!Set(ref _settingsQuery, value))
                return;
            ApplySettingsFilter(_selectedSettings?.Key);
        }
    }
    public string SettingsNavStatus { get => _settingsNavStatus; private set => Set(ref _settingsNavStatus, value); }
    public string SettingsBanner { get => _settingsBanner; private set => Set(ref _settingsBanner, value); }
    public bool ShowSettingsSaved { get => _showSettingsSaved; private set => Set(ref _showSettingsSaved, value); }
    public bool SettingsDirty { get => _settingsDirty; private set => Set(ref _settingsDirty, value); }
    public string SaveSettingsLabel => SettingsDirty ? "Save changes" : "Save";
    public string SettingsFilePath { get; } = SettingsFile();
    public bool HasConnectors => SettingsConnectors.Any(group => group.IsShown);
    public bool HasStudies => SettingsStudies.Any(group => group.IsShown);
    public bool HasApplication => SettingsApplication.Any(group => group.IsShown);
    public bool HasOther => SettingsOther.Any(group => group.IsShown);
    public bool HasAnySettings => SettingsConnectors.Count + SettingsStudies.Count + SettingsApplication.Count + SettingsOther.Count > 0;
    public bool HasSelectedSettings => _selectedSettings != null;
    public bool ShowSettingsEmpty => !HasAnySettings;
    public bool ShowSettingsNoMatch => HasAnySettings && _selectedSettings == null;
    public string SelectedSettingsEyebrow => _selectedSettings?.Eyebrow ?? "";
    public string SelectedSettingsTitle => _selectedSettings?.Title ?? "";
    public string SelectedSettingsSubtitle => _selectedSettings?.Summary ?? "";
    public bool SelectedHasSubtitle => _selectedSettings?.HasSummary ?? false;
    public bool SelectedHasStatus => _selectedSettings?.HasStatus ?? false;
    public string SelectedStatusText => _selectedSettings?.StatusText ?? "";
    public IBrush SelectedStatusBrush => _selectedSettings?.StatusBrush ?? Brushes.Transparent;
    public ObservableCollection<SettingsSectionRow> SelectedSections => _selectedSettings?.Sections ?? EmptySections;

    public SettingsGroupRow? SelectedSettings
    {
        get => _selectedSettings;
        set
        {
            if (ReferenceEquals(_selectedSettings, value))
                return;
            if (_selectedSettings != null)
                _selectedSettings.IsSelected = false;
            _selectedSettings = value;
            if (_selectedSettings != null)
                _selectedSettings.IsSelected = true;
            RaiseSelection();
        }
    }

    private static readonly ObservableCollection<SettingsSectionRow> EmptySections = new();

    public async Task StartAsync()
    {
        var baseDir = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);
        var config = Path.Combine(baseDir, "log4net.config");
        if (File.Exists(config))
            log4net.Config.XmlConfigurator.Configure(new FileInfo(config));

        HelperOrderBook.Instance.Subscribe(OnOrderBook);
        HelperTrade.Instance.Subscribe(OnTrade);
        HelperPosition.Instance.Subscribe(OnPosition);
        HelperProvider.Instance.OnDataReceived += OnProviderEvent;
        HelperProvider.Instance.OnStatusChanged += OnProviderEvent;
        HelperNotificationManager.Instance.NotificationAdded += OnNotificationEvent;

        _timer.Start();
        StatusText = "Loading plugins…";
        LicenseManager.Instance.LoadFromKeygen();
        TriggerEngineService.LoadAllRules();
        _ = TriggerEngineService.StartBackgroundWorkerAsync(CancellationToken.None);

        await Task.Run(HostPlugins.LoadPlugins);
        BindTickerSearch();
        BindStudies();
        RefreshPlugins();
        RefreshRules();
        LoadSettingsTree();
        StatusText = HostPlugins.AllPlugins.Count.ToString(CultureInfo.InvariantCulture) + " plugins loaded. Connecting venues…";
        _ = ConnectPluginsAsync();
    }

    private async Task ConnectPluginsAsync()
    {
        try
        {
            foreach (var plugin in HostPlugins.AllPlugins)
            {
                if (plugin.PluginType == ePluginType.MARKET_CONNECTOR)
                    continue;
                try
                {
                    await HostPlugins.StartPluginAsync(plugin);
                }
                catch (Exception ex)
                {
                    plugin.Status = ePluginStatus.STOPPED_FAILED;
                    HelperNotificationManager.Instance.AddNotification(
                        plugin.Name,
                        "Plugin failed to Start.",
                        HelprNorificationManagerTypes.ERROR,
                        HelprNorificationManagerCategories.PLUGINS,
                        ex);
                }
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText = "Study start failed: " + ex.Message);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            BindTickerSearch();
            BindStudies();
            RefreshPlugins();
            RefreshSessionChrome();
            var running = HostPlugins.AllPlugins.Count(p => p.Status == ePluginStatus.STARTED);
            StatusText = HostPlugins.AllPlugins.Count.ToString(CultureInfo.InvariantCulture)
                + " plugins loaded, "
                + running.ToString(CultureInfo.InvariantCulture)
                + " running";
        });
    }

    public void RefreshPlugins()
    {
        var existing = Plugins.ToDictionary(p => p.Plugin);
        foreach (var plugin in HostPlugins.AllPlugins)
        {
            if (!existing.TryGetValue(plugin, out var row))
            {
                row = new PluginRow(plugin, this);
                Plugins.Add(row);
            }
            else
            {
                row.Refresh();
            }
        }
        RebuildPluginGroups();
    }

    private void RebuildPluginGroups()
    {
        PluginGroups.Clear();
        foreach (var cluster in PluginCatalog.Group(Plugins, plugin => plugin.Kind))
            PluginGroups.Add(new PluginGroupRow(cluster.Kind, cluster.Items));
    }

    public void RefreshRules()
    {
        Rules.Clear();
        foreach (var rule in TriggerEngineService.GetRules().OrderBy(r => r.Name))
            Rules.Add(new RuleRow(rule, this));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        HelperOrderBook.Instance.Unsubscribe(OnOrderBook);
        HelperTrade.Instance.Unsubscribe(OnTrade);
        HelperPosition.Instance.Unsubscribe(OnPosition);
        HelperProvider.Instance.OnDataReceived -= OnProviderEvent;
        HelperProvider.Instance.OnStatusChanged -= OnProviderEvent;
        HelperNotificationManager.Instance.NotificationAdded -= OnNotificationEvent;
    }

    private void BindStudies()
    {
        foreach (var plugin in HostPlugins.AllPlugins)
        {
            if (plugin is IMultiStudy multi && multi.Studies != null)
            {
                foreach (var child in multi.Studies)
                    AddStudy(child, plugin);
            }
            else if (plugin is IStudy study)
            {
                AddStudy(study, plugin);
            }
        }
        RebuildStudyGroups();
    }

    private void AddStudy(IStudy study, IPlugin owner)
    {
        if (Studies.Any(row => ReferenceEquals(row.Study, study)))
            return;
        Studies.Add(new StudyRow(study, owner, this));
    }

    private void RebuildStudyGroups()
    {
        StudyGroups.Clear();
        var cards = Studies.Select(row => new StudyCard(row.Owner.Name, row.Title, row.Owner is IMultiStudy)).ToList();
        foreach (var cluster in StudyClusters.Group(cards))
        {
            var rows = Studies.Where(row => row.Owner.Name == cluster.Owner).ToList();
            if (rows.Count == 0)
                continue;
            StudyGroups.Add(new StudyGroupRow(cluster.Owner, cluster.IsGroup, rows[0].Owner, rows));
        }
    }

    private void OnOrderBook(OrderBook book) => _ingress.AddBook(book);

    private void OnTrade(Trade trade) => _ingress.AddTrade(trade);

    private void OnPosition(Position position, Order? _, Order? __) => _ingress.AddPosition(position);

    private void OnProviderEvent(object? _, VisualHFT.Model.Provider? provider) => _ingress.AddProvider(provider);

    private void OnNotificationEvent(object? _, ErrorNotificationEventArgs args) =>
        _ingress.AddNotification(args?.Notification);

    internal void MarkDirty() => _dirty = true;

    internal bool Matches(string symbol, string provider)
    {
        if (string.IsNullOrEmpty(_focusNative) && string.IsNullOrEmpty(_focusNormalized))
            return false;
        if (!TickerSearch.SameInstrument(symbol, _focusNative, _focusNormalized))
            return false;
        return string.IsNullOrEmpty(provider)
            || string.Equals(provider, _focusVenue, StringComparison.OrdinalIgnoreCase);
    }

    private void RememberFocus(WatchItemRow? row)
    {
        _focusNative = row?.Native ?? "";
        _focusNormalized = row?.Normalized ?? "";
        _focusVenue = row?.Venue ?? "";
        _ingress.SetFocus(_focusNative, _focusNormalized, _focusVenue);
    }

    internal WatchItemRow? FocusedWatch() => Watchlist.FirstOrDefault(row => row.IsFocused);

    internal string FocusCaption()
    {
        var focus = FocusedWatch();
        return focus == null ? "" : focus.Venue + "  " + focus.Native + "  " + focus.KindLabel;
    }

    private void ApplyNotifications(IReadOnlyList<NotificationUpdate> updates)
    {
        if (updates.Count == 0)
            return;
        foreach (var update in updates)
        {
            Notifications.Insert(0, new NotificationRow
            {
                Id = update.Id,
                Title = update.Title,
                Message = update.Message,
                When = update.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Timestamp = update.Timestamp,
                Type = update.Type.ToString(),
                Tone = Tone(update.Type),
                IsRead = update.IsRead
            });
        }
        while (Notifications.Count > MarketIngress.MaxNotifications)
            Notifications.RemoveAt(Notifications.Count - 1);
        UnreadCount = Notifications.Count(item => !item.IsRead);
    }

    private static IBrush Tone(HelprNorificationManagerTypes type) => type switch
    {
        HelprNorificationManagerTypes.ERROR => Brushes.IndianRed,
        HelprNorificationManagerTypes.WARNING => Brushes.Goldenrod,
        HelprNorificationManagerTypes.TRIGGER_ACTION => Brushes.DeepSkyBlue,
        _ => Brushes.LightGray
    };

    private void ApplyProviders(IReadOnlyList<ProviderUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (!Providers.Contains(update.Name))
                Providers.Add(update.Name);
            var row = Sessions.FirstOrDefault(session => session.Code == update.Code);
            if (row == null)
            {
                row = new ProviderSessionRow(update.Code);
                Sessions.Add(row);
            }
            row.Apply(update.Name, update.Status, update.Tooltip, update.Plugin);
        }
        if (updates.Count > 0)
            RefreshSessionChrome();
    }

    private void ApplyPositions(IReadOnlyList<PositionUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (!_positions.TryGetValue(update.Symbol, out var row))
            {
                row = new PositionRow();
                _positions[update.Symbol] = row;
                Positions.Add(row);
            }
            row.Symbol = update.Symbol;
            row.NetText = update.Net.ToString("0.####", CultureInfo.InvariantCulture);
            row.BuyText = update.Buy.ToString("0.####", CultureInfo.InvariantCulture);
            row.SellText = update.Sell.ToString("0.####", CultureInfo.InvariantCulture);
            row.ExposureText = update.Exposure.ToString("0.####", CultureInfo.InvariantCulture);
            row.WorkingBuyText = update.WorkingBuy.ToString("0.####", CultureInfo.InvariantCulture);
            row.WorkingSellText = update.WorkingSell.ToString("0.####", CultureInfo.InvariantCulture);
            row.TotalText = update.Total.ToString("0.####", CultureInfo.InvariantCulture);
            row.RealizedText = update.Realized.ToString("0.####", CultureInfo.InvariantCulture);
            row.OpenText = update.Open.ToString("0.####", CultureInfo.InvariantCulture);
            row.UpdatedText = update.Updated.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            row.PlBrush = BrushFrom(BlotterPaint.PositionFill(update.Total));
            row.OpenBrush = BrushFrom(BlotterPaint.PositionFill(update.Open));
            _positionSnaps[update.Symbol] = new PositionSnap(update.Symbol, update.Net, update.Realized, update.Open);
            _equity.Add(_positionSnaps.Values.Sum(p => p.Realized + p.Open));
            if (_equity.Count > 500)
                _equity.RemoveAt(0);
            _orders.RemoveAll(order => order.Symbol == update.Symbol);
            _orders.AddRange(update.Orders);
        }
        if (updates.Count > 0)
            PublishOrders();
    }

    private void ApplyLatestTrade(TradePrint? trade)
    {
        if (trade == null)
            return;
        var print = trade.Value;
        var focus = FocusedWatch();
        var name = focus == null ? print.Provider + " " + print.Symbol : focus.Venue + " " + focus.Native;
        LastTradeText = name + "  " + print.Price.ToString("0.########", CultureInfo.InvariantCulture)
            + " × " + print.Size.ToString("0.####", CultureInfo.InvariantCulture);
        LastTradeBrush = print.IsBuy == false ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }

    private void Flush()
    {
        var batch = _ingress.Drain();
        if (!batch.HasWork && !_dirty)
        {
            foreach (var plugin in Plugins)
                plugin.Refresh();
            return;
        }

        _dirty = false;
        foreach (var study in Studies)
            study.PublishPending();
        _visibleTrades = batch.Trades;
        ApplyNotifications(batch.Notifications);
        ApplyProviders(batch.Providers);
        ApplyPositions(batch.Positions);

        var books = batch.Books;
        Dictionary<string, List<double>> history;
        lock (_gate)
            history = _midHistory.ToDictionary(p => p.Key, p => p.Value.ToList());

        if (!string.IsNullOrEmpty(_focusNative) && !Symbols.Contains(_focusNative))
            Symbols.Add(_focusNative);
        var focus = FocusedWatch();
        if (focus == null)
        {
            if (!_marketIdle)
                ClearMarket(null);
            _marketIdle = true;
            PublishTape();
            ApplyLatestTrade(batch.LatestTrade);
            PriceFrame = _prices.Frame();
            PublishFlow();
            return;
        }

        var forSymbol = books.Where(book => Matches(book.Symbol, book.Provider)).ToList();
        var picked = forSymbol.OrderByDescending(b => b.UpdatedUtc).FirstOrDefault();

        if (picked == null)
        {
            ClearMarket(focus);
            _marketIdle = true;
            PublishTape();
            ApplyLatestTrade(batch.LatestTrade);
            PriceFrame = _prices.Frame();
            PublishFlow();
            return;
        }

        _marketIdle = false;

        if (focus != null)
            TickerStatus = focus.Venue + "  " + focus.Native + "  " + focus.KindLabel;

        FollowStudies(picked.Provider, picked.Symbol);
        foreach (var study in Studies)
            study.RefreshContext();
        RecordBookSeries(picked);

        var decimals = Math.Clamp(picked.Decimals, 0, 8);
        var priceFormat = "F" + decimals;
        MidText = picked.Mid.ToString(priceFormat, CultureInfo.InvariantCulture);
        SpreadText = picked.Spread.ToString(priceFormat, CultureInfo.InvariantCulture);
        var bestBid = picked.Bids.OrderByDescending(level => level.Price).FirstOrDefault();
        var bestAsk = picked.Asks.OrderBy(level => level.Price).FirstOrDefault();
        var bidQuote = QuoteFace.Split(bestBid.Price, bestBid.Size, decimals);
        var askQuote = QuoteFace.Split(bestAsk.Price, bestAsk.Size, decimals);
        BidRest = bidQuote.Rest;
        BidDecimals = bidQuote.NextTwo;
        BidLast = bidQuote.LastDecimal;
        BidSize = bidQuote.Size;
        AskRest = askQuote.Rest;
        AskDecimals = askQuote.NextTwo;
        AskLast = askQuote.LastDecimal;
        AskSize = askQuote.Size;
        Imbalance = picked.Imbalance;
        var spreads = _spreads.Values();
        SpreadSeries = spreads.Count < 2
            ? Array.Empty<ChartSeries>()
            : new[] { new ChartSeries { Name = "Spread", Values = spreads, Color = Color.Parse("#F5C16C") } };
        FillLadder(Asks, picked.Asks.OrderByDescending(x => x.Price).Take(LadderDepth).ToList(), isBid: false);
        FillLadder(Bids, picked.Bids.OrderByDescending(x => x.Price).Take(LadderDepth).ToList(), isBid: true);
        ReplaceDepth(picked);

        DepthBids = Cumulative(picked.Bids.OrderByDescending(x => x.Price));
        DepthAsks = Cumulative(picked.Asks.OrderBy(x => x.Price));
        Raise(nameof(DepthBids));
        Raise(nameof(DepthAsks));

        Venues.Clear();
        foreach (var book in forSymbol.OrderBy(b => b.Provider))
        {
            Venues.Add(new VenueRow
            {
                Provider = book.Provider,
                MidText = book.Mid.ToString(priceFormat, CultureInfo.InvariantCulture),
                SpreadText = book.Spread.ToString(priceFormat, CultureInfo.InvariantCulture)
            });
        }

        var series = new List<ChartSeries>();
        var palette = new[] { Color.Parse("#3DDC84"), Color.Parse("#61AFFE"), Color.Parse("#F5C16C"), Color.Parse("#FF6B6B"), Color.Parse("#C792EA") };
        var index = 0;
        foreach (var book in forSymbol.OrderBy(b => b.Provider))
        {
            var key = book.Provider + "|" + book.Symbol;
            if (!history.TryGetValue(key, out var points) || points.Count < 2)
                continue;
            series.Add(new ChartSeries
            {
                Name = book.Provider,
                Values = points,
                Color = palette[index % palette.Length]
            });
            index++;
        }
        MidSeries = series;
        Raise(nameof(MidSeries));

        foreach (var plugin in Plugins)
            plugin.Refresh();
        PublishFlow();
        PublishReport();
        ApplyLatestTrade(batch.LatestTrade);
        PublishTape();
        PriceFrame = _prices.Frame();
    }

    public void ToggleProvider(ProviderSessionRow row)
    {
        if (row.Plugin is not IDataRetriever retriever)
        {
            StatusText = row.Name + " has no market connector to connect.";
            return;
        }

        if (!SessionControl.TryNext(row.Status, out var connect))
            return;

        _ = Task.Run(async () =>
        {
            if (connect)
                await retriever.StartAsync();
            else
                await retriever.StopAsync();
        });
    }

    public void MarkNotificationsRead()
    {
        foreach (var row in Notifications)
        {
            row.IsRead = true;
            HelperNotificationManager.Instance.MarkAsRead(row.Id);
        }
        UnreadCount = 0;
    }

    public void ClearNotifications()
    {
        Notifications.Clear();
        UnreadCount = 0;
        HelperNotificationManager.Instance.ClearNotifications();
    }

    private void PublishTape()
    {
        var trades = _visibleTrades
            .Where(trade => Matches(trade.Symbol, trade.Provider))
            .Reverse()
            .Take(400)
            .ToList();

        TradeTape.Clear();
        foreach (var trade in trades)
            TradeTape.Add(TradeTapeRow.From(trade));
    }

    private void PublishOrders()
    {
        var rows = _orders
            .Where(order => Matches(order.Symbol, order.Provider)
                && OrderBlotter.Matches(order.StatusCode, order.Created, OrderFilter, SessionDate))
            .OrderByDescending(order => order.Created)
            .Take(200)
            .Select(OrderRow.From)
            .ToList();
        Orders.Clear();
        foreach (var row in rows)
            Orders.Add(row);
    }

    public void RetargetStudies()
    {
        var focus = FocusedWatch();
        if (focus == null)
            return;
        FollowStudies(focus.Venue, LiveSymbol(focus) ?? focus.Normalized);
    }

    private void FollowStudies(string providerName, string symbol)
    {
        if (!HasTickerFocus || string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(providerName))
            return;
        if (!StudyFollow.TryResolveConnector(HostPlugins.AllPlugins, providerName, out var providerId, out var resolvedName))
            return;

        foreach (var row in Studies)
        {
            var settings = row.Owner.Settings;
            if (settings == null)
            {
                row.RefreshContext();
                continue;
            }

            StudyFollow.Apply(row.Owner, settings, symbol, providerId, resolvedName);
            row.RefreshContext();
        }
    }

    private void RecordBookSeries(BookSnapshot picked)
    {
        var now = DateTime.UtcNow;
        var key = picked.Provider + "|" + picked.Symbol;
        lock (_gate)
        {
            if (!_midHistory.TryGetValue(key, out var history))
            {
                history = new List<double>();
                _midHistory[key] = history;
            }
            var span = _aggregation.ToTimeSpan();
            var inBucket = _midStamp.TryGetValue(key, out var last)
                && span > TimeSpan.Zero
                && now - last < span;
            if (!inBucket)
            {
                history.Add(picked.Mid);
                _midStamp[key] = now;
                if (history.Count > 240)
                    history.RemoveAt(0);
            }
        }

        var bid = picked.Bids.Count == 0 ? 0 : picked.Bids.Max(level => level.Price);
        var ask = picked.Asks.Count == 0 ? 0 : picked.Asks.Min(level => level.Price);
        var resting = picked.Bids.Select(level => new RestingLevel(level.Price, level.Size, true))
            .Concat(picked.Asks.Select(level => new RestingLevel(level.Price, level.Size, false)))
            .ToList();
        _prices.TryAdd(now, bid, ask, picked.Mid, resting);
        _spreads.TryAdd(now, picked.Spread);
        _flow.Add(picked.Symbol, picked.Provider, _focusNative, _focusNormalized, _focusVenue, picked.Imbalance, picked.Mid, picked.Micro, now);
    }

    private void ReplaceDepth(BookSnapshot picked)
    {
        var bids = picked.Bids.Select(level => new BookLevel(level.Price, level.Size, level.ActiveSize, true)).ToList();
        var asks = picked.Asks.Select(level => new BookLevel(level.Price, level.Size, level.ActiveSize, false)).ToList();
        DepthMirror.Clear();
        foreach (var row in DepthBoard.Mirror(bids, asks, LadderDepth))
            DepthMirror.Add(row);
        DepthLadder.Clear();
        foreach (var row in DepthBoard.Ladder(bids, asks, LadderDepth))
            DepthLadder.Add(row);
    }

    private void PublishFlow()
    {
        var points = _flow.Snapshot();
        if (points.Count == 0)
            FlowImbalanceText = "—";
        else
        {
            var last = points[^1].Imbalance;
            var lean = last > 0.05 ? "bid pressure" : last < -0.05 ? "ask pressure" : "balanced";
            FlowImbalanceText = last.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) + "  " + lean;
        }

        FlowImbalance = points.Count < 2
            ? Array.Empty<ChartSeries>()
            : new[]
            {
                new ChartSeries
                {
                    Name = "Imbalance",
                    Values = points.Select(p => p.Imbalance).ToList(),
                    Color = Color.Parse("#F5C16C"),
                    AxisMin = -1,
                    AxisMax = 1,
                    ZeroLine = true
                }
            };
        FlowMid = points.Count < 2
            ? Array.Empty<ChartSeries>()
            : new[]
            {
                new ChartSeries { Name = "Mid (B+A)/2", Values = points.Select(p => p.Mid).ToList(), Color = Color.Parse("#61AFFE") },
                new ChartSeries { Name = "Microprice", Values = points.Select(p => p.Micro).ToList(), Color = Color.Parse("#C084FC") }
            };
        if (points.Count == 0)
            FlowMidText = "—";
        else
        {
            var last = points[^1];
            var format = "0.######";
            FlowMidText = "mid " + last.Mid.ToString(format, CultureInfo.InvariantCulture)
                + "   micro " + last.Micro.ToString(format, CultureInfo.InvariantCulture);
        }
    }

    private void PublishReport()
    {
        var trades = _visibleTrades.Where(t => Matches(t.Symbol, t.Provider)).ToList();
        var report = SessionAnalytics.From(_equity.ToList(), trades, _positionSnaps.Values.ToList());
        ReportText = report.Text() + "\nLive-session ratios use equity steps, not closed-trade AnalyticReports.";
        EquitySeries = _equity.Count < 2
            ? Array.Empty<ChartSeries>()
            : new[] { new ChartSeries { Name = "Equity", Values = _equity.ToArray(), Color = Color.Parse("#3DDC84") } };
    }

    public void LoadSettingsTree()
    {
        _settingsSavedNote = false;
        SetSettingsDirty(false);
        RebuildSettings(_selectedSettings?.Key);
        RefreshSettingsStatus();
    }

    public void SaveSettingsTree()
    {
        if (_settingsRoot == null)
            RebuildSettings(_selectedSettings?.Key);
        if (_settingsRoot == null)
            return;
        foreach (var field in AllSettingsFields())
        {
            if (!field.IsDirty)
                continue;
            if (field.ListPath != null)
                SettingsTree.TryReplaceList(_settingsRoot, field.ListPath, SettingsEditor.SplitList(field.Value));
            else
                SettingsTree.TrySet(_settingsRoot, field.Path, field.Value);
        }
        try
        {
            var path = SettingsFile();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, SettingsTree.ToJson(_settingsRoot));
        }
        catch (Exception ex)
        {
            SettingsBanner = "Could not save settings. " + ex.Message;
            ShowSettingsSaved = true;
            return;
        }
        var keep = _selectedSettings?.Key;
        _settingsSavedNote = true;
        SetSettingsDirty(false);
        RebuildSettings(keep);
        RefreshSettingsStatus();
    }

    private void SetSettingsDirty(bool value)
    {
        if (_settingsDirty == value)
            return;
        SettingsDirty = value;
        Raise(nameof(SaveSettingsLabel));
    }

    private void RebuildSettings(string? preferKey)
    {
        var json = SettingsManager.Instance.GetAllSettings();
        _settingsRoot = SettingsTree.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var hints = new Dictionary<string, SettingsPluginHint>(StringComparer.Ordinal);
        foreach (var plugin in HostPlugins.AllPlugins)
        {
            hints[plugin.GetPluginUniqueID()] = new SettingsPluginHint
            {
                Name = plugin.Name ?? "",
                Description = plugin.Description ?? ""
            };
        }
        var groups = SettingsEditor.Organize(_settingsRoot, hints);
        SelectedSettings = null;
        SettingsConnectors.Clear();
        SettingsStudies.Clear();
        SettingsApplication.Clear();
        SettingsOther.Clear();
        foreach (var group in groups)
        {
            var row = SettingsGroupRow.Create(group, OnSettingsEdited);
            switch (group.Bucket)
            {
                case "Connectors":
                    SettingsConnectors.Add(row);
                    break;
                case "Studies":
                    SettingsStudies.Add(row);
                    break;
                case "Application":
                    SettingsApplication.Add(row);
                    break;
                default:
                    SettingsOther.Add(row);
                    break;
            }
        }
        _catalogLine = CatalogLine();
        ApplySettingsFilter(preferKey);
    }

    private void OnSettingsEdited()
    {
        _settingsSavedNote = false;
        SetSettingsDirty(AllSettingsFields().Any(field => field.IsDirty));
        RefreshSettingsStatus();
    }

    private void ApplySettingsFilter(string? preferKey)
    {
        var query = _settingsQuery.Trim();
        foreach (var group in AllSettingsGroups())
        {
            var titleMatch = query.Length == 0 || Contains(group.Title, query) || Contains(group.Summary, query) || Contains(group.Eyebrow, query) || Contains(group.StatusText, query);
            var anyField = false;
            foreach (var section in group.Sections)
            {
                var sectionShown = false;
                foreach (var field in section.Fields)
                {
                    var fieldMatch = titleMatch || Contains(field.Label, query) || Contains(field.Hint, query) || (field.Kind != SettingsFieldKind.Secret && Contains(field.Value, query));
                    var show = query.Length == 0 || fieldMatch;
                    field.IsShown = show;
                    if (show)
                    {
                        sectionShown = true;
                        anyField = true;
                    }
                }
                section.IsShown = query.Length == 0 || sectionShown;
            }
            group.IsShown = query.Length == 0 || titleMatch || anyField;
        }

        SettingsGroupRow? prefer = null;
        if (!string.IsNullOrEmpty(preferKey))
        {
            foreach (var group in AllSettingsGroups())
            {
                if (group.Key == preferKey && group.IsShown)
                {
                    prefer = group;
                    break;
                }
            }
        }
        if (prefer != null)
            SelectedSettings = prefer;
        else if (_selectedSettings == null || !_selectedSettings.IsShown)
            SelectedSettings = AllSettingsGroups().FirstOrDefault(group => group.IsShown);
        RaiseNavigation();
        RefreshSettingsStatus();
    }

    private void RefreshSettingsStatus()
    {
        var query = _settingsQuery.Trim();
        if (query.Length == 0)
            SettingsNavStatus = _catalogLine;
        else
        {
            var shown = AllSettingsGroups().Count(group => group.IsShown);
            SettingsNavStatus = shown == 1 ? "1 match" : shown.ToString(CultureInfo.InvariantCulture) + " matches";
        }

        SettingsBanner = !_settingsDirty && _settingsSavedNote
            ? "Saved. Restart VisualHFT so plugins reload this file."
            : "";
        ShowSettingsSaved = SettingsBanner.Length > 0;
    }

    private string CatalogLine()
    {
        var parts = new List<string>();
        AddCount(parts, SettingsConnectors.Count, "connector", "connectors");
        AddCount(parts, SettingsStudies.Count, "study", "studies");
        if (SettingsApplication.Count > 0)
            parts.Add("Application");
        AddCount(parts, SettingsOther.Count, "other group", "other groups");
        return parts.Count == 0 ? "No settings file yet." : string.Join(" · ", parts);
    }

    private static void AddCount(List<string> parts, int count, string singular, string plural)
    {
        if (count == 0)
            return;
        parts.Add(count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural));
    }

    private IEnumerable<SettingsGroupRow> AllSettingsGroups()
    {
        foreach (var group in SettingsConnectors)
            yield return group;
        foreach (var group in SettingsStudies)
            yield return group;
        foreach (var group in SettingsApplication)
            yield return group;
        foreach (var group in SettingsOther)
            yield return group;
    }

    private IEnumerable<SettingsFieldRow> AllSettingsFields()
    {
        foreach (var group in AllSettingsGroups())
        {
            foreach (var section in group.Sections)
            {
                foreach (var field in section.Fields)
                    yield return field;
            }
        }
    }

    private void RaiseNavigation()
    {
        Raise(nameof(HasConnectors));
        Raise(nameof(HasStudies));
        Raise(nameof(HasApplication));
        Raise(nameof(HasOther));
        Raise(nameof(HasAnySettings));
        Raise(nameof(ShowSettingsEmpty));
        Raise(nameof(ShowSettingsNoMatch));
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectedSettings));
        Raise(nameof(HasSelectedSettings));
        Raise(nameof(SelectedSettingsEyebrow));
        Raise(nameof(SelectedSettingsTitle));
        Raise(nameof(SelectedSettingsSubtitle));
        Raise(nameof(SelectedHasSubtitle));
        Raise(nameof(SelectedHasStatus));
        Raise(nameof(SelectedStatusText));
        Raise(nameof(SelectedStatusBrush));
        Raise(nameof(SelectedSections));
        Raise(nameof(ShowSettingsNoMatch));
    }

    private static bool Contains(string text, string query) =>
        text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string SettingsFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisualHFT", "settings.json");

    private static readonly IBrush BidBar = new SolidColorBrush(Color.Parse("#163D2E"));
    private static readonly IBrush AskBar = new SolidColorBrush(Color.Parse("#4A1C1C"));

    internal static IBrush BrushFrom(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static void FillLadder(ObservableCollection<BookRow> rows, List<Level> levels, bool isBid)
    {
        var max = levels.Count == 0 ? 1 : levels.Max(l => l.Size);
        while (rows.Count < levels.Count)
            rows.Add(new BookRow());
        while (rows.Count > levels.Count)
            rows.RemoveAt(rows.Count - 1);

        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            rows[i].PriceText = level.Price.ToString("0.########", CultureInfo.InvariantCulture);
            rows[i].SizeText = level.Size.ToString("0.####", CultureInfo.InvariantCulture);
            rows[i].BarWidth = Math.Max(4, 180 * (level.Size / max));
            rows[i].IsBid = isBid;
            rows[i].PriceBrush = isBid ? Brushes.MediumSeaGreen : Brushes.IndianRed;
            rows[i].BarBrush = isBid ? BidBar : AskBar;
        }
    }

    private static IReadOnlyList<DepthPoint> Cumulative(IEnumerable<Level> levels)
    {
        double cum = 0;
        var points = new List<DepthPoint>();
        foreach (var level in levels.Take(80))
        {
            cum += level.Size;
            points.Add(new DepthPoint(level.Price, cum));
        }
        return points;
    }

    private void ClearMarket(WatchItemRow? focus)
    {
        RememberFocus(focus);
        WipeFocusedPanels(resetTape: focus == null);
        HasTickerFocus = focus != null;
        TickerStatus = focus != null
            ? "Waiting for " + focus.Native + " on " + focus.Venue + "…"
            : "No ticker selected.";
        RefreshSessionChrome();
    }

    private void RefreshSessionChrome()
    {
        var focus = FocusedWatch();
        foreach (var session in Sessions)
            session.ShowInChrome = focus != null
                && string.Equals(session.Name, focus.Venue, StringComparison.OrdinalIgnoreCase);
    }

    private void WipeFocusedPanels(bool resetTape)
    {
        BidRest = BidDecimals = BidLast = BidSize = "";
        AskRest = AskDecimals = AskLast = AskSize = "";
        MidText = "—";
        SpreadText = "—";
        LastTradeText = "—";
        LastTradeBrush = Brushes.LightGray;
        Imbalance = 0;
        Asks.Clear();
        Bids.Clear();
        Venues.Clear();
        DepthMirror.Clear();
        DepthLadder.Clear();
        DepthBids = Array.Empty<DepthPoint>();
        DepthAsks = Array.Empty<DepthPoint>();
        Raise(nameof(DepthBids));
        Raise(nameof(DepthAsks));
        MidSeries = Array.Empty<ChartSeries>();
        Raise(nameof(MidSeries));
        SpreadSeries = Array.Empty<ChartSeries>();
        FlowImbalance = Array.Empty<ChartSeries>();
        FlowMid = Array.Empty<ChartSeries>();
        FlowImbalanceText = "—";
        FlowMidText = "—";
        PriceFrame = new BookPriceFrame();
        _flow.Clear();
        _prices.Clear();
        _spreads.Clear();
        _ingress.Clear(resetTape);
        lock (_gate)
        {
            _midHistory.Clear();
            _midStamp.Clear();
        }
        if (resetTape)
            _orders.Clear();
        if (resetTape)
        {
            TradeTape.Clear();
            Positions.Clear();
            _positions.Clear();
            _positionSnaps.Clear();
            _equity.Clear();
            Orders.Clear();
        }
        foreach (var study in Studies)
            study.ResetDisplay();
    }

    private void BindTickerSearch()
    {
        _exchangePlugins.Clear();
        foreach (var plugin in HostPlugins.AllPlugins.Where(p => p.PluginType == ePluginType.MARKET_CONNECTOR).OrderBy(TickerSearch.VenueName))
        {
            var venue = TickerSearch.VenueName(plugin);
            if (string.IsNullOrEmpty(venue))
                continue;
            _exchangePlugins[venue] = plugin;
            if (!Exchanges.Contains(venue))
                Exchanges.Add(venue);
        }

        if (Exchanges.Count == 0)
            CatalogStatus = "No market connectors loaded.";
        else if (string.IsNullOrEmpty(CatalogStatus) || CatalogStatus.StartsWith("Select an exchange", StringComparison.Ordinal))
            CatalogStatus = Exchanges.Count.ToString(CultureInfo.InvariantCulture) + " exchanges ready.";

        if (Watchlist.Count == 0)
        {
            foreach (var snap in TickerSearch.LoadWatchlist()
                         .DistinctBy(item => item.Venue + "|" + item.Native + "|" + item.Kind))
                Watchlist.Add(WatchItemRow.From(snap));
        }
        HasWatchlist = Watchlist.Count > 0;
        if (!_tickersBound)
        {
            ClearMarket(null);
            _marketIdle = true;
            _tickersBound = true;
        }

        if (string.IsNullOrEmpty(SelectedExchange) && Exchanges.Count > 0)
            SelectedExchange = Exchanges[0];
        else
            _ = LoadTickerCatalogAsync();
    }

    private async Task LoadTickerCatalogAsync()
    {
        var venue = SelectedExchange;
        if (string.IsNullOrEmpty(venue) || !_exchangePlugins.TryGetValue(venue, out var plugin))
        {
            _catalog = new List<TickerHit>();
            CatalogStatus = "Select an exchange, then type a ticker.";
            FilterTickerHits();
            return;
        }

        var version = Interlocked.Increment(ref _catalogVersion);
        CatalogStatus = "Loading " + venue + " tickers…";
        IReadOnlyList<TickerHit> hits;
        string status;
        try
        {
            hits = await TickerSearch.FetchAsync(plugin).ConfigureAwait(false);
            var futures = hits.Count(hit => hit.Kind == TickerKind.Futures);
            status = futures > 0
                ? hits.Count.ToString(CultureInfo.InvariantCulture) + " " + venue + " tickers (" + futures.ToString(CultureInfo.InvariantCulture) + " futures). Type to shortlist."
                : hits.Count.ToString(CultureInfo.InvariantCulture) + " " + venue + " tickers. Type to shortlist.";
        }
        catch (Exception ex)
        {
            hits = TickerSearch.FromSettings(plugin);
            status = "Could not fetch " + venue + " tickers (" + ex.Message + "). Searching subscribed symbols.";
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _catalogVersion)
                return;
            _catalog = hits.ToList();
            CatalogStatus = status;
            FilterTickerHits();
        });
    }

    private void FilterTickerHits()
    {
        TickerHits.Clear();
        foreach (var hit in TickerSearch.Shortlist(_catalog, TickerQuery))
            TickerHits.Add(hit);
        ShowTickerHits = TickerHits.Count > 0;
    }

    public void AddTicker(TickerHit? hit) => _ = AddTickerAsync(hit);

    public async Task AddTickerAsync(TickerHit? hit)
    {
        hit ??= TickerHits.FirstOrDefault();
        if (hit == null)
            return;

        var existing = Watchlist.FirstOrDefault(row => row.SameAs(hit));
        var row = existing ?? WatchItemRow.From(hit);
        if (existing == null)
        {
            Watchlist.Add(row);
            HasWatchlist = true;
            PersistWatchlist();
        }
        FocusWatch(row);
        TickerQuery = "";
    }

    public void FocusWatch(WatchItemRow row)
    {
        foreach (var item in Watchlist)
            item.IsFocused = item == row;
        RememberFocus(row);
        SelectedSymbol = row.Normalized;
        SelectedProvider = row.Venue;
        if (!Symbols.Contains(row.Normalized))
            Symbols.Add(row.Normalized);
        HasTickerFocus = true;
        WipeFocusedPanels(resetTape: true);
        FollowStudies(row.Venue, LiveSymbol(row) ?? row.Normalized);
        foreach (var study in Studies)
            study.RefreshContext();
        RefreshSessionChrome();
        TickerStatus = HasLiveBook(row)
            ? row.Native + "  " + row.Venue
            : "Waiting for " + row.Native + " on " + row.Venue + "…";
        _dirty = true;
        _ = EnsureFocusedFeedAsync(row, Interlocked.Increment(ref _focusVersion));
    }

    private async Task EnsureFocusedFeedAsync(WatchItemRow row, int version)
    {
        if (!_exchangePlugins.TryGetValue(row.Venue, out var plugin))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _focusVersion)
                    TickerStatus = "No connector for " + row.Venue + ".";
            });
            return;
        }

        await StopOtherConnectorsAsync(row.Venue);
        var hit = row.ToHit();
        var restricted = TickerSearch.RestrictTo(plugin, hit);
        var running = plugin.Status is ePluginStatus.STARTED or ePluginStatus.STARTING;
        if (restricted || !running)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _focusVersion)
                    TickerStatus = "Subscribing to " + row.Native + " on " + row.Venue + "…";
            });
            if (running)
                await HostPlugins.RestartPluginAsync(plugin);
            else
                await HostPlugins.StartPluginAsync(plugin);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _focusVersion || FocusedWatch() != row)
                return;
            FollowStudies(row.Venue, LiveSymbol(row) ?? row.Normalized);
            foreach (var study in Studies)
                study.RefreshContext();
            RefreshSessionChrome();
            TickerStatus = HasLiveBook(row)
                ? row.Native + "  " + row.Venue
                : "Waiting for " + row.Native + " on " + row.Venue + "…";
            _dirty = true;
        });
    }

    private async Task StopOtherConnectorsAsync(string keepVenue)
    {
        foreach (var plugin in HostPlugins.AllPlugins)
        {
            if (plugin.PluginType != ePluginType.MARKET_CONNECTOR)
                continue;
            if (string.Equals(TickerSearch.VenueName(plugin), keepVenue, StringComparison.OrdinalIgnoreCase))
                continue;
            if (plugin.Status is ePluginStatus.STARTED or ePluginStatus.STARTING)
                await HostPlugins.StopPluginAsync(plugin);
        }
    }

    private string? LiveSymbol(WatchItemRow row) =>
        _ingress.LiveSymbol(row.Native, row.Normalized, row.Venue);

    private bool HasLiveBook(WatchItemRow row) =>
        _ingress.HasLiveBook(row.Native, row.Normalized, row.Venue);

    public void RemoveWatch(WatchItemRow row)
    {
        var index = Watchlist.IndexOf(row);
        Watchlist.Remove(row);
        HasWatchlist = Watchlist.Count > 0;
        PersistWatchlist();
        if (row.IsFocused && Watchlist.Count > 0)
            FocusWatch(Watchlist[Math.Clamp(index, 0, Watchlist.Count - 1)]);
        else if (Watchlist.Count == 0)
        {
            SelectedSymbol = "";
            SelectedProvider = "All";
            HasTickerFocus = false;
            RememberFocus(null);
            WipeFocusedPanels(resetTape: true);
            RefreshSessionChrome();
            _ = StopOtherConnectorsAsync("");
        }
    }

    public void HideTickerHits() => ShowTickerHits = false;

    private void PersistWatchlist() =>
        TickerSearch.SaveWatchlist(Watchlist.Select(row => row.Snapshot()));
}

public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class BookRow : ObservableObject
{
    private string _priceText = "";
    private string _sizeText = "";
    private double _barWidth;
    private bool _isBid;

    private IBrush _priceBrush = Brushes.White;
    private IBrush _barBrush = Brushes.Transparent;

    public string PriceText { get => _priceText; set => Set(ref _priceText, value); }
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value); }
    public bool IsBid { get => _isBid; set => Set(ref _isBid, value); }
    public IBrush PriceBrush { get => _priceBrush; set => Set(ref _priceBrush, value); }
    public IBrush BarBrush { get => _barBrush; set => Set(ref _barBrush, value); }
}

public sealed class VenueRow
{
    public string Provider { get; init; } = "";
    public string MidText { get; init; } = "";
    public string SpreadText { get; init; } = "";
}

public sealed class PositionRow : ObservableObject
{
    private string _symbol = "";
    private string _netText = "";
    private string _buyText = "";
    private string _sellText = "";
    private string _exposureText = "";
    private string _workingBuyText = "";
    private string _workingSellText = "";
    private string _totalText = "";
    private string _realizedText = "";
    private string _openText = "";
    private string _updatedText = "";
    private IBrush _plBrush = Brushes.Transparent;
    private IBrush _openBrush = Brushes.Transparent;
    public string Symbol { get => _symbol; set => Set(ref _symbol, value); }
    public string NetText { get => _netText; set => Set(ref _netText, value); }
    public string BuyText { get => _buyText; set => Set(ref _buyText, value); }
    public string SellText { get => _sellText; set => Set(ref _sellText, value); }
    public string ExposureText { get => _exposureText; set => Set(ref _exposureText, value); }
    public string WorkingBuyText { get => _workingBuyText; set => Set(ref _workingBuyText, value); }
    public string WorkingSellText { get => _workingSellText; set => Set(ref _workingSellText, value); }
    public string TotalText { get => _totalText; set => Set(ref _totalText, value); }
    public string RealizedText { get => _realizedText; set => Set(ref _realizedText, value); }
    public string OpenText { get => _openText; set => Set(ref _openText, value); }
    public string UpdatedText { get => _updatedText; set => Set(ref _updatedText, value); }
    public IBrush PlBrush { get => _plBrush; set => Set(ref _plBrush, value); }
    public IBrush OpenBrush { get => _openBrush; set => Set(ref _openBrush, value); }
}

public sealed class NotificationRow : ObservableObject
{
    private bool _isRead;
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string When { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string Type { get; init; } = "";
    public IBrush Tone { get; init; } = Brushes.LightGray;
    public bool IsRead { get => _isRead; set => Set(ref _isRead, value); }

    public NotificationNote Note => new(Id, Title, Message, Timestamp, Type, IsRead);

    public static NotificationRow From(VisualHFT.Commons.Model.ErrorNotification notification)
    {
        var tone = notification.NotificationType switch
        {
            HelprNorificationManagerTypes.ERROR => Brushes.IndianRed,
            HelprNorificationManagerTypes.WARNING => Brushes.Goldenrod,
            HelprNorificationManagerTypes.TRIGGER_ACTION => Brushes.DeepSkyBlue,
            _ => Brushes.LightGray
        };
        return new NotificationRow
        {
            Id = notification.Id,
            Title = notification.Title ?? "",
            Message = notification.Message ?? "",
            When = notification.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Timestamp = notification.Timestamp,
            Type = notification.NotificationType.ToString(),
            Tone = tone,
            IsRead = notification.IsRead
        };
    }
}

public sealed class ProviderSessionRow : ObservableObject
{
    private string _name = "";
    private string _statusText = "";
    private string _toolTip = "";
    private IBrush _statusBrush = Brushes.Gray;
    private eSESSIONSTATUS _status;
    private bool _showInChrome;

    public ProviderSessionRow(int code) => Code = code;

    public int Code { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ToolTip { get => _toolTip; private set => Set(ref _toolTip, value); }
    public IBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public eSESSIONSTATUS Status => _status;
    public bool ShowInChrome { get => _showInChrome; set => Set(ref _showInChrome, value); }
    public IPlugin? Plugin { get; private set; }

    public void Apply(string name, eSESSIONSTATUS status, string toolTip, IPlugin? plugin)
    {
        Name = name;
        _status = status;
        StatusText = status.ToString().Replace('_', ' ');
        ToolTip = string.IsNullOrEmpty(toolTip) ? StatusText : toolTip;
        StatusBrush = new SolidColorBrush(Color.Parse(SessionControl.Color(status)));
        if (plugin != null)
            Plugin = plugin;
        Raise(nameof(Status));
    }
}

public sealed class TradeTapeRow
{
    public string Time { get; init; } = "";
    public string Price { get; init; } = "";
    public string Size { get; init; } = "";
    public bool IsBuy { get; init; }
    public bool IsSell { get; init; }

    public static TradeTapeRow From(TradePrint trade) => new()
    {
        Time = trade.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        Price = trade.Price.ToString("0.########", CultureInfo.InvariantCulture),
        Size = trade.Size.ToString("0.####", CultureInfo.InvariantCulture),
        IsBuy = trade.IsBuy != false,
        IsSell = trade.IsBuy == false
    };
}

public sealed class OrderRow
{
    public string Created { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Quantity { get; init; } = "";
    public string Price { get; init; } = "";
    public string Side { get; init; } = "";
    public string Filled { get; init; } = "";
    public string FilledQuantity { get; init; } = "";
    public string AveragePrice { get; init; } = "";
    public string TimeInForce { get; init; } = "";
    public string Type { get; init; } = "";
    public string Status { get; init; } = "";
    public string BestBid { get; init; } = "";
    public string BestAsk { get; init; } = "";
    public IBrush SideBrush { get; init; } = Brushes.Transparent;
    public IBrush StatusBrush { get; init; } = Brushes.White;
    public double FillBar { get; init; }
    public bool CanCancel { get; init; }

    public static OrderRow From(OrderSnap order) => new()
    {
        Created = order.Created.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        Provider = order.Provider,
        Symbol = order.Symbol,
        ClientId = order.ClientId,
        Quantity = HelperCommon.GetKiloFormatter(order.Quantity),
        Price = order.Price.ToString("0.########", CultureInfo.InvariantCulture),
        Side = order.Side,
        Filled = order.FilledPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%",
        FilledQuantity = HelperCommon.GetKiloFormatter(order.FilledQuantity),
        AveragePrice = order.AveragePrice.ToString("0.########", CultureInfo.InvariantCulture),
        TimeInForce = order.TimeInForce,
        Type = order.Type,
        Status = order.Status,
        BestBid = order.BestBid.ToString("0.########", CultureInfo.InvariantCulture),
        BestAsk = order.BestAsk.ToString("0.########", CultureInfo.InvariantCulture),
        SideBrush = TerminalViewModel.BrushFrom(BlotterPaint.SideFill(order.Side)),
        StatusBrush = TerminalViewModel.BrushFrom(BlotterPaint.StatusFill(order.Status)),
        FillBar = BlotterPaint.FillWidth(order.FilledPercent, 72),
        CanCancel = BlotterPaint.CanCancel(order.Status)
    };
}

public sealed class PluginGroupRow
{
    public PluginGroupRow(string kind, IReadOnlyList<PluginRow> items)
    {
        Kind = kind;
        Items = new ObservableCollection<PluginRow>(items);
    }

    public string Kind { get; }
    public ObservableCollection<PluginRow> Items { get; }
}

public sealed class StudyGroupRow
{
    public StudyGroupRow(string owner, bool isGroup, IPlugin plugin, IReadOnlyList<StudyRow> studies)
    {
        Owner = owner;
        IsGroup = isGroup;
        Plugin = plugin;
        Studies = new ObservableCollection<StudyRow>(studies);
    }

    public string Owner { get; }
    public bool IsGroup { get; }
    public IPlugin Plugin { get; }
    public ObservableCollection<StudyRow> Studies { get; }
}

public sealed class StudyRow : ObservableObject
{
    private readonly TerminalViewModel _desk;
    private readonly List<double> _history = new();
    private readonly List<double> _mids = new();
    private readonly object _paint = new();
    private string _pendingText = "";
    private double _pendingValue;
    private bool _pending;
    private string _valueText = "—";
    private IBrush _valueBrush = Brushes.White;

    public StudyRow(IStudy study, IPlugin owner, TerminalViewModel desk)
    {
        Study = study;
        Owner = owner;
        _desk = desk;
        Title = string.IsNullOrWhiteSpace(study.TileTitle) ? owner.Name : study.TileTitle;
        Tooltip = StudyTooltip.Plain(study.TileToolTip);
        HasOwnSettings = owner is IStudy and not IMultiStudy;
        study.OnCalculated += (_, model) =>
        {
            if (model == null || !_desk.HasTickerFocus)
                return;
            var settings = owner.Settings;
            if (!_desk.Matches(settings?.Symbol ?? "", settings?.Provider?.ProviderName ?? ""))
                return;
            var value = (double)model.Value;
            string text;
            try
            {
                text = model.CustomFormatter != null
                    ? model.CustomFormatter(model.Value)
                    : model.Value.ToString(string.IsNullOrWhiteSpace(model.Format) ? "0.######" : model.Format, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                text = model.Value.ToString(CultureInfo.InvariantCulture);
            }

            lock (_history)
            {
                _history.Add(value);
                _mids.Add((double)model.MarketMidPrice);
                if (_history.Count > 360)
                {
                    _history.RemoveAt(0);
                    _mids.RemoveAt(0);
                }
            }
            lock (_paint)
            {
                _pendingText = text;
                _pendingValue = value;
                _pending = true;
            }
            _desk.MarkDirty();
        };
    }

    internal void PublishPending()
    {
        string text;
        double value;
        lock (_paint)
        {
            if (!_pending)
                return;
            _pending = false;
            text = _pendingText;
            value = _pendingValue;
        }
        if (!_desk.HasTickerFocus)
            return;
        ValueText = text;
        ValueBrush = double.IsNaN(value) || value == 0
            ? Brushes.White
            : value > 0 ? Brushes.MediumSeaGreen : Brushes.IndianRed;
    }

    public void ResetDisplay()
    {
        lock (_history)
        {
            _history.Clear();
            _mids.Clear();
        }
        lock (_paint)
            _pending = false;
        ValueText = "—";
        ValueBrush = Brushes.White;
        Context = "";
    }

    public IStudy Study { get; }
    public IPlugin Owner { get; }
    public string Title { get; }
    public string Tooltip { get; }
    public bool HasOwnSettings { get; }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public IBrush ValueBrush { get => _valueBrush; private set => Set(ref _valueBrush, value); }
    private string _context = "";
    public string Context { get => _context; private set => Set(ref _context, value); }

    public void RefreshContext()
    {
        var settings = Owner.Settings;
        var error = settings?.GetConfigurationError();
        var instrumentError = !string.IsNullOrEmpty(error)
            && (error.Contains("symbol", StringComparison.OrdinalIgnoreCase)
                || error.Contains("data provider", StringComparison.OrdinalIgnoreCase));
        var extra = !string.IsNullOrEmpty(error) && !instrumentError ? error : null;
        var focus = _desk.FocusedWatch();
        if (focus != null)
            Context = string.IsNullOrEmpty(extra) ? focus.Venue + "  " + focus.Native : focus.Native + "  " + extra;
        else
            Context = extra ?? error ?? $"{settings?.Provider?.ProviderName}  {settings?.Symbol}".Trim();
    }

    public double[] CopyHistory()
    {
        lock (_history)
            return _history.ToArray();
    }

    public double[] CopyMids()
    {
        lock (_history)
            return _mids.ToArray();
    }
}

public sealed class PluginRow : ObservableObject
{
    private readonly TerminalViewModel _owner;
    private string _status = "";

    public PluginRow(IPlugin plugin, TerminalViewModel owner)
    {
        Plugin = plugin;
        _owner = owner;
        StartCommand = new RelayCommand<object>(_ => HostPlugins.StartPlugin(Plugin));
        StopCommand = new RelayCommand<object>(_ => HostPlugins.StopPlugin(Plugin));
        Refresh();
    }

    public IPlugin Plugin { get; }
    public string Name => Plugin.Name;
    public string Version => string.IsNullOrWhiteSpace(Plugin.Version) ? "" : Plugin.Version;
    public string Kind => Plugin.PluginType.ToString();
    public string Description => Plugin.Description ?? "";
    public string CustomUi => PluginSurface.Describe(Plugin.GetCustomUI());
    public string Status { get => _status; private set => Set(ref _status, value); }
    public RelayCommand<object> StartCommand { get; }
    public RelayCommand<object> StopCommand { get; }

    public void Refresh() => Status = Plugin.Status.ToString();

    public void OpenSettings(Avalonia.Controls.Window owner)
    {
        var window = new Views.PluginSettingsWindow(Plugin);
        window.ShowDialog(owner);
        Refresh();
        _owner.RefreshPlugins();
    }
}

public sealed class RuleRow : ObservableObject
{
    private readonly TriggerRule _rule;
    private readonly TerminalViewModel _owner;
    private bool _enabled;

    public RuleRow(TriggerRule rule, TerminalViewModel owner)
    {
        _rule = rule;
        _owner = owner;
        _enabled = rule.IsEnabled;
        Summary = BuildSummary(rule);
        DeleteCommand = new RelayCommand<object>(_ =>
        {
            TriggerEngineService.RemoveRule(rule.RuleID);
            owner.RefreshRules();
        });
    }

    public TriggerRule Rule => _rule;
    public string Name => _rule.Name ?? "(unnamed)";
    public string Summary { get; }
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value))
                return;
            _rule.IsEnabled = value;
            TriggerEngineService.AddOrUpdateRule(_rule);
            _owner.RefreshRules();
        }
    }

    public RelayCommand<object> DeleteCommand { get; }

    private static string BuildSummary(TriggerRule rule)
    {
        if (rule.Condition == null || rule.Condition.Count == 0)
            return "No condition";
        return string.Join(" AND ", rule.Condition.Select(condition =>
        {
            var window = condition.Window is { Duration: > 0 }
                ? $" for {condition.Window.Duration} {condition.Window.Unit}"
                : "";
            return $"{condition.Operator} {condition.Threshold.ToString(CultureInfo.InvariantCulture)}{window}";
        }));
    }
}

public readonly record struct Level(double Price, double Size, double ActiveSize = 0);
public readonly record struct DepthPoint(double Price, double Cumulative);

public sealed class ChartSeries
{
    public required string Name { get; init; }
    public required IReadOnlyList<double> Values { get; init; }
    public required Color Color { get; init; }
    public double? AxisMin { get; init; }
    public double? AxisMax { get; init; }
    public bool ZeroLine { get; init; }
}

sealed class BookSnapshot
{
    public required string Symbol { get; init; }
    public required string Provider { get; init; }
    public required double Mid { get; init; }
    public required double Micro { get; init; }
    public required double Spread { get; init; }
    public required double Imbalance { get; init; }
    public required int Decimals { get; init; }
    public required DateTime UpdatedUtc { get; init; }
    public required List<Level> Bids { get; init; }
    public required List<Level> Asks { get; init; }

    public static BookSnapshot Capture(OrderBook book)
    {
        var bids = Copy(book.Bids);
        var asks = Copy(book.Asks);
        var bestBid = bids.Count == 0 ? default : bids.MaxBy(level => level.Price);
        var bestAsk = asks.Count == 0 ? default : asks.MinBy(level => level.Price);
        return new BookSnapshot
        {
            Symbol = book.Symbol ?? "",
            Provider = string.IsNullOrWhiteSpace(book.ProviderName) ? "Provider " + book.ProviderID : book.ProviderName,
            Mid = book.MidPrice,
            Micro = Microprice.Of(bestBid.Price, bestBid.Size, bestAsk.Price, bestAsk.Size),
            Spread = book.Spread,
            // Incremental venue deltas never refresh OrderBook.ImbalanceValue. Windows
            // recalculates on the UI thread; compute from the copied sides instead.
            Imbalance = BookImbalance.Of(bids.Select(level => level.Size), asks.Select(level => level.Size)),
            Decimals = book.PriceDecimalPlaces,
            UpdatedUtc = DateTime.UtcNow,
            Bids = bids,
            Asks = asks
        };
    }

    private static List<Level> Copy(System.Collections.IEnumerable? side)
    {
        var list = new List<Level>();
        if (side == null)
            return list;
        foreach (var entry in side)
        {
            if (entry is not BookItem item || item.Price is not double price || item.Size is not double size || size <= 0)
                continue;
            list.Add(new Level(price, size, item.ActiveSize ?? 0));
        }
        return list;
    }
}

public sealed class WatchItemRow : ObservableObject
{
    private bool _focused;

    public required string Venue { get; init; }
    public required string Native { get; init; }
    public required string Normalized { get; init; }
    public required TickerKind Kind { get; init; }
    public required string SettingEntry { get; init; }
    public string KindLabel => Kind == TickerKind.Futures ? "futures" : "spot";
    public string Caption => Venue + "  " + Native;
    public IBrush ChipBrush => _focused ? new SolidColorBrush(Color.Parse("#1F6FEB")) : new SolidColorBrush(Color.Parse("#212833"));
    public bool IsFocused
    {
        get => _focused;
        set
        {
            if (!Set(ref _focused, value))
                return;
            Raise(nameof(ChipBrush));
        }
    }

    public bool SameAs(TickerHit hit) =>
        string.Equals(Venue, hit.Venue, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Native, hit.Native, StringComparison.OrdinalIgnoreCase)
        && Kind == hit.Kind;

    public WatchSnapshot Snapshot() => new(Venue, Native, Normalized, Kind, SettingEntry);

    public static WatchItemRow From(TickerHit hit) => new()
    {
        Venue = hit.Venue,
        Native = hit.Native,
        Normalized = hit.Normalized,
        Kind = hit.Kind,
        SettingEntry = hit.SettingEntry
    };

    public TickerHit ToHit() => new()
    {
        Venue = Venue,
        Native = Native,
        Normalized = Normalized,
        Kind = Kind,
        SettingEntry = SettingEntry
    };

    public static WatchItemRow From(WatchSnapshot snap) => new()
    {
        Venue = snap.Venue,
        Native = snap.Native,
        Normalized = snap.Normalized,
        Kind = snap.Kind,
        SettingEntry = snap.SettingEntry
    };
}
