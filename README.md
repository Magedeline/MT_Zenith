# MT_Zenith

MT_Zenith is a map viewer and editor for Celeste `.bin` maps, made for the **CEL (Desolo Zantas)** mod.
It uses the same Lönn plugins, autotiler and decals as the game, links live to a running Celeste, and has built-in AI tooling (MCP server and ACP agent panel). With that tooling, agents can edit maps and see/hear the mod's images, audio and video.

It was called **CEL Map Viewer** before.

## Requirements

- Windows 10/11 x64.
- Celeste with [Everest](https://everestapi.github.io/) installed. The CEL mod (`Mods/CEL` or another `Celestellaris` mod folder) is needed for CEL entities. Other maps open too, and vanilla assets come from the game.
- `MT_Zenith-<ver>-win-x64.zip` is self-contained, so you don't need .NET.
- `…-fxdependent.zip` is smaller but needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or newer.
- Optional: put `ffmpeg` on `PATH` (or set `CEL_FFMPEG`). Without it there are no video frame grabs or audio decoding for non-WAV files.

## Install

1. Unzip anywhere and run `MT_Zenith.exe`.
2. MT_Zenith looks for Celeste in this order:
   1. The folders above the exe. For example, a source build in `Celeste/Mods/CEL/Tools/CELMapViewer`.
   2. `CELESTE_DIR`.
   3. **File ▸ Set Celeste folder…**.
   4. Steam libraries.
   5. Olympus installs.
   6. Common install paths.
3. If it gets it wrong, use **File ▸ Set Celeste folder…** and restart.

To open the editor from inside the game with the `cel_mapviewer` console command or `/cel/editor/launch`, set `MT_ZENITH_EXE` to the full path of `MT_Zenith.exe`.

## Use

- **Open a map:** File ▸ Open (`Mods/CEL/Maps/CEL/...`).
- **Tools:** Select (V), Room (R), Brush (B), Rect (T), Fill (F), Line (L), Place (P) and Decal (D).
- **Live link:** Game ▸ Launch Celeste. Turn on Everest debug mode so the editor can teleport, reload and read the log on port 32270.
- **AI ▸ MCP server:** serves `http://localhost:32271/mcp` as `mt-zenith`. *AI ▸ Copy MCP client config* gives ready-to-paste snippets.
- **AI tab (Ctrl+Shift+A):** runs an ACP agent (Claude, Codex, Copilot, Gemini…) against the open map. You can attach images, audio and video.
- **Headless:**
  - `MT_Zenith.exe --mcp [--map file.bin] [--read-only] [--port N]`
  - `--mcp-bridge [url]`
  - `--selftest`

Settings and caches live in `%APPDATA%\MT_Zenith`. Old `%APPDATA%\CELMapViewer` settings are copied over on first run.

## Building

```
dotnet build CELMapViewer.csproj
pwsh ./publish.ps1 -SelfTest        # release zips -> dist/
```

Releases are built by `.github/workflows/release.yml` when a `v<version>` tag is pushed (bump `<Version>` in `CELMapViewer.csproj`).

This repository is the standalone copy of `Tools/CELMapViewer` from the CEL mod ([Magedeline/CEL](https://github.com/Magedeline/CEL)). To develop against the mod, clone it into `Celeste/Mods/CEL/Tools/CELMapViewer` or set `CELESTE_DIR`.