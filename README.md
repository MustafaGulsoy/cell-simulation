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

### Multiplayer protocol notes

Additions are backwards compatible with already-shipped clients: `Welcome` carries a trailing map half-height, and `Snapshot` carries a trailing list of which player entities belong to which player (so the camera can frame every piece of a split player). Older clients simply stop reading before them.

### Deploying

`Server/deploy.sh [user@host]` runs the tests, builds the Docker image on the VPS, swaps the `cellsimulator-server` container, health-checks it and rolls back to the previous image if it's unhealthy (needs key-based ssh; defaults to the production box).
