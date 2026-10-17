using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Model;
using VisualHFT.Desktop.Screens;
using VisualHFT.Desktop.ViewModels;
using VisualHFT.Enums;
using VisualHFT.Model;
using VisualHFT.PluginManager;

namespace VisualHFT.Desktop.Hosting;

/// <summary>
/// Copies market data off the synchronous helper callbacks and keeps the mutable
/// <see cref="OrderBook"/>, <see cref="Trade"/>, and <see cref="Position"/> from escaping.
/// The UI thread drains the copies on its timer.
/// </summary>
internal sealed class MarketIngress
{
    public const int MaxTrades = 2000;
    public const int MaxNotifications = 80;

    private readonly object _gate = new();
    private readonly Dictionary<string, BookSnapshot> _books = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TradePrint> _trades = new();
    private readonly Dictionary<string, PositionUpdate> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ProviderUpdate> _providers = new();
    private readonly List<NotificationUpdate> _notifications = new();
    private string _focusNative = "";
    private string _focusNormalized = "";
    private string _focusVenue = "";
    private string _providerFilter = "All";
    private TradePrint? _latestTrade;
    private bool _tradePending;
    private bool _dirty;

    public void SetFocus(string? native, string? normalized, string? venue)
    {
        lock (_gate)
        {
            var nextNative = native ?? "";
            var nextNormalized = normalized ?? "";
            var nextVenue = venue ?? "";
            if (string.Equals(_focusNative, nextNative, StringComparison.Ordinal)
                && string.Equals(_focusNormalized, nextNormalized, StringComparison.Ordinal)
                && string.Equals(_focusVenue, nextVenue, StringComparison.OrdinalIgnoreCase))
                return;

            _focusNative = nextNative;
            _focusNormalized = nextNormalized;
            _focusVenue = nextVenue;
            _books.Clear();
            _trades.Clear();
            _positions.Clear();
            _latestTrade = null;
            _tradePending = false;
            _dirty = true;
        }
    }

    public void SetProviderFilter(string? provider)
    {
        lock (_gate)
            _providerFilter = string.IsNullOrWhiteSpace(provider) ? "All" : provider;
    }

    public void Clear(bool resetTape)
    {
        lock (_gate)
        {
            if (resetTape)
            {
                _books.Clear();
                _trades.Clear();
                _latestTrade = null;
                _tradePending = false;
                _positions.Clear();
            }
        }
    }

    public void AddBook(OrderBook? book)
    {
        if (book == null || string.IsNullOrEmpty(book.Symbol))
            return;

        var snapshot = BookSnapshot.Capture(book);
        lock (_gate)
        {
            if (!Matches(snapshot.Symbol, snapshot.Provider))
                return;
            _books[BookKey(snapshot.Provider, snapshot.Symbol)] = snapshot;
            _dirty = true;
        }
    }

    public void AddTrade(Trade? trade)
    {
        if (trade == null)
            return;

        var print = new TradePrint(
            trade.Symbol ?? "",
            trade.ProviderName ?? "",
            (double)trade.Price,
            (double)trade.Size,
            trade.IsBuy,
            trade.Timestamp);

        lock (_gate)
        {
            if (!Matches(print.Symbol, print.Provider))
                return;
            _trades.Add(print);
            if (_trades.Count > MaxTrades)
                _trades.RemoveRange(0, _trades.Count - MaxTrades);
            _latestTrade = print;
            _tradePending = true;
            _dirty = true;
        }
    }

    public void AddPosition(Position? position)
    {
        if (position == null || string.IsNullOrEmpty(position.Symbol))
            return;

        var orders = CopyOrders(position.GetAllOrders(null));
        var update = new PositionUpdate(
            position.Symbol,
            position.NetPosition,
            position.TotBuy,
            position.TotSell,
            position.Exposure,
            position.WrkBuy,
            position.WrkSell,
            position.PLTot,
            position.PLRealized,
            position.PLOpen,
            position.LastUpdated,
            orders);

        lock (_gate)
        {
            var provider = string.Equals(_providerFilter, "All", StringComparison.OrdinalIgnoreCase)
                ? ""
                : _providerFilter;
            if (!Matches(update.Symbol, provider))
                return;
            _positions[update.Symbol] = update;
            _dirty = true;
        }
    }

    public void AddProvider(Provider? provider)
    {
        if (provider == null || string.IsNullOrEmpty(provider.ProviderName))
            return;

        var update = new ProviderUpdate(
            provider.ProviderCode,
            provider.ProviderName,
            provider.Status,
            provider.Tooltip ?? "",
            provider.Plugin);

        lock (_gate)
        {
            _providers[update.Code] = update;
            _dirty = true;
        }
    }

