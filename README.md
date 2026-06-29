# mc-nbt-tool

CLI scanner for Minecraft NBT-style blueprint files. It reads binary NBT and SNBT, extracts block usage, and groups the output by style, building type, and level.

## What it returns

- A per-file breakdown of block counts for each blueprint or schematic.
- A grouped list of unique blocks per style, building type, and level.

## Usage

```bash
dotnet run -- scan "path\to\blueprints" --output report.json
```

Export CSV reports:

```bash
dotnet run -- scan "path\to\blueprints" --csv out\minecolonies
```

Start the local browser UI:

```bash
dotnet run -- serve "path\to\blueprints" --port 5055
```

When using the browser UI, enter a local folder path (or `|` separated file paths) in the `Local folder or file` field.

Optional filters:

```bash
dotnet run -- scan "path\to\blueprints" --style acacia --building composter --level 1
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

```bash
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

## Notes

- Files are grouped by the first folder under the scan root as the style.
- The building type is derived from the file name.
- If the file name ends with digits, those digits are treated as the level.
- The extractor currently targets palette-driven NBT layouts and packed block-data layouts.
- Block names are normalized to base names only (for example `architectscutter`), with mod namespace in a separate field (for example `domum_ornamentum`).
- The app no longer depends on PowerShell for local path selection.