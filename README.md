# EVE Multibox Helper

A Windows helper for running several EVE Online clients without clicking in the wrong one.

## Download

**[Download the latest EveMultibox.exe](https://github.com/Brandin-Canterbury/eve-multibox-helper/releases/latest/download/EveMultibox.exe)**: a single file for 64-bit Windows, with nothing else to install. All versions and their notes are on the [releases page](https://github.com/Brandin-Canterbury/eve-multibox-helper/releases/latest).

Windows SmartScreen may warn about an unrecognised app the first time, because the exe isn't code-signed. Choose "More info" → "Run anyway".

Every change pushed to `master` is tested and published as a new release automatically.

## Features

- **Trade**: sign in with EVE SSO, then find hub-to-hub or nearby trade routes sized to your hold and wallet, plan multi-item runs, and find the best place to sell what you own. A guided widget walks you through each step.
- **Client colors and labels**: every EVE window gets a click-through colored border and a name tag, such as "MINER 2". The active client gets a thicker border.
- **Thumbnail switcher**: live previews of each client. Click one to bring that client to the front. Optional per-client focus hotkeys, such as Ctrl+Alt+1.
- **Favorites toolbox**: buttons for the EVE shortcuts you use most. Click into a client, then click a button. The header always shows which client will receive the key.
- **Log alerts**: watches EVE's game and chat logs. On a matching line it plays a sound and flashes that client's thumbnail until you switch to it. Ctrl+Alt+A jumps to the client with the newest alert. Built-in rules cover taking damage, being warp scrambled, a full hold and running out of ammo. You can add your own, such as intel channel keywords.
- **UI sync**: copies one character's window layout and overview (`core_char_<id>.dat`) to your other characters, and optionally account settings (`core_user_<id>.dat`). Every overwritten file is backed up first, and "Restore last backup" undoes the copy.

## Staying within EVE's rules

CCP bans input broadcasting and input multiplexing. The tool enforces this in code:

- Thumbnails and hotkeys only change which window is in front. They send nothing to the game.
- One toolbox click sends exactly one key chord, and only to the EVE client that is already in the foreground. If any other window is active, nothing is sent and the toolbox shows a warning.
- The toolbox never takes focus, never sends to background windows, and has no sequences, repeats, timers or log-triggered input.
- A 300 ms cooldown on each button ignores accidental double-clicks.
- Log alerts only notify. The jump hotkey only switches windows.

## Build and run

Requires the .NET 8 SDK.

```bash
dotnet run --project src/EveMultibox
```

```bash
dotnet test
```

Single-file release build: `dotnet publish src/EveMultibox -c Release -r win-x64`.

### Using it

- **☰ menu button** (bottom-right of your main screen by default) is the app's main menu:
  - **Multiboxer** shows or hides the clients strip. Borders and name tags stay either way.
  - **Settings** opens general app settings: the EVE logs folder, start with Windows, and resetting the button's position.
  - **Quit** exits the helper.
  - The button is locked in place. Hold **Alt** and drag it to move it.
- **Clients strip header:** ★ shows or hides the favorites toolbox (amber while showing). ⚙ opens the multiboxer settings inside the overlay; press it again or Esc to close them. With thumbnails turned off, the strip shrinks to its header bar.
- Multiboxer settings open automatically on first run. Alt+F4 on the strip hides it rather than quitting.

Settings are stored in `%AppData%\EveMultibox\config.json`. Use `--config <path>` to load a different file.

## Setup tips

- Favorite keys must match your EVE shortcuts (Esc → Shortcuts). The defaults are EVE's stock bindings (F1–F3 for high slots, Alt+F1, Ctrl+Space).
- For focus hotkeys, pick combinations EVE doesn't use. A registered hotkey is taken system-wide, so EVE won't see it.
- If EVE runs as administrator, Windows blocks keys from this tool unless it runs as administrator too.
- **Alerts:** EVE's chat logs record messages only, not who enters Local, so "neutral in Local" can't be detected from logs. Add an intel channel rule with your system names instead. Chat rules need EVE's "Log chat to file" setting turned on. Rules are case-insensitive regexes matched against the line with EVE's markup removed.
- **UI sync:** close every EVE client first; EVE saves settings when it exits and would overwrite the copy. Character names come from your log files. A character that has never logged in on this PC shows as "Unknown character", identified by its ID. Backups go to `%AppData%\EveMultibox\settings-backups`.