    public void AddNotification(ErrorNotification? notification)
    {
        if (notification == null)
            return;

        var update = new NotificationUpdate(
            notification.Id,
            notification.Title ?? "",
            notification.Message ?? "",
            notification.Timestamp,
            notification.IsRead,
            notification.NotificationType);

        lock (_gate)
        {
            _notifications.Add(update);
            if (_notifications.Count > MaxNotifications)
                _notifications.RemoveRange(0, _notifications.Count - MaxNotifications);
            _dirty = true;
        }
    }

    public bool HasLiveBook(string native, string normalized, string venue)
    {
        lock (_gate)
        {
            foreach (var book in _books.Values)
            {
                if (TickerSearch.SameInstrument(book.Symbol, native, normalized)
                    && string.Equals(book.Provider, venue, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    public string? LiveSymbol(string native, string normalized, string venue)
    {
        lock (_gate)
        {
            foreach (var book in _books.Values)
            {
                if (TickerSearch.SameInstrument(book.Symbol, native, normalized)
                    && string.Equals(book.Provider, venue, StringComparison.OrdinalIgnoreCase))
                    return book.Symbol;
            }
            return null;
        }
    }

    public MarketBatch Drain()
    {
        lock (_gate)
        {
            var latest = _tradePending ? _latestTrade : null;
            var batch = new MarketBatch(
                _dirty,
                _books.Values.ToList(),
                _trades.ToList(),
                latest,
                _positions.Values.ToList(),
                _providers.Values.ToList(),
                _notifications.ToList());
            _dirty = false;
            _tradePending = false;
            _positions.Clear();
            _providers.Clear();
            _notifications.Clear();
            return batch;
        }
    }

    private bool Matches(string symbol, string provider)
    {
        if (string.IsNullOrEmpty(_focusNative) && string.IsNullOrEmpty(_focusNormalized))
            return false;
        if (!TickerSearch.SameInstrument(symbol, _focusNative, _focusNormalized))
            return false;
        return string.IsNullOrEmpty(provider)
            || string.Equals(provider, _focusVenue, StringComparison.OrdinalIgnoreCase);
    }

    private static string BookKey(string provider, string symbol) => provider + "|" + symbol;

    private static List<OrderSnap> CopyOrders(IEnumerable<Order>? orders)
    {
        var copies = new List<OrderSnap>();
        if (orders == null)
            return copies;
        foreach (var order in orders)
        {
            if (order != null)
                copies.Add(CopyOrder(order));
        }
        return copies;
    }

    private static OrderSnap CopyOrder(Order order)
    {
        var executions = new List<(decimal Price, decimal Quantity)>();
        if (order.Executions != null)
        {
            foreach (var execution in order.Executions)
            {
                if (execution == null)
                    continue;
                executions.Add((execution.Price ?? 0, execution.QtyFilled ?? 0));
            }
        }

        return new OrderSnap(
            order.CreationTimeStamp,
            order.ProviderName ?? "",
            order.Symbol ?? "",
            order.ClOrdId ?? "",
            order.Quantity,
            order.PricePlaced,
            order.Side.ToString(),
            OrderBlotter.FillPercent(order.FilledQuantity, order.Quantity),
            order.FilledQuantity,
            OrderBlotter.AverageFill(order.FilledPrice, executions),
            order.TimeInForce.ToString(),
            order.OrderType.ToString(),
            order.Status.ToString(),
            order.BestBid,
            order.BestAsk,
            order.Status);
    }
}

internal sealed class MarketBatch
{
    public MarketBatch(
        bool hasWork,
        IReadOnlyList<BookSnapshot> books,
        IReadOnlyList<TradePrint> trades,
        TradePrint? latestTrade,
        IReadOnlyList<PositionUpdate> positions,
        IReadOnlyList<ProviderUpdate> providers,
        IReadOnlyList<NotificationUpdate> notifications)
    {
        HasWork = hasWork;
        Books = books;
        Trades = trades;
        LatestTrade = latestTrade;
        Positions = positions;
        Providers = providers;
        Notifications = notifications;
    }

    public bool HasWork { get; }
    public IReadOnlyList<BookSnapshot> Books { get; }
    public IReadOnlyList<TradePrint> Trades { get; }
    public TradePrint? LatestTrade { get; }
    public IReadOnlyList<PositionUpdate> Positions { get; }
    public IReadOnlyList<ProviderUpdate> Providers { get; }
    public IReadOnlyList<NotificationUpdate> Notifications { get; }
}

internal readonly record struct PositionUpdate(
    string Symbol,
    double Net,
    double Buy,
    double Sell,
    double Exposure,
    double WorkingBuy,
    double WorkingSell,
    double Total,
    double Realized,
    double Open,
    DateTime Updated,
    IReadOnlyList<OrderSnap> Orders);

internal readonly record struct ProviderUpdate(
    int Code,
    string Name,
    eSESSIONSTATUS Status,
    string Tooltip,
    IPlugin? Plugin);

internal readonly record struct NotificationUpdate(
    int Id,
    string Title,
    string Message,
    DateTime Timestamp,
    bool IsRead,
    HelprNorificationManagerTypes Type);
