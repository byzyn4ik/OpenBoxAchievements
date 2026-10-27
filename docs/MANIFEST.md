# Game manifest

Each supported game is one file, `games\<game>\achievement_manifest.json`,
beside the app. The folder name is the game's id (used by `--game <id>` and by
the [events file](EVENTS.md)). The manifest holds only what can be shared
freely: no achievement texts or art.

```json
{
  "Name": "Forza Horizon 2",
  "XboxTitleId": 446059611,
  "Process": "Forza Horizon 2",
  "Achievements": ["1", "2", "3"]
}
```

| Key | Meaning |
|---|---|
| `Name` | the game's name |
| `XboxTitleId` | the Xbox Live title ID (decimal); `0` for a manual game. Used to load the texts and art from Xbox |
| `Process` | the game's EXE name without `.exe`: time played, and which game the page opens on |
| `Achievements` | the achievement IDs, as on Xbox Live (or `1`, `2`, … for a manual game) |

OpenBox has no achievement rules: the game's mod decides unlocks and sends
them (see [EVENTS.md](EVENTS.md)). Achievements unlock by ID even before
their texts are loaded.

## Local data (not shared)

Everything else is per user, in `%LOCALAPPDATA%\OpenBoxAchievements`:

| Path | Contents |
|---|---|
| `games\<game>\achievement_data.json` | names, descriptions, gamerscore, rarity (loaded from Xbox) |
| `games\<game>\<id>.jpg`, `boxart.jpg`, `background.jpg` | art (loaded from Xbox; `background.jpg` you may add yourself) |
| `games\<game>\state.json` | your unlocks, progress, time played |
| `games\<game>\ingame_state.json` | the game mod's own counters and unlocks (FH2's `fh2-achievements.dll` writes it; OpenBox only reads its unlocks) |
| `openbox.ini` | settings |
| `account.bin` | Xbox sign-in token, encrypted for your Windows account |
| `error.log` | diagnostics |

A manual game's own texts and images (`achievement_data.json`, `<id>.jpg`)
are written beside its manifest instead, since they are yours to share.
