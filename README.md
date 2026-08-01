# JP-QuantForge ⚡

A personal monorepo for production-grade cTrader Automate (cBots) engineered for low-latency and deterministic execution.

## 🤖 Robot Catalog

| Robot Name | Target Folder | Execution Style | Default Timeframe |
| :--- | :--- | :--- | :--- |
| **Grid** | `Grid` | Grid / Position Scaling | Any |
| **LinReg Intercept v6** | `LinReg Intercept v6` | Regression Intercept Reversal | M15 / H1 |
| **Momentum V6** | `Momentum V6` | Momentum Acceleration + ATR Trail | H1 / H4 |

---

## 🛠️ Mobile Build & Deployment Protocol

1. **Modify Code:** Edit source files inside Termux and test syntax via git commits.
2. **Push Changes:** Execute `git push` to `main`.
3. **Trigger Workflow:**
   - Go to GitHub Repository -> **Actions** -> **Build cTrader Algorithms**.
   - Tap **Run workflow**.
   - Input the exact **Target Folder** name from the catalog table above.
4. **Deploy:** Download the generated `.algo` artifact from GitHub Actions directly into **cTrader Mobile**.
