# SMA Cross

Self-contained cTrader Automate (cBot) implementing an adaptive Simple Moving Average Crossover strategy with ATR risk management, time session filtering, and diagnostic logging.

## ⚙️ Key Parameters
- **Fast SMA Period / Slow SMA Period:** Moving average lookbacks.
- **Session Filter:** Restrict trades between `StartHourUTC` and `EndHourUTC`.
- **Exit Modes:** Close on opposite cross, initial ATR Stop Loss, and optional ATR Trailing Stop.
- **Risk Management:** Fixed Lots or Risk Percentage with multi-asset volume normalization.
