# Blob Rush
[Agar.io](https://agar.io/)-style multiplayer game made in [Unity 2D](https://unity.com/) for Android/iOS, with a standalone .NET server (`Server/CellSimulator.Server`) authoritative over movement, splitting, eating, and hazards; the Unity client is a thin visual proxy driven by UDP snapshots. Targets Unity 6000.5.3f1 (originally started as a single-player prototype on 2021.3.22f, hence some older/simpler code still in `Assets/`).

Here's a quick [video](https://youtu.be/kuOGl9nbOqI) made demonstrating an earlier, single-player version of the project.

And some screenshots:
![](https://i.imgur.com/H1kcxDU.png)
![](https://i.imgur.com/OZQHChD.png)
![](https://i.imgur.com/yn3TShs.png)

## Running the server

```
cd Server
dotnet run --project CellSimulator.Server
```

Listens on UDP `7778` (game protocol) and HTTP `5000`/`5282` (`/health`, `/stats`, `/leaderboard`). Point the Unity client at it via `MainMenuHandler.DEFAULT_SERVER_IP`. Run `dotnet test` from `Server/` for the game-rules test suite.

### Tuning the game

Every feel-related number lives in one place, `Server/CellSimulator.Server/Game/GameConfig.cs`, and can be overridden **without recompiling** from the `Game` section of `appsettings.json` or an environment variable (e.g. `Game__SplitForce=1.3`; with the Docker container add `-e Game__SplitForce=1.3` to `docker run`). Out-of-range values are repaired at startup.

| Setting | Meaning |
|---|---|
| `SplitForce` | Strength of a split/pop launch - scales distance and peak speed together |
| `SplitDistance`, `SplitDistancePerScale` | Launch distance: base + per unit of the piece's scale |
| `SplitSpeed` | Peak launch speed (u/s); the throw eases in and out, never starts/stops abruptly |
| `MergeTimeMin` / `MergeTimeMax` | Merge cooldown after a split, picked per split (default 23-26 s) |
| `MinSpeed` / `MaxSpeed` / `ScoreToSpeedMultiplier` | Mass -> speed: `MaxSpeed / (1 + k * (sqrt(mass) - sqrt(MassMin)))`, clamped to [Min, Max] |
| `SpikySplitThreshold` / `SpikySplitCount` | A spike splits a cell into `2 + floor(mass / threshold)` pieces, at most `count` - deterministic |
| `SpikyFoodCount` / `SpikyFoodLaunchDistance` | Pellets a spike hit throws out of the new pieces, and how far they may fly |
| `MapWidth` / `MapHeight` | Size of the default map (the other map-size choices are 2x/3x/5x of it); food, bots, spikes and capacity scale with it |
| `PowerupSeconds` / `PowerupRespawnSeconds` / `SpeedBoostMultiplier` / `MagnetRadiusMultiplier` | Power-up (speed / shield / magnet) duration, respawn delay and strength |
| `BotDifficulty` | `Easy` / `Normal` / `Hard`: how far bots see, how fast they move, whether they dodge spikes |

### Features at a glance

- **Split / merge / spikes / food / speed / map** rules are described above and tuned from `GameConfig`.
- **Power-ups** (speed, shield, magnet) lie on the map; a cell touching one grants it to the whole player group.
- **Death report** (who ate you, peak mass, cells eaten...) drives XP, levels, stats and achievements on the client.
- **Persistent leaderboard** (day / week / all-time, `leaderboard.json` in the `cellsim-data` volume), shown in the main menu's *Top players* and at `/leaderboard/top`.
- **Colour choice** in the main menu (sanitised server-side), ping display, connection-lost banner, volume button, mini-map, first-run hint.
- **Sound effects** are synthesised (`python Tools/generate_sfx.py` regenerates `Assets/Resources/Audio/*.wav`), so there's no third-party audio to license.
- **Operations:** `/health`, `/stats`, `/metrics` (Prometheus text: tick time vs the 33 ms budget, snapshot sizes, counters), abuse limits (packet rate, joins and sessions per address), slow-tick log lines.

### Multiplayer protocol notes

Every addition is backwards compatible with already-shipped clients:

- `Welcome` carries a trailing map half-height; `Snapshot` carries a trailing list of which player entities belong to which player.
- The client announces a protocol version (and optional colour) after the map size in `Join`. Version 2 clients get the **compact snapshot** (`SnapshotV2`: quantised positions, name/colour only occasionally, power-ups, effect flags), `Died`, `Pong` and `TopList` messages; older clients keep getting the original format.
- A snapshot is capped at 1400 bytes (one network packet): each player only receives what its camera can see (`Net/InterestManager.cs`). Food changes go to everyone, and every snapshot also carries a few pellets in rotation, so a lost update heals within ~85 s.
- `Assets/_Scripts/Game/SnapshotDecoder.cs` is plain C# with no Unity dependency and is **compiled into the server's test project**, so the wire format is verified against the real client decoder.

### Testing

| What | How |
|---|---|
| Rules, protocol, multi-player simulation, real-socket sessions | `cd Server && dotnet test` |
| Load / soak test (N fake players over the real protocol; checks snapshot rate, packet size, food delivery) | `dotnet run --project Server/CellSimulator.LoadBot -- <host> [bots] [seconds] [udpPort] [httpPort]` |
| The Unity client, headless-ish: a bot that eats, splits, chases pickups, and takes screenshots | Build with `-executeMethod CiWindowsBuild.Build`, run a local server, then `BlobRush.exe -autopilot game -server 127.0.0.1 -shots <dir>` (or `-autopilot menu`). It writes screenshots and `autopilot.log`. |

### Deploying

`Server/deploy.sh [user@host]` runs the tests, **publishes the app locally**, ships only the output, builds a small runtime image on the VPS, swaps the `cellsimulator-server` container (with a persistent `cellsim-data` volume and a higher CPU weight), health-checks it and rolls back to the previous image if it's unhealthy (needs key-based ssh; defaults to the production box). Building on the server is avoided on purpose: the box is shared and heavily loaded, and a nightly `docker image prune -af` there deletes the .NET SDK image.

The server needs CPU headroom to hold 30 ticks per second; check `/metrics` (`blob_tick_p99_ms` should stay well under 33). On a box that is saturated by other workloads the game will stutter no matter what the code does.
