# Hull & LinReg Forecast

Self-contained cTrader Automate (cBot) implementing a hybrid trend-following strategy using a **Linear Regression Forecast** as the fast signal line crossing over a **Hull Moving Average (HMA)** as the smooth trend baseline.

## ⚙️ Key Parameters
- **LinReg Forecast Period (Fast):** Lookback period for linear regression endpoint forecast.
- **Hull MA Period (Slow):** Lookback period for the responsive Hull Moving Average.
- **Session Filter:** Restrict entry signals within `StartHourUTC` and `EndHourUTC`.
- **Exit & Risk Rules:** ATR SL/Trailing Stop, optional opposite-crossover exit, and multi-asset position sizing.
