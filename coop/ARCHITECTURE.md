# ARCHITECTURE — GTA 5 Story Co-op (v0.9.1)

Teknisk referanse for hvordan hele **GTA 5 Story Co-op v0.9.1** er bygget opp, slik at neste agent kan feilsøke, utvide og rulle ut endringer på sekunder uten gjetting.

---

## 1. Systemoversikt (3-lags arkitektur + Dual-Stack Nettverk)

```text
[PC 1: GTA5.exe (Host)]                              [PC 2: GTA5.exe (Guest)]
   └─ scripts\CoopBridge.dll                             └─ scripts\CoopBridge.dll
        │ UDP 127.0.0.1:27510 (inn)                           │ UDP 127.0.0.1:27510 (inn)
        ▼ UDP 127.0.0.1:27511 (ut, 40 Hz i bil)               ▼ UDP 127.0.0.1:27511 (ut, 40 Hz i bil)
   coop\gta5_coop_gui.py  ◄── UDP :27530 / HTTPS /gta5 ──► coop\gta5_coop_gui.py
   (lokal config.json)              Relay-server (.121)    (lokal config.json)
                                    coop/gta5_server.py      
```

- **LAN / VPN:** Direkte UDP mot `27530` (< 1 ms latens, hendelsesstyrt umiddelbar videresending uten sleep-buffring).
- **Internett:** valgfri HTTPS-tunnel (f.eks. Cloudflare Tunnel eller en omvendt proxy) foran relay-serverens HTTP-port; settes som `tunnel_url` i `config.json`.

---

## 2. Filer og mapper

| Fil | Beskrivelse |
|---|---|
| `Start_GTA5_Coop.bat` | Ett-klikks launcher i rotmappen som starter både Mod-GUI (`v0.9.1`) og GTA V Story Mode uten BattlEye. |
| `mod\CoopBridge.3.cs` | C#-kildekode for in-game SHVDN3-bridge (~3650 linjer). Kompileres alltid til `scripts\CoopBridge.dll` (legg aldri rå `.3.cs` i `scripts\`). |
| `scripts\cutscenes.txt` | Liste over alle 772 offisielle GTA V cutscene-navn (brukes av Vert via `HAS_THIS_CUTSCENE_LOADED` for 1:1 cutscene-synk). |
| `coop\gta5_coop_gui.py` | 100 % engelsk, sentrert moderne Tkinter-GUI (`v0.9.1`) med Lobby Create/Join, Connected Players-liste, Auto UDP+Tunnel og `Teleport to Host` kun for Guest. |
| `coop\gta5_server.py` | Dual-stack UDP + HTTP lobby- og relay-server (`27530`). |
| `coop\config.json` | Lokal per-PC konfig (`role`, `name`, `server`, `port`, `tunnel_url`, `transport`, `room`, `model`). |

---

## 3. Synkroniseringskanaler (`v0.9.1`)

| Kanal | Retning | Frekvens | Funksjon |
|---|---|---|---|
| `state` (`STATE` / `PEER`) | Begge veier | 40 Hz (bil) / 22 Hz (til fots) | Modell (`mh`), posisjon (`dist2d`), heading (`LerpAngle`), faktisk gå/løp-fart, klær/props (`0..11` + `Prop 0..1`), våpen, sikting, skyting, passiv slag-animasjon (`pflags & 64`), kjøretøy med eksakt `vhid`, `Quaternion.Slerp`, horn/lys/sirene, innstigning (`pflags & 128`) og utstigning. |
| `pvp_hit` (`PVP_HIT`) | Begge veier | Hendelse | Null-AI, null-lagg PvP-skade fra skuddlinje, slag-kjegle og påkjørsel -> `APPLY_DAMAGE_TO_PED` + 5s Co-op Respawn i Free Roam. |
| `host_cutscene` (`CUTSCENE`) | Vert → Gjest | Hendelse + 0.7 Hz sync | **1:1 Native Cutscene Sync:** Rydder/skjuler speil-entiteter og spillere (0 % duplikater), registrerer hovedroller med `CU_CREATE_AND_ANIMATE_NEW_SCRIPT_ENTITY (3)` og spiller eksakt samme cutscene via `REQUEST_CUTSCENE` + `START_CUTSCENE(0)`. |
| `host_vehs` / `host_npcs` | Vert → Gjest | 14 Hz MTU-chunks (< 1 KB) | Opptil 95 biler (500 m, `CREATE_PED_INSIDE_VEHICLE`) og 65 NPC-er (400 m) rundt både Vert og Gjest, med topprioritet for politi og helikoptre (`SET_HELI_BLADES_FULL_SPEED`). |
| `host_world` / `wanted_req` | Toveis | Ved endring / 0.7 Hz | Klokke, vær og toveis `WantedLevel` (med alle 15 lokale `ENABLE_DISPATCH_SERVICE` avslått på Gjest så kun Vertens politi eksisterer). |
| `host_props` / `host_inv` / `npc_dead` | Vert ↔ Gjest | ~1 Hz | Dører (`DOOR_SYSTEM`), porter, `RAYFIRE`, `IPL`, penger/våpen og synk av drepte NPC-er/politi. |

---

## 4. Kritiske tekniske regler (IKKE bryt disse!)

1. **ALDRI bruk `TASK_COMBAT_PED`, `SET_PED_TO_LOAD_COVER` eller `IS_PED_IN_MELEE_COMBAT` på `_guestPed`:** Det skrur på GTA Vs egen kamp-AI slik at figuren begynner å slåss eller skyte helt av seg selv i evig loop! Bruk alltid 100 % passive animasjoner (`PlayPassiveMeleeAnim` / `PlayCoverAnim` via `TASK_PLAY_ANIM`).
2. **Hold alltid `_guestPed.IsInvincible = true` + `SET_PED_SUFFERS_CRITICAL_HITS(false)`:** Uten dette utløser et vanlig knyttneveslag GTA Vs skriptede 1-slags NPC-knockout som dreper figuren momentant og skaper kraftig lagg ved `RESURRECT_PED`. Bruk i stedet vår rene `PVP_HIT`-kanal for skade!
3. **Bruk alltid 2D horisontal avstand (`dist2d`) for fotgjengere og spillere i ro:** 3D-avstand inkluderer små Z-forskjeller i bakke/modellhøyde (f.eks. Franklin vs Tracey) og må aldri trigge `TASK_GO_STRAIGHT_TO_COORD` når `_targetSpeed < 0.25f`.
4. **Skjul alltid speil-NPC-er, speil-biler og lokale spillere under 1:1 Cutscenes (`_guestCsState != 0`):** Da spawner det aldri doble karakterer oppå cutscenens egne skuespillere.
