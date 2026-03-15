I've made a frontend for the tool, you can find it here:

[https://tools.xjk.yt/Strip-RaceValidationGhost/](https://tools.xjk.yt/Embed-RaceValidationGhost/)

Very useful if you don't wanna build from source and/or don't want to run some randos (me xD) exe

---

# Trackmania Ghost Embed CLI

Embed a `RaceValidateGhost` into a Trackmania map by passing:

1. `Map.Gbx` path
2. `Ghost.Gbx` or `Replay.Gbx` path

The tool auto-decompresses inputs with `gbxlzo`, reads a ghost from ghost/replay input, injects it, then recompresses the final map.

## Usage

```powershell
dotnet run -- "<Map.Gbx>" "<GhostOrReplay.Gbx>" [Out.Map.Gbx]
```

Examples:

```powershell
dotnet run -- "C:\maps\track.Map.Gbx" "C:\maps\run.Ghost.Gbx"
dotnet run -- "C:\maps\track.Map.Gbx" "C:\maps\run.Replay.Gbx"
dotnet run -- "C:\maps\track.Map.Gbx" "C:\maps\pack.Replay.Gbx" --ghost-index 2
dotnet run -- "track.Map.Gbx" "run.Ghost.Gbx" "track_withghost.Map.Gbx"
dotnet run -- "track.Map.Gbx" "run.Ghost.Gbx" --gbxlzo "C:\tools\gbxlzo.exe"
```

If output is omitted, it defaults to:

```text
<MapName>_withValidationGhost.Map.Gbx
```

in the same folder as the input map.

If a replay/clip contains multiple ghosts, you can select which one to embed with `--ghost-index` (default: `0`).
If there is only one ghost in the input, that ghost is always used (the index is ignored).

## Embedded Signature Metadata

Each output map gets a transparent signature entry in `ScriptMetadata` (default key: `EmbedRaceValidationGhost`).

It stores open metadata such as:

- tool/signature text
- UTC embed timestamp
- source file names and source type (Ghost/Replay/Clip)
- requested/used ghost index and available ghost count
- selected ghost identity fields (`GhostUid`, validation fields, race time)

You can override the metadata key with environment variable `TM_EMBED_META_KEY` (or legacy `TM_META_KEY`).

## Requirements

- .NET SDK 10
- `gbxlzo.exe` (in current folder, on PATH, or provided via `--gbxlzo`)

## Useful Options

- `--out <path>`: explicit output path
- `--gbxlzo <path>`: explicit `gbxlzo.exe` path
- `--ghost-index <n>`: select ghost index from replay/clip input (0-based)
- `--temp <folder>`: custom temp root
- `--keep-temp`: keep temp files for debugging

## License Notes

This project depends on `GBX.NET` (MIT) and calls external `gbxlzo.exe` for compression/decompression.

See:

- `THIRD_PARTY_NOTICES.md` for license context and redistribution notes.
- `LICENSES/GPL-3.0.txt` for the GNU GPL v3 license text relevant when redistributing `gbxlzo.exe`.
