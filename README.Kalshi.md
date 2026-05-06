# Kalshi Setup (Paulo's fork)

This fork of [VisualHFT](https://github.com/visualHFT/VisualHFT) adds UI windows
and helpers for trading prediction markets on
[Kalshi](https://kalshi.com): strike ladder, per-market ladder, events browser,
watch list, implied PMF, depth chart, and a demo-only order panel.

The UI lives in **this repo**. The actual data-feed plugin (Kalshi WebSocket /
REST → VisualHFT order books and trades) lives in a **separate repo**:

- Plugin: <https://github.com/Paulo-BatistaFerraz/VisualHFT-Kalshi>
  (the working folder is `visualhft-kalshi/`).

Cloning this repo alone gets you the Kalshi UI, but no data will appear until
the plugin is built and dropped into VisualHFT's plugin folder, because the UI
is a consumer of order-book/trade events normalised by the plugin.

## Two-repo setup

1. Clone this repo (UI):

   ```sh
   git clone https://github.com/Paulo-BatistaFerraz/VisualHFT.git
   ```

2. Clone the plugin repo and build the `visualhft-kalshi` plugin DLL:

   ```sh
   git clone https://github.com/Paulo-BatistaFerraz/VisualHFT-Kalshi.git
   ```

   Drop the resulting `visualhft-kalshi.dll` into VisualHFT's plugin folder so
   `PluginManager` picks it up at startup. The plugin reports the same
   provider ID/name the UI expects (see
   `Helpers/KalshiBrowserPoller.cs`).

3. Configure your Kalshi demo PEM (only required if you want to use the
   in-app demo order panel — view-only ladder, events browser, watch list,
   etc. don't need it):

   - Set the `KALSHI_DEMO_PEM` environment variable to the full path of your
     `kalshi-demo.pem`, **or**
   - Place the file at `%USERPROFILE%\.visualhft\kalshi-demo.pem`.

   The resolver in `Helpers/KalshiTradeHelper.cs` checks the env var first,
   then the user-profile location, then a legacy hardcoded path (kept so the
   author's existing setup keeps working — not required for new clones).

4. Open `VisualHFT.sln`, build, and run as usual. The Kalshi windows light up
   automatically once order books for Kalshi tickers start arriving from the
   plugin.

## What this fork adds vs. upstream

Approximately 3.4k lines added, additive only — no upstream files removed.

- `View/Kalshi*Window.xaml(.cs)` — strike ladder, per-market ladder, events
  browser, watch list, implied PMF.
- `ViewModel/vmKalshi*.cs` — view-models backing those windows.
- `Helpers/Kalshi*.cs` — supplemental browser-poller (richer prod book),
  event catalog, demo trade helper.
- Small tweaks to `View/Dashboard.xaml(.cs)`, `View/ucDepth1.xaml`,
  `ViewModel/vmOrderBook.cs`, and `VisualHFT.Commons/UserSettings/enums.cs`
  to wire up the Kalshi UI and persist settings.

See <https://github.com/Paulo-BatistaFerraz/VisualHFT-Kalshi> for the plugin
side.
