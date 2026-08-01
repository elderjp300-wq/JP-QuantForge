# JP-QuantForge ⚡


A professional monorepo for production-grade cTrader Automate (cBots) engineered for low-latency, deterministic execution, and seamless mobile deployment.

## 🤖 Active Robot Catalog

| Robot Name | Target Folder | Execution Style | Primary Timeframe | Target Assets |
| :--- | :--- | :--- | :--- | :--- |
| **EMA Cross** | `EMA Cross` | Exponential MA Crossover + Session Filter | M5 / M15 | EURUSD / Major FX |
| **Grid** | `Grid` | Grid Execution / Position Scaling | M5 / M15 | EURUSD / Major FX |
| **LinReg Intercept v6** | `LinReg Intercept v6` | Regression Intercept Reversal | M5 / M15 | EURUSD / FX Majors |
| **Momentum V6** | `Momentum V6` | Momentum Acceleration + ATR Trail | M5 (Primary) / M15 | EURUSD / XAUUSD |
| **SMA Cross** | `SMA Cross` | Simple MA Crossover + Session Filter | M5 / M15 | EURUSD / Major FX |

---

## 🏛️ Monorepo Architecture

Each robot is strictly self-contained within the `Robots/` directory:

```text
JP-QuantForge/
├── README.md               <-- Tier 1: Global Catalog, Build Pipeline & Troubleshooting
└── Robots/
    ├── EMA Cross/          <-- EMA Crossover Strategy
    ├── Grid/               <-- Grid Trading System
    ├── LinReg Intercept v6/ <-- Linear Regression Strategy
    ├── Momentum V6/        <-- Momentum Acceleration System
    └── SMA Cross/          <-- SMA Crossover Strategy

---
```

## 🚀 JP-QuantForge Build & Deployment Protocol

### 🛠️ The Working GitHub Actions Workflow
This configuration leverages automatic .algo packaging built natively into the cTrader.Automate NuGet package (Version 1.0.17+). It targets a specific bot folder manually via your phone's browser or the GitHub app.

**Workflow Location:** `.github/workflows/build-algo.yml`

```yaml
name: Build cTrader Algorithms

on:
  workflow_dispatch:
    inputs:
      robot_folder:
        description: 'Target Robot Folder Name (e.g., Momentum V6)'
        required: true
        default: 'Momentum V6'
        type: string

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Verify Robot Path
        run: |
          TARGET_DIR="Robots/${{ github.event.inputs.robot_folder }}"
          if [ ! -d "$TARGET_DIR" ]; then
            echo "ERROR: Folder '$TARGET_DIR' does not exist."
            exit 1
          fi

      - name: Build and Auto-Package .algo
        run: |
          mkdir -p artifacts
          TARGET_DIR="Robots/${{ github.event.inputs.robot_folder }}"

          PROJECT_FILE=$(find "$TARGET_DIR" -name "*.csproj" | head -n1)
          echo "Building and packaging: $PROJECT_FILE"

          dotnet build "$PROJECT_FILE" -c Release

          find "$TARGET_DIR" -name "*.algo" -exec cp {} artifacts/ \;

      - name: Upload Official .algo Artifact
        uses: actions/upload-artifact@v4
        with:
          name: ${{ github.event.inputs.robot_folder }}-algo
          path: artifacts/*.algo
          if-no-files-found: error
```

### 📋 Mandatory .csproj File Requirement
For the GitHub Action to auto-package the .algo binary successfully, every robot's .csproj file **must** use at least version 1.0.17 of the Automate API.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="cTrader.Automate" Version="1.0.17"/>
  </ItemGroup>
</Project>
```

### 📱 How to Compile From Mobile (Termux + GitHub)
1. Edit and commit your C# code via **Termux**.
2. Push your changes: `git push origin main`.
3. Open GitHub Mobile / Browser -> Navigate to **Actions** -> **Build cTrader Algorithms**.
4. Tap **Run workflow**, enter the target folder name (e.g., Momentum V6), and execute.
5. Download the .algo artifact zip from the run summary, extract it, and tap the .algo file to instantly install it into **cTrader Mobile**.

## 🛠️ Engineering Troubleshooting History & Lessons Learned

### 🚨 Issue 1: Missing cTrader.Automate.CLI Tool
*   **The Problem:** The default Spotware pipeline script attempted to run `dotnet tool install -g cTrader.Automate.CLI` to package the bot. This threw a critical error because Spotware does not host this CLI packaging tool publicly on standard NuGet feeds (api.nuget.org/v3/index.json).
*   **The Fix:** We completely removed the external global CLI dependency step from our workflow. Instead, we rely entirely on standard, native .NET SDK build triggers to compile our code.

### 🚨 Issue 2: Empty/Placeholder Template Projects Crashing the Build
*   **The Problem:** The original repository layout contained blank placeholder subfolders (Template002, Template003, etc.) with empty .csproj files. A broad directory scanning loop (`find Robots -name "*.csproj"`) tried to build everything alphabetically. MSBuild hit an empty 0-byte XML template file, threw an error `MSB4025: Root element is missing`, and aborted the pipeline before reaching functional bot folders.
*   **The Fix:** We converted the GitHub Action trigger to use `workflow_dispatch` with a manual text input variable (`robot_folder`). The script now uses a targeted path check to isolate and build *only* the specific active robot directory you specify from your phone, gracefully skipping unused templates.

### 🚨 Issue 3: The Missing/Rejected .algo Package Format on Mobile (The Core Breakthrough 💡)
*   **The Problem:** Standard Linux compilation output yields a raw .dll binary assembly file. However, cTrader Mobile completely rejects raw .dll binaries; it requires a native .algo source package to allow cloud importing and deployment. Manually zipping up directory files from the runner failed because cTrader Mobile looks for highly specific structural parameters within the package.
*   **The Root Cause:** Initial sample bot templates referenced old, generic versions of the cTrader.Automate NuGet dependency library which did not include integrated archive routines.
*   **The Ultimate Fix:** **We updated the .csproj configuration to reference cTrader.Automate version 1.0.17 or higher.** Starting with version 1.0.8, Spotware bundled the official .algo compiler/packager routine *directly inside the library's standard build pipeline hooks*. Running a standard `dotnet build` against version 1.0.17+ automatically creates a perfectly encoded, natively compliant .algo file right inside your build output folder without requiring any external zip code.
