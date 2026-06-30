# minecraft-nbt-tool

CLI scanner for Minecraft NBT-style blueprint files. It reads binary NBT and SNBT, extracts block usage, and groups the output by style, building type, and level.

## What it returns

- A per-file breakdown of block counts for each blueprint or schematic.
- A grouped list of unique blocks per style, building type, and level.

## Usage

PowerShell:

```powershell
dotnet run -- scan "path\to\blueprints" --output report.json
```

Bash:

```bash
dotnet run -- scan "path/to/blueprints" --output report.json
```

Export CSV reports:

PowerShell:

```powershell
dotnet run -- scan "path\to\blueprints" --csv out\minecolonies
```

Bash:

```bash
dotnet run -- scan "path/to/blueprints" --csv out/minecolonies
```

Start the local browser UI:

PowerShell:

```powershell
dotnet run -- serve "path\to\blueprints" --port 5055
```

Bash:

```bash
dotnet run -- serve "path/to/blueprints" --port 5055
```

From VS Code integrated terminal (in the project root):

PowerShell:

```powershell
dotnet run -- serve . --port 5055
```

Bash:

```bash
dotnet run -- serve . --port 5055
```

When using the browser UI, you can either paste a GitHub folder URL manually or use the `Theme URL preset` selector for common sources:

- StyleColonies: https://github.com/ldtteam/stylecolonies/tree/release/main/src/main/resources/blueprints/stylecolonies
- MineColonies: https://github.com/ldtteam/minecolonies/tree/version/main/src/main/resources/blueprints/minecolonies

You can also enter a local folder path (or `|` separated file paths) in the `Local folder or file` field.

When a selected source looks like a multi-theme root (including the two built-in presets), the UI defers the full blueprint scan and first prompts you to select one or more styles. This avoids loading all themes up front.

Optional filters:

PowerShell:

```powershell
dotnet run -- scan "path\to\blueprints" --style acacia --building composter --level 1
```

Bash:

```bash
dotnet run -- scan "path/to/blueprints" --style acacia --building composter --level 1
```

## Supported files

- `.nbt`
- `.blueprint`
- `.dat`
- `.schem`
- `.schematic`
- `.snbt`

## Cross-platform packaging

Create self-contained single-file builds:

Windows x64:

```powershell
dotnet publish -c Release -r win-x64 -o out/win-x64
```

Linux x64:

```bash
dotnet publish -c Release -r linux-x64 -o out/linux-x64
```

Linux ARM64:

```bash
dotnet publish -c Release -r linux-arm64 -o out/linux-arm64
```

macOS Intel:

```bash
dotnet publish -c Release -r osx-x64 -o out/osx-x64
```

macOS Apple Silicon:

```bash
dotnet publish -c Release -r osx-arm64 -o out/osx-arm64
```

## GitHub Packages publishing

The GitHub Actions workflow `.github/workflows/build-cross-platform.yml` publishes zipped executables to GitHub Packages (GHCR) as OCI artifacts when builds run on `main` (and on manual dispatch).

Package name:

```text
ghcr.io/<owner>/minecraft-nbt-tool
```

Tags created per platform:

- `<rid>-<commitSha>` (for example `win-x64-<sha>`)
- `<rid>-latest` (updated on `main`)

To pull a package with ORAS:

PowerShell:

```powershell
oras pull ghcr.io/<owner>/minecraft-nbt-tool:win-x64-latest
```

Bash:

```bash
oras pull ghcr.io/<owner>/minecraft-nbt-tool:win-x64-latest
```

The pulled artifact is a zip containing the executable and `README.md`.

## GitHub Releases on version tags

The workflow `.github/workflows/release-on-tag.yml` builds all supported platform binaries and publishes zip assets to a GitHub Release.

Trigger it by pushing a version tag such as:

PowerShell:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

Bash:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Release assets include:

- `minecraft-nbt-tool-<version>-win-x64.zip`
- `minecraft-nbt-tool-<version>-linux-x64.zip`
- `minecraft-nbt-tool-<version>-linux-arm64.zip`
- `minecraft-nbt-tool-<version>-osx-x64.zip`
- `minecraft-nbt-tool-<version>-osx-arm64.zip`

## Notes

- Files are grouped by the first folder under the scan root as the style.
- The building type is derived from the file name.
- If the file name ends with digits, those digits are treated as the level.
- The extractor currently targets palette-driven NBT layouts and packed block-data layouts.
- Block names are normalized to base names only (for example `architectscutter`), with mod namespace in a separate field (for example `domum_ornamentum`).
- The app no longer depends on PowerShell for local path selection.