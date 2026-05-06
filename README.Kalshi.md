# Kalshi Setup

This fork of [VisualHFT](https://github.com/visualHFT/VisualHFT) adds UI
windows and helpers for trading prediction markets on
[Kalshi](https://kalshi.com): strike ladder, per-market ladder, events
browser, watch list, implied PMF, depth chart, and a demo-only order panel.

The UI lives in **this repo**. The actual data-feed plugin (Kalshi WebSocket
/ REST → VisualHFT order books and trades) lives in a **separate repo**:

- Plugin: <https://github.com/Paulo-BatistaFerraz/VisualHFT-Kalshi>
  (the working folder is `visualhft-kalshi/`).

Cloning this repo alone gets you the Kalshi UI, but no data will appear
until the plugin is built and dropped into VisualHFT's plugin folder,
because the UI is a consumer of order-book/trade events normalised by the
plugin.

## Two-repo setup

1. Clone this repo (UI):

   ```sh
   git clone https://github.com/Paulo-BatistaFerraz/VisualHFT.git
   ```

2. Clone the plugin repo and build the `visualhft-kalshi` plugin DLL:

   ```sh
   git clone https://github.com/Paulo-BatistaFerraz/VisualHFT-Kalshi.git
   ```

   Drop the resulting `visualhft-kalshi.dll` into VisualHFT's plugin folder
   so `PluginManager` picks it up at startup. The plugin reports the same
   provider ID/name the UI expects (see `Helpers/KalshiBrowserPoller.cs`).

3. **Configure your Kalshi credentials.** Nothing is hardcoded — supply
   your own via environment variables. Two scopes are independent:

   | Env var               | What it is                                                  | Required for                                  |
   | --------------------- | ----------------------------------------------------------- | --------------------------------------------- |
   | `KALSHI_PROD_KEY_ID`  | Your Kalshi **prod** access-key id (UUID)                   | Events Browser, Watch List, Strike/Per-market ladder data, browser-poller |
   | `KALSHI_PROD_PEM`     | Full path to your prod RSA private key in PEM format        | same as above                                 |
   | `KALSHI_DEMO_KEY_ID`  | Your Kalshi **demo** access-key id (UUID)                   | Demo-only in-app order panel                  |
   | `KALSHI_DEMO_PEM`     | Full path to your demo RSA private key in PEM format        | same as above                                 |

   Generate a key pair from the Kalshi web UI (Settings → API keys) for each
   environment you want to use. Kalshi keeps the public side; you keep the
   PEM.

   The PEM env vars are optional — if unset, the resolver also checks
   `%USERPROFILE%\.visualhft\kalshi-prod.pem` and
   `%USERPROFILE%\.visualhft\kalshi-demo.pem`. The key-id env vars are
   required (no defaults).

   View-only features will run without the demo creds; the order panel
   will throw a clear error pointing at the missing variable. Likewise
   without prod creds the polling helpers log a warning and skip work
   instead of crashing the UI.

4. Open `VisualHFT.sln`, build, and run as usual. The Kalshi windows light
   up automatically once order books for Kalshi tickers start arriving from
   the plugin.

## What this fork adds vs. upstream

Approximately 3.4k lines added, additive only — no upstream files removed.

- `View/Kalshi*Window.xaml(.cs)` — strike ladder, per-market ladder, events
  browser, watch list, implied PMF.
- `ViewModel/vmKalshi*.cs` — view-models backing those windows.
- `Helpers/Kalshi*.cs` — supplemental browser-poller (richer prod book),
  event catalog, demo trade helper, plus a small `KalshiCredentials`
  resolver used by all three.
- Small tweaks to `View/Dashboard.xaml(.cs)`, `View/ucDepth1.xaml`,
  `ViewModel/vmOrderBook.cs`, and `VisualHFT.Commons/UserSettings/enums.cs`
  to wire up the Kalshi UI and persist settings.

See <https://github.com/Paulo-BatistaFerraz/VisualHFT-Kalshi> for the
plugin side.
