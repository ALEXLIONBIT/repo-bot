# repo-bot

An autonomous player for **R.E.P.O.** (Steam, Unity/Mono), written as a BepInEx mod for Linux/Proton.
It reads the game's own state from inside the process and presses the game's own inputs, so it needs no screenshots
and reacts in real time: it picks valuables, carries them to the extraction point, flees from enemies, goes shopping,
uses the upgrades it buys, unlocks and equips cosmetics, and moves on to the next level by itself.

> Unofficial and unaffiliated with the game. It includes cheats (auto-revive, auto-heal, teleports). Use it in
> singleplayer, or only in a lobby where everybody has agreed. It never obeys other players' chat.

## Setup

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (win x64, tested with 5.4.23.5) by unzipping it into the game folder.
2. In Steam, R.E.P.O. > Properties > Launch options: `WINEDLLOVERRIDES=winhttp=n,b %command%`
3. `./start.sh --build` builds the plugin and the brain, installs them and starts the game through Steam.
   `./start.sh --check` only reports what is missing; `./start.sh --on` also switches the bot on once the mod has loaded (or press **F9** in game).

The game folder is found through Steam's library list (`REPO_DIR=<folder>` overrides it). The plugin and the brain
reach `run/` through a hardcoded Wine path (`Z:\home\ale\repo-bot\run`, in `plugin/Plugin.cs` and `brain/Brain.cs`);
if you clone the repo somewhere else, change those two constants (`start.sh` warns about it).

The menus are not automated. For a first run pick SINGLEPLAYER > NEW GAME (not one of your saves), or join a lobby;
the bot takes over from there.

## Layout

| path | what it is |
|---|---|
| `plugin/` | the fixed half: Harmony patches on `InputManager` so the bot can "press" keys, plus a loader that hot-reloads `run/Brain.dll`. Changing it needs a game restart. |
| `brain/` | the decision logic, rebuilt while the game runs: `Brain.cs` (main loop and state machine), `BrainSmart.cs` (target choice, danger memory), `BrainShop.cs`, `BrainDeath.cs` (death reports, learned enemy avoidance, auto-heal/revive), `BrainPowers.cs`, `BrainTruck.cs`, `BrainUpgrades.cs`, `BrainChat.cs`. |
| `tools/dump/` | lists the types, fields and methods of the game's `Assembly-CSharp.dll` (`dump <Managed dir> list [regex]`, `show <regex>`). |
| `tools/input/` | a virtual keyboard and mouse over `/dev/uinput`, driven through a FIFO; used for the menus. |

The brain is compiled against the game's own assemblies (`refs/Managed` links to `REPO_Data/Managed`); none of the
game's or BepInEx's binaries are kept in the repo.

## Controlling it while it runs

Write a command into `run/cmd.txt` (the bot reads and deletes the file): `on`, `off`, `dump`, `skip`, `revive`,
`autorevive on|off`, `autoheal on|off`, `superpowers on|off`, `chat on|off`, `say <text>`.
`run/state.json` shows the current level, health, state and extraction points; the log is `BepInEx/LogOutput.log` in the game folder.
Things it learns between runs (`learned.txt`, `homes.txt`, `deaths.log`) are kept in `run/` and are not committed.
