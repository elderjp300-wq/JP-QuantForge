# Momentum V6

Production-grade cTrader Automate (cBot) implementing TradingView **Momentum Signals v6** parity with deterministic candle-close execution and risk management.

## 📐 Core Formula

$$\text{Momentum}_t = \text{Close}_t - \text{Close}_{t - \text{Length}}$$

- **Long Entry:** $\text{Momentum}_t > 0$ **AND** $\text{Momentum}_t > \text{Momentum}_{t-1}$ (State $\le 0$)
- **Short Entry:** $\text{Momentum}_t < 0$ **AND** $\text{Momentum}_t < \text{Momentum}_{t-1}$ (State $\ge 0$)

## ⚙️ Parameters

| Parameter | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Momentum Length** | `int` | `12` | Lookback period for price difference |
| **ATR Length** | `int` | `14` | Wilder's ATR lookback |
| **ATR Multiplier** | `double` | `2.0` | Multiplier for trailing stop distance |
| **Sizing Mode** | `Enum` | `RiskPercentage` | `FixedLots` or `RiskPercentage` |
| **Risk Percentage** | `double` | `1.0%` | Capital percentage risked per trade |
| **Max Spread** | `double` | `3.0` | Maximum spread in pips allowed for entries |

## 🛡️ Invariants
- **Candle-Close Execution:** Evaluates signals and trails stops strictly on `OnBar()`.
- **Non-Repainting:** Operates solely on confirmed closed bars (`Last(1)`).
- **Position Ratchet:** Stop Loss only moves in profit direction; never widens.
