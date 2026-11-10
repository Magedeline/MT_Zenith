# MT_Zenith

MT_Zenith is a map viewer and editor for Celeste `.bin` maps, made for the **CEL (Desolo Zantas)** mod.
It uses the same Lönn plugins, autotiler and decals as the game, links live to a running Celeste, and has built-in AI tooling (MCP server and ACP agent panel). With that tooling, agents can plan, build and verify whole maps and see/hear the mod's images, audio and video.

It was called **CEL Map Viewer** before.

## Requirements

- Windows 10/11 x64.
- Celeste with [Everest](https://everestapi.github.io/) installed. The CEL mod (`Mods/CEL` or another `Celestellaris` mod folder) is needed for CEL entities. Other maps open too, and vanilla assets come from the game.
- `MT_Zenith-<ver>-win-x64.zip` is self-contained, so you don't need .NET.
- `…-fxdependent.zip` is smaller but needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or newer.
- Optional: put `ffmpeg` on `PATH` (or set `CEL_FFMPEG`). Without it there are no video frame grabs or audio decoding for non-WAV files.
- Optional: the [PCGHelper](https://github.com/Magedeline/PCGHelper) mod in `Mods/PCGHelper` for procedural generation (or set `MT_ZENITH_PCGHELPER`, or use **PCG ▸ Locate PCGHelper…**).

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
- **PCG ▸ Generate rooms (Ctrl+G):** the seleCte Markov generator with best-of-N candidates, artefact repair, a target difficulty tier, a mechanics filter, a movement-model check of every room, a preview, and **Train model…** to learn a new model from the open map or from installed collab maps of a tier.
- **AI ▸ MCP server:** serves `http://localhost:32271/mcp` as `mt-zenith`. *AI ▸ Copy MCP client config* gives ready-to-paste snippets.
- **AI tab (Ctrl+Shift+A):** runs an ACP agent (Claude, Codex, Copilot, Gemini…) against the open map. You can attach images, audio and video.
  - **Strongest effort** switches the agent to its most capable model and highest effort, auto-allows the map tools, prepends a build-and-verify directive to every prompt and continues automatically when a turn limit stops the agent.
  - **Permissions** chooses how the agent's permission requests are answered: ask, auto-allow map tools (edits are undoable), or auto-allow all.
  - **Design ▾** inserts a workflow prompt: build a complete level, design a few rooms, generate with PCG and refine, review difficulty, polish.
- **Headless:**
  - `MT_Zenith.exe --mcp [--map file.bin] [--read-only] [--port N]`
  - `--mcp-bridge [url]`
  - `--selftest`

Settings, caches and custom PCG models live in `%APPDATA%\MT_Zenith`. Old `%APPDATA%\CELMapViewer` settings are copied over on first run.

## Agent tools (MCP)

Besides reading, rendering and editing rooms, tiles and entities, the `mt-zenith` server gives an agent what it needs to build a complex map and know that it works:

| Area | Tools |
|---|---|
| Build | `build_room` (a whole room in one call: placement by attaching to another room's side, size tier, fill, fg/bg rows or draw ops, carved exits, entities, auto spawn, analysis), `draw_tiles` (rect, outline, lines, flood fill, stairs, platforms, exits, replace), `connect_rooms` (matching openings between touching rooms), `duplicate_room` (with mirroring), `batch` (several tools as one undo step with rollback) |
| Verify | `analyze_room` (movement-model reachability: jump, fall, one dash, climb with stamina, wall jumps, swimming, springs/boosters/feathers, rides and flings off zip movers and moving platforms; lethal spikes/spinners/lightning; which exits and items are reachable, min tech per exit, hazard pressure, routes, overlay image), `analyze_map` (openings connected / blocked / void, rooms reachable and traversable from the start, rooms that need advanced tech), `validate_map`, `compare_to_tier` |
| Plan | `design_notes` (concept, tier, plan, todo, log kept beside the map as `<map>.design.json` and shown to the agent every turn), prompts `build_map`, `design_level`, `pcg_level`, `fix_room`, `review_difficulty`, `polish_map` |
| PCG | `pcg_models`, `pcg_generate` (candidates, repair, tier, mechanics, preview, per-room judgement), `pcg_import`, `pcg_train` (new Markov models from the open map or reference maps of a tier) |
| Reference | `list_reference_maps`, `study_reference_map`, `difficulty_profile` (statistics of installed collabs per tier) |
| Game & media | `game_*` (teleport, reload, screenshot, log), `list_media`, `view_image`, `compare_images`, `analyze_audio`, `analyze_video` |

The movement model is an approximation of Celeste physics (jump ≈ 3 tiles up / 6 across, dash ≈ +4 tiles, stamina ≈ 10 tiles of climbing), not a simulation: it catches unreachable exits, missing ground and blocked openings, and tells which exits need a dash or advanced tech, but a human playtest still decides the feel.

## Building

```
dotnet build CELMapViewer.csproj
pwsh ./publish.ps1 -SelfTest        # release zips -> dist/
```

Releases are built by `.github/workflows/release.yml` when a `v<version>` tag is pushed (bump `<Version>` in `CELMapViewer.csproj`).

This repository is the standalone copy of `Tools/CELMapViewer` from the CEL mod ([Magedeline/CEL](https://github.com/Magedeline/CEL)). To develop against the mod, clone it into `Celeste/Mods/CEL/Tools/CELMapViewer` or set `CELESTE_DIR`.
