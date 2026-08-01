# JP-QuantForge

A professional monorepo for my cTrader algorithmic trading systems.

## Structure

Each robot is self-contained and includes:

- Solution (.sln)
- Project (.csproj)
- Source (.cs)
- Documentation (README)

This repository serves as my personal library of trading robots and templates.

---

## 🚀 JP-QuantForge Build Documentation

### 🛠️ The Working GitHub Actions Workflow
This configuration leverages the automatic `.algo` packaging built into the `cTrader.Automate` NuGet package (Version `1.0.17+`). It targets a specific bot folder manually via your phone's browser or the GitHub app, completely bypassing empty placeholder templates.

**Workflow Location:** `.github/workflows/build-algo.yml`

```yaml
name: Build cTrader Algorithms

on:
  workflow_dispatch:
    inputs:
      robot_folder:
        description: 'Target Robot Folder Name (e.g., Grid)'
        required: true
        default: 'Grid'
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
          TARGET_DIR="Robots/\${{ github.event.inputs.robot_folder }}"
          if [ ! -d "\$TARGET_DIR" ]; then
            echo "ERROR: Folder '\$TARGET_DIR' does not exist."
            exit 1
          fi

      - name: Build and Auto-Package .algo
        run: |
          mkdir -p artifacts
          TARGET_DIR="Robots/\${{ github.event.inputs.robot_folder }}"
          
          PROJECT_FILE=\$(find "\$TARGET_DIR" -name "*.csproj" | head -n1)
          echo "Building and packaging: \$PROJECT_FILE"
          
          dotnet build "\$PROJECT_FILE" -c Release

          find "\$TARGET_DIR" -name "*.algo" -exec cp {} artifacts/ \;

      - name: Upload Official .algo Artifact
        uses: actions/upload-artifact@v4
        with:
          name: \${{ github.event.inputs.robot_folder }}-algo
          path: artifacts/*.algo
          if-no-files-found: error
```

### 📋 Mandatory `.csproj` File Requirement
For the GitHub Action to auto-package the `.algo` file successfully, every robot's `.csproj` file **must** use at least version `1.0.17` of the Automate API. 

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="cTrader.Automate" Version="1.0.17" />
  </ItemGroup>
</Project>
```

### 📱 How to Compile From Your Phone
1. Push your C# changes via Termux.
2. Open GitHub Web/Mobile App -> Navigate to **Actions** -> Select **Build cTrader Algorithms**.
3. Click **Run workflow**, input the folder name (e.g., `Grid`), and hit execute.
4. Download the generated artifact zip, extract it, and tap the `.algo` file to instantly install it into **cTrader Mobile**.
