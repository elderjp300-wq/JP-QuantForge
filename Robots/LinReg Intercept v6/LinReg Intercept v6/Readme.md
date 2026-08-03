# Linear Regression Intercept V6

## Overview

Linear Regression Intercept V6 is a production-ready cTrader cBot implementing the Linear Regression Intercept (LRI) crossover strategy.

The objective is to reproduce the behavior of the cTrader Linear Regression Intercept indicator as closely as possible while maintaining clean, reliable, and maintainable C# code.

Correctness is prioritized over optimization.

---

# Strategy

## Buy Entry

Enter a Buy position when:

- Previous candle Close <= Previous LRI
- Current candle Close > Current LRI

Signals are evaluated only after the candle closes.

Entries are executed from `OnBar()`.

No intra-bar entries.

---

## Sell Entry

Enter a Sell position when:

- Previous candle Close >= Previous LRI
- Current candle Close < Current LRI

Signals are evaluated only after the candle closes.

Entries are executed from `OnBar()`.

No intra-bar entries.

---

# Exit Logic

Positions may only be closed by:

- ATR Stop Loss
- ATR Trailing Stop (when enabled)
- Opposite crossover signal

There is no Take Profit.

---

# Stop Loss

Every position must have a Stop Loss.

The initial Stop Loss is calculated as:

ATR × ATR Multiplier

Once the trade has been placed, the Stop Loss remains broker-managed.

If price reaches the Stop Loss before the candle closes, the broker should close the position immediately.

The cBot must not delay Stop Loss execution until candle close.

After a Stop Loss exit, the bot waits for the next valid completed-bar crossover before opening another trade.

---

# Trailing Stop

Optional.

When enabled:

- Uses ATR distance.
- Never increases risk.
- Never moves the Stop Loss away from price.
- Only tightens the Stop Loss.

---

# Parameters

## Core

- Price Source
- LRI Length

Defaults:

- Source = Close
- Length = 9

---

## ATR

- ATR Length
- ATR Multiplier

Defaults:

- ATR Length = 14
- ATR Multiplier = 2

---

## Trailing

- Enable ATR Trailing Stop

Default:

Enabled

---

## Position Sizing

Supported methods:

- Fixed Lots
- Risk Percentage

Parameters:

- Fixed Lot Size
- Risk Percent

Only one sizing method is active at a time.

---

# Risk Management

## Per Trade Risk

Every trade must have a Stop Loss.

When using Risk Percentage sizing:

The bot must never intentionally exceed the configured account risk.

If broker minimum volume or Stop Loss distance would require risking more than the configured amount, the trade is rejected.

The rejection reason is recorded in the journal.

---

## Maximum Daily Loss

Parameter:

Max Daily Loss (%)

Default:

2%

The bot tracks realized losses for the current trading day.

When realized daily loss reaches the configured limit:

- No new trades may be opened.
- Existing positions continue to be managed normally.
- Trading resumes automatically on the next trading day.

---

# Trading Sessions

Trading is controlled using configurable session filters.

Supported sessions:

- Asia
- London
- New York
- London / New York Overlap

Each session has its own Enable/Disable parameter.

Only enabled sessions may generate new trades.

Internally, session calculations should remain consistent.

Journal timestamps are displayed in UTC+1.

---

# Supported Markets

Primary:

- XAUUSD
- Indices

The bot should also operate correctly on any symbol supported by cTrader.

---

# Supported Timeframes

All chart timeframes.

No timeframe-specific logic.

---

# Position Management

- One position per direction.
- Opposite signal closes the existing position before opening a new one.
- Duplicate positions are not permitted.

---

# Execution Rules

- Entries occur only on completed candles.
- No intra-bar entries.
- Broker-managed Stop Loss remains active continuously.
- No repainting logic.

---

# Logging

The bot journals important operational events, including:

- Startup
- Shutdown
- Parameters loaded
- Current trading session
- Signal detection
- Ignored signals
- Trade rejection reasons
- Daily loss limit reached
- Risk calculations
- Volume calculations
- Order submission
- Order execution
- Position closure
- Stop Loss execution
- Trailing Stop updates
- Broker errors
- Runtime exceptions

The journal should provide enough information to diagnose real operational issues.

---

# Design Goals

- Production ready
- Professional grade
- Correct before optimized
- Fully parameterized
- No hardcoded strategy values
- Maintainable
- Readable
- Reliable
- Matches the cTrader Linear Regression Intercept indicator as closely as possible
- Consistent across supported symbols and timeframes
```
