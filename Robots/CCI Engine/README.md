# CCI Engine

A custom Close-Based Commodity Channel Index (CCI) state machine trading robot.

## ⚙️ Core Innovations
- **Custom Close-Based CCI:** Bypasses Typical Price to compute SMA and Mean Deviation purely on completed `ClosePrices`.
- **State Machine Engine ($P \in \{-1, 0, +1\}$):** Eliminates signal duplication by tracking state transitions across $+T$ and $-T$ thresholds.
- **Reboot State Sync:** Reconstructs position state on `OnStart` to prevent orphan orders upon terminal restarts.
- **Risk Management:** ATR Stop Loss & Trailing Stop with session filtering and risk percentage sizing.
