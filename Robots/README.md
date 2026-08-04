# 🏛️ JP-QuantForge: cBot Engineering Playbook

This playbook defines the required C# API standards, execution architecture, and pre-flight validation rules for all cBots in this repository.

---

## 1. Development Pipeline

1. **Requirements Mapping:** Define signal rules, entry triggers, stop loss parameters, and risk sizing.
2. **Mathematical Normalization:** Map all indicator logic strictly to closed candles (`Last(1)` and `Last(2)`) to eliminate repainting and backtest artifacts.
3. **C# Code Synthesis:** Build using standardized parameter groups, enum dropdowns, and error-handling blocks.
4. **CI/CD & Mobile Audit:** Verify compilation via GitHub Actions artifact builds and test UI/execution on cTrader Mobile.

---

## 2. cTrader Automate API Rules

* **Data Series:** Use `Bars` and `Bars.ClosePrices` (never legacy `MarketSeries`).
* **Volume Handling:** Always pass volume through `Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down)` and check against `Symbol.VolumeInUnitsMin`.
* **Crossover Logic:** Always evaluate closed candles:
  * `longCross = (fastPrev <= slowPrev) && (fastCurr > slowCurr)`
  * `shortCross = (fastPrev >= slowPrev) && (fastCurr < slowCurr)`
  where `curr` = `Last(1)` and `prev` = `Last(2)`.

---

## 3. Core Architectural Standards

### Dual Trailing Engine & Journal Hygiene
* **`AtrTrail` Mode:** Evaluates in `OnBar()` on candle close. Enforces a minimum step filter (`AtrStepPips = 0.5`) so `ModifyPosition()` only fires on meaningful price moves, keeping the cTrader journal silent.
* **`PipsTrail` Mode:** Delegates trailing directly to cTrader's native server engine by passing `hasTrailingStop: true` to `ExecuteMarketOrder()`. Displays the native **TSL badge** on charts.
* **`Disabled` Mode:** Standard fixed Stop Loss.

### Transient Spread Queueing
Never discard entries permanently due to temporary spread spikes at candle open. If `Spread > MaxSpreadPips` during `OnBar()`, store `_pendingSignal` and re-check on subsequent ticks in `OnTick()`. Execute immediately once spread normalizes.

### Atomic Reversals
When an opposite signal triggers:
1. Call `ClosePosition()` on the active trade.
2. Confirm `closeResult.IsSuccessful` before invoking `ExecuteMarketOrder()` to prevent margin lockups.

### Direct UTC+1 Session Control
Convert server time to local West Africa Time (WAT / UTC+1) internally using `Server.Time.AddHours(1).Hour`. Allows direct input of local trading hours without manual timezone conversions.

---

## 4. Pre-Flight Validation Checklist

Before declaring a cBot complete, verify:

- [ ] **No Syntax Errors:** Uses current `cAlgo.API` namespaces and types.
- [ ] **Closed-Bar Execution:** Signals run on `Last(1)` and `Last(2)` only.
- [ ] **Volume Guardrails:** Volume is normalized and checked against `Symbol.VolumeInUnitsMin`.
- [ ] **Diagnostic Logging:** Order execution captures `result.IsSuccessful` and prints `result.Error` on rejections.
- [ ] **Transient Spread Handling:** Spread spikes trigger a pending signal queue rather than dropping trades.
- [ ] **Atomic Reversal:** Active opposite positions are closed before new orders are dispatched.
- [ ] **Mobile UI Grouping:** All parameters use `[Parameter(Group = "...")]` attributes and enums for clean mobile rendering.

---

## 5. Troubleshooting & Common Pitfalls

| Issue | Root Cause | Engineering Solution |
| :--- | :--- | :--- |
| **Journal spam (`ModifyPosition`)** | ATR trail running on every tick without a step buffer. | Evaluate ATR trail in `OnBar()` only; enforce `AtrStepPips >= 0.5`. |
| **Silent order failure ("Ghost Trade")** | Uncaptured broker rejection or sub-minimum volume. | Print `result.Error` and validate against `Symbol.VolumeInUnitsMin`. |
| **Missed entries on volatile candles** | Spread exceeded limit exactly at the `OnBar()` tick. | Store `_pendingSignal` and retry execution on subsequent ticks inside `OnTick()`. |
| **Reversal order rejected** | Margin locked because old position was still closing. | Implement atomic reversal: await `ClosePosition()` success before opening new order. |
| **Missing TSL badge on chart** | Native trailing flag omitted from order parameters. | Pass `hasTrailingStop: true` when `TrailingType == TrailingMode.PipsTrail`. |
