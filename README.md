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
