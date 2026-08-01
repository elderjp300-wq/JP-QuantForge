# EMA Cross

Self-contained cTrader Automate (cBot) implementing an exponential moving average (EMA) crossover strategy with ATR risk management, time-of-day session filtering, and diagnostic logging.

## ⚙️ Key Parameters
- **Fast EMA Period / Slow EMA Period:** Exponential moving average lookback periods.
- **Session Filter:** Restrict new trade triggers between `StartHourUTC` and `EndHourUTC`.
- **Exit Modes:** Close on opposite crossover, initial ATR Stop Loss, and optional ATR Trailing Stop.
- **Risk Management:** Fixed Lots or Risk Percentage with multi-asset volume normalization.
