# Trackmania Ghost Embed CLI

Embed a `RaceValidateGhost` into a Trackmania map by passing:

1. `Map.Gbx` path
2. `Ghost.Gbx` path

The tool auto-decompresses inputs with `gbxlzo`, injects the ghost, then recompresses the final map.

## Usage

```powershell
dotnet run -- "<Map.Gbx>" "<Ghost.Gbx>" [Out.Map.Gbx]
```

Examples:

```powershell
dotnet run -- "C:\maps\track.Map.Gbx" "C:\maps\run.Ghost.Gbx"
dotnet run -- "track.Map.Gbx" "run.Ghost.Gbx" "track_withghost.Map.Gbx"
dotnet run -- "track.Map.Gbx" "run.Ghost.Gbx" --gbxlzo "C:\tools\gbxlzo.exe"
```

If output is omitted, it defaults to:

```text
<MapName>_withValidationGhost.Map.Gbx
```

in the same folder as the input map.

## Requirements

- .NET SDK 10
- `gbxlzo.exe` (in current folder, on PATH, or provided via `--gbxlzo`)

## Useful Options

- `--out <path>`: explicit output path
- `--gbxlzo <path>`: explicit `gbxlzo.exe` path
- `--temp <folder>`: custom temp root
- `--keep-temp`: keep temp files for debugging
