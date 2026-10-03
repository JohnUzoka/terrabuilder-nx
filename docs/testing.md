# Testing on a Switch

How to install a build, what to check, and what to send back when something
goes wrong. Builds are tested on Switch hardware running Atmosphère and hbmenu.

## Install

Each build writes an NRO, an `sdcard/` folder, and a receipt JSON under
`~/.cache/terrabuilder/out/`. tModLoader builds use a `tmodloader-<mods>/`
subfolder.

| Target | Copy | To |
| --- | --- | --- |
| Vanilla | `out/Terraria.nro` | `sd:/switch/` |
| Vanilla | contents of `out/sdcard/` | SD card root |
| tModLoader | `out/tmodloader-<mods>/tmodloader.nro` | `sd:/switch/` |
| tModLoader | contents of `out/tmodloader-<mods>/sdcard/` | SD card root, overwriting existing files |

Every `sdcard/` payload contains `mono/config.ini` and the ICU data file
`mono/etc/icudt77l.dat`. Both NROs read these from the SD card before anything
else starts; without them the launch stops with "Can't load app config" or
"Libicu init failed". If the card already holds a `sd:/mono/config.ini` for
another mono-nx app, back it up first: this payload replaces it.

A tModLoader NRO and the `.tmod` files in its `sdcard/` payload are a matched
pair. The mod code is AOT-compiled into the NRO, so a Workshop download, or a
`.tmod` with the same name and version from another build, aborts at mod
loading with `Failed to load AOT module '<Mod>' ... doesn't match assembly`.
Install the whole `sdcard/` payload with every new NRO, and do not update these
mods from the in-game Mod Browser.

Start hbmenu in full application mode: hold R while launching any installed
game. The NROs refuse applet mode (the Album icon) and show an error saying so.

Saves are kept apart from the build output, so a new NRO does not touch them:

- Vanilla: `sd:/switch/terraria/`
- tModLoader: `sd:/switch/tmodloader/Terraria/tModLoader/` (which also holds
  `Mods/`)

## Checks

Vanilla:

1. Boots to the title screen with music.
2. Menus respond to the controller (sticks, D-pad, A/B).
3. A new character and world can be created, and the world plays with sound
   effects.
4. Save and Exit, then reload the same character and world.
5. Holding Plus and Minus together toggles the on-screen FPS counter.
6. Joins a multiplayer server hosted on another device.
7. Quitting from the title screen returns to hbmenu.

tModLoader:

1. Mod loading completes and the main menu appears.
2. The Mods menu lists the built mods as enabled.
3. A world can be created and entered, with audio.
4. Save and Exit, then reload.

## Logs

| Path | Contents |
| --- | --- |
| `sd:/mono/log.txt` | Launcher and Mono runtime log for both NROs. It appends across launches, so rename or delete it before a run you want to report. |
| `sd:/tModLoader-Logs/` | tModLoader's `client.log` and `environment-client.log`; earlier sessions move to `Old/`. |
| `sd:/atmosphere/crash_reports/`, `sd:/atmosphere/fatal_reports/` | Atmosphère crash reports. |

After a fatal Mono error the launcher writes the error to `sd:/mono/log.txt`
and then aborts on purpose. The abort leaves an Atmosphère crash report with
result `2347-0093`; the cause is in the log, not the report.

Release builds keep diagnostics quiet. Build with `--profile debug` for frame,
GPU, and audio diagnostics in the log.

## Reporting a problem

Include:

- the NRO SHA-256 (`nro.sha256` in the build's receipt JSON);
- the target and, for tModLoader, the `--mods` selection;
- what you did and what happened;
- the logs above from that run.

## Known issues

Vanilla:

- Hosting a multiplayer game from the Switch is not supported; joining works.

tModLoader (experimental):

- In-world performance is very slow.
- The Plus+Minus FPS toggle has not been confirmed working.
- Multiplayer and extended play are untested.
- Only the curated open-source mods in `terrabuilder_pkg/curated_tmod_mods.json`
  can be built.
