# GTA V Story Co-op (v0.9.2)

Two-player co-op for **GTA V Legacy, Story Mode** on PC. One player is the **Host** and plays the story as usual;
the other is the **Guest**, who appears as an extra character in the host's world. A small mod GUI controls everything
(no in-game menu).

> Story Mode only. Never start GTA Online with this mod installed. You need your own legitimate copy of the game.

## What is synced
- Player position, movement, weapons, aiming, shooting and melee (hits as events, with reactions)
- Shared vehicles (driver/passengers, entering/leaving, brake lights), and vehicle damage (health, doors, windows, tyres, dents)
- Traffic, pedestrians and police around both players (mirrored from the host)
- Time of day and weather (including transitions), wanted level, doors and props
- Cutscenes (best effort), and interiors the host is in
- Guest can start from a 100% save (`coop/saves/guest/`), so the guest never runs story missions of its own

## Requirements
- GTA V Legacy (tested with 1.0.3725)
- [Script Hook V](http://www.dev-c.com/gtav/scripthookv/) and [ScriptHookVDotNet v3](https://github.com/scripthookvdotnet/scripthookvdotnet) installed in the game folder
- Python 3.10+ on both PCs (for the GUI) and a machine running the relay server (`coop/gta5_server.py`)

## Install
1. Copy `coop/`, `mod/` and `Start_GTA5_Coop.bat` into your GTA V folder.
2. Build the in-game part: `powershell -ExecutionPolicy Bypass -File mod\build.ps1`, then copy `mod\CoopBridge.dll`
   and `mod\cutscenes.txt` into `scripts\` in the GTA V folder.
3. Copy `coop/config.example.json` to `coop/config.json` and set `role` (Host/Guest), `name` and `server`.
4. Run the relay: `python coop/gta5_server.py 27530` on a PC or server both players can reach (UDP + HTTP on port 27530).
5. Start with `Start_GTA5_Coop.bat` on both PCs.

## Files
| Path | |
|---|---|
| `mod/CoopBridge.3.cs` | In-game part (ScriptHookVDotNet v3), talks to the GUI over localhost UDP |
| `coop/gta5_coop_gui.py` | Mod GUI (Tkinter): rooms, roles, model choice, status, chat |
| `coop/gta5_server.py` | Relay/lobby server (UDP + HTTP) |
| `coop/ARCHITECTURE.md` | Technical notes (Norwegian) |

## Credits
Made by NorPat. Uses Script Hook V by Alexander Blade and ScriptHookVDotNet (not included).
Not affiliated with Rockstar Games or Take-Two Interactive.

## License
GPL-3.0, see `LICENSE`.
