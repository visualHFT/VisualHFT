# FXMacroData Macro Event Risk Study

This VisualHFT study reads the public FXMacroData USD release calendar and exposes a simple risk-state metric:

- `1` while a confirmed, top-tier USD release is within the configured pre- or post-release window.
- `0` at all other times.

It adds a risk control to the dashboard and trigger-rule metric picker for scheduled macroeconomic releases.

## Use

Add **FXMacroData Macro Event Risk** to the VisualHFT dashboard, then configure the number of minutes before and after a release that should be treated as a risk window. The study refreshes the calendar periodically and triggers its alert once when each risk window starts.

The public calendar endpoint does not require an FXMacroData API key. The study only uses rows explicitly marked as confirmed and top-tier for USD, so it does not infer future release times.
