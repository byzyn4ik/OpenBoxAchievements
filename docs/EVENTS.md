# Events

OpenBox has no achievement rules. Each game's mod decides its unlocks
(Forza Horizon 2: Extra Options with `achievements\fh2-achievements.dll`,
whose rules ship with the mod) and sends only the results to OpenBox over a
local pipe. OpenBox records them, shows the pop-ups, the page and the widget.

## Results over the pipe

The mod connects to `\\.\pipe\OpenBoxAchievements.Events.v1` (read and
write), writes UTF-8 lines and ends with `end`; OpenBox applies them and
answers `ok`. Ids and times are percent-encoded.

```
hello fh2 1 fh2-achievements/1.0
unlocked fh2 35 2026-10-20T21:07:47.9243327Z
progress fh2 6 4/28
end
```

| Line | Meaning |
|---|---|
| `hello <game> <protocol> <producer> [pid=<n>]` | the mod is connected (logged); `pid` is the game's process id |
| `unlocked <game> <id> [<time>]` | unlocked in game (UTC). Recorded once with a pop-up; a resend is ignored |
| `progress <game> <id> <current>/<target>` | progress shown on the page and widget; OpenBox keeps the last value |
| `alive <game> [pid=<n>]` | heartbeat: the game is still played (older OpenBox versions ignore it) |
| `bye <game>` | the game is no longer played |
| `end` | end of the message; OpenBox answers `ok` |

`<game>` is the game's folder name under `games\` (for Forza Horizon 2:
`fh2`). The mod resends unlocks until it has an `ok`, so none is lost while
OpenBox is closed, and none is counted twice; on connecting it sends all its
progress.

## Which game is played

A game counts as played (the Game Bar widget's active game, time played, the
page opening over the game) while its mod is on the pipe: every line that
names the game keeps it connected. A mod with nothing to send says
`alive <game> pid=<n>` (with `end`) every few seconds; the FH2 plugin does
every 5 seconds. The game is no longer played after `bye <game>`, as soon as
the process given by `pid` exits, or 15 seconds after its last line. A game
whose manifest names its EXE (`Process`) also counts while that EXE runs: for
games without a mod on the pipe. FH2's manifest names none, since one
launcher (Azimuth) runs several games.

## The mod's own state

A mod may keep its counters and unlocks in
`%LOCALAPPDATA%\OpenBoxAchievements\games\<game>\ingame_state.json` (FH2's
DLL does). Only the mod writes it. OpenBox reads its `Unlocked` list, so
unlocks made while OpenBox was closed appear (with a pop-up if the mod marks
them `"Notified": false`), and never writes it.

OpenBox keeps what it was sent (unlocks, progress, time played) in
`%LOCALAPPDATA%\OpenBoxAchievements\games\<game>\state.json`.
