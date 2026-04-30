using System;
using System.Collections.Generic;
using System.Windows;
using VisualHFT.Helpers;
using VisualHFT.ViewModel;

namespace VisualHFT.View
{
    /// <summary>
    /// Per-ticker depth ladder for a single Kalshi market, styled to mirror
    /// kalshi.com's view (asks top in red, bids bottom in green, with cumulative
    /// dollar totals walking away from mid). Includes a demo-only order panel.
    /// </summary>
    public partial class KalshiLadderWindow : Window
    {
        private readonly string _symbol;
        private readonly vmKalshiLadder _vm;
        private readonly KalshiTradeHelper _trade;
        private readonly List<string> _myLiveOrders = new();   // most-recent-last

        public KalshiLadderWindow(string symbol)
        {
            InitializeComponent();
            _symbol = symbol;
            _vm = new vmKalshiLadder(symbol);
            DataContext = _vm;

            try { _trade = KalshiTradeHelper.ForDemo(); }
            catch (Exception ex) { _trade = null!; OrderStatus.Text = $"Trade helper unavailable: {ex.Message}"; }

            _vm.Asks.CollectionChanged += (_, _) => ScrollAsksToBottom();
            _vm.Bids.CollectionChanged += (_, _) => ScrollBidsToTop();

            this.Closed += (_, _) =>
            {
                _vm.Dispose();
                _trade?.Dispose();
            };
        }

        private void ScrollAsksToBottom()
        {
            if (AsksGrid?.Items.Count > 0)
                AsksGrid.ScrollIntoView(AsksGrid.Items[AsksGrid.Items.Count - 1]);
        }

        private void ScrollBidsToTop()
        {
            if (BidsGrid?.Items.Count > 0)
                BidsGrid.ScrollIntoView(BidsGrid.Items[0]);
        }

        private async void OrderSubmit_Click(object sender, RoutedEventArgs e)
        {
            if (_trade is null) { OrderStatus.Text = "Trade helper not initialized."; return; }

            string side   = (OrderSide.SelectedIndex   == 0) ? "yes" : "no";
            string action = (OrderAction.SelectedIndex == 0) ? "buy" : "sell";
            if (!int.TryParse(OrderPrice.Text.Trim(), out int price) || price < 1 || price > 99)
            { OrderStatus.Text = "Price must be an integer 1..99 cents."; return; }
            if (!int.TryParse(OrderCount.Text.Trim(), out int count) || count < 1 || count > KalshiTradeHelper.MAX_COUNT)
            { OrderStatus.Text = $"Count must be 1..{KalshiTradeHelper.MAX_COUNT}."; return; }

            OrderSubmit.IsEnabled = false;
            OrderStatus.Text = $"Sending {side.ToUpper()} {action} {count}@{price}¢ on demo for {_symbol}…";
            try
            {
                var r = await _trade.PlaceLimitAsync(_symbol, side, action, price, count);
                if (r.Success)
                {
                    _myLiveOrders.Add(r.OrderId);
                    OrderCancelLast.IsEnabled = true;
                    OrderStatus.Text = $"✅ Placed (demo) {side.ToUpper()} {action} {count}@{price}¢  →  status={r.Status}  id={r.OrderId.Substring(0, Math.Min(8, r.OrderId.Length))}…";
                }
                else
                {
                    OrderStatus.Text = $"❌ {r.Error}";
                }
            }
            finally { OrderSubmit.IsEnabled = true; }
        }

        private async void OrderCancelLast_Click(object sender, RoutedEventArgs e)
        {
            if (_trade is null || _myLiveOrders.Count == 0) return;
            string id = _myLiveOrders[^1];
            OrderStatus.Text = $"Canceling {id.Substring(0, Math.Min(8, id.Length))}…";
            bool ok = await _trade.CancelAsync(id);
            if (ok)
            {
                _myLiveOrders.RemoveAt(_myLiveOrders.Count - 1);
                if (_myLiveOrders.Count == 0) OrderCancelLast.IsEnabled = false;
                OrderStatus.Text = $"✅ Canceled {id.Substring(0, Math.Min(8, id.Length))}";
            }
            else
            {
                OrderStatus.Text = $"❌ Cancel failed for {id}";
            }
        }
    }
}
