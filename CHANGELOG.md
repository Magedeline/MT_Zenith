# Changelog

## 1.1.0

Update from 1.0.0: replace `MT_Zenith.exe` with the new one (or unzip over the old folder). Settings, caches and PCG models in `%APPDATA%\MT_Zenith` are kept.

**New**
- **Room properties dialog** (Ctrl+R, also used for *New room*): camera offset, colour, wind, flags, checkpoint, music and ambience.
- **Project menu**: choose campaign/map, new empty or standard project, open/resync project, new map in a campaign, zip the mod.
- **Presets menu**: save selected entities/triggers as presets or preset groups (relative positions kept) and place them at the mouse.
- **Scripts menu**: run installed mods' Lönn scripts on the current room or all rooms through an emulated Lönn script API, undoable.
- Ahorn `@mapdef`/`@pardef` entities and triggers in the palette.
- Convert a room's FG tiles to filler rectangles and back; fillers drawn on the canvas.
- **Asset credits** (Assets menu): every decal, styleground, tileset, entity and trigger the map uses with who to credit, matched via Maddie480's mod database; exports `credits.txt`. Also an `asset_credits` MCP tool.
- **Community Asset Drive** browser: author readmes, "Recommended for this map", add files to your mod with their credit recorded.
- **UI themes** (View > Theme): 13 themes including Dark, Celeste, Desolo Zantas, Strawberry Jam, Spring Collab and Game Boy, or System.
- **Agent tools**: room/map reachability analysis (`analyze_room`, `analyze_map`), `build_room`, `draw_tiles`, `connect_rooms`, `duplicate_room`, atomic `batch`, design notes, Strongest-effort mode and permission policy in the agent panel.
- **PCG pipeline**: best-of-N candidates scored by traversability and tier, repair, mechanics filter, previews, and custom Markov models trained from your maps.

**Fixes**
- `get_room` / `study_reference_map` with tiles failed on newer .NET runtimes.
- Failing edit tools roll back instead of leaving half-applied changes.
