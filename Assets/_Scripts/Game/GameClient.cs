using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Talks the custom UDP protocol to Server/CellSimulator.Server (see its Net/Protocol.cs). Snapshot parsing
// lives in SnapshotDecoder.cs - a plain-C# file the server's tests compile too, so the wire format is
// checked against this very decoder. The server is authoritative for every entity's position/scale/mass/
// color; this class only mirrors it visually, plus the local extras (sound, HUD, stats).
public class GameClient : MonoBehaviour
{
    /// <summary>Announced in Join: 2 = understands compact snapshots, power-ups, Died/Ping/Top messages.</summary>
    public const byte ProtocolVersion = 2;

    private enum ClientMsg : byte { Join = 1, Input = 2, Split = 3, Eject = 4, Emoji = 5, Ping = 6, Top = 7 }
    private enum ServerMsg : byte { Welcome = 1, Snapshot = 2, FoodFull = 3, EmojiEvent = 4, Died = 5, SnapshotV2 = 6, Pong = 7, TopList = 8 }
    private const byte EntityTypeVirus = 2;
    private const byte EntityTypeSaw = 3;
    private const byte EntityTypePowerup = 4;

    // Daily quest thresholds - 3 fixed quests, no general-purpose quest system. Tune here only.
    private const float QUEST_MASS_TARGET = 200f;
    private const float QUEST_SURVIVE_TARGET = 120f;
    private const int QUEST_RANK_TARGET = 3;

    public static GameClient instance;

    [SerializeField] private string serverHost = MainMenuHandler.DEFAULT_SERVER_IP;
    [SerializeField] private int serverPort = 7778;

    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private GameObject aiPrefab;
    [SerializeField] private GameObject foodPrefab;
    [SerializeField] private GameObject virusPrefab;
    [SerializeField] private GameObject sawPrefab;

    private UdpClient socket;
    private Thread receiveThread;
    private volatile bool running;
    private uint lastSnapshotTick; // receive thread only
    private readonly SnapshotDecoder decoder = new SnapshotDecoder(); // receive thread only
    private volatile bool serverIsV2;                                 // set once a compact snapshot / Died arrives

    private const float JOIN_RETRY_SECONDS = 1f;
    private float nextJoinRetryTime;

    private uint myEntityId;
    private bool welcomed;
    private float halfMapWidth = 100f;
    private float halfMapHeight = 100f;

    // ---- connection quality ----
    private const float PING_INTERVAL_SECONDS = 2f;
    private const long CONNECTION_LOST_AFTER_MS = 3000;
    private float nextPingTime;
    private volatile int pingMs = -1;
    private long lastPacketMs;             // receive thread writes, main thread reads (a torn read only skews it by a tick)
    private HudExtras hud;

    // Monotonic milliseconds (Environment.TickCount64 isn't available in Unity's .NET profile).
    private static long NowMs
    {
        get { return System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency; }
    }

    /// <summary>Round-trip time in ms, or -1 if not measured yet.</summary>
    public int PingMs { get { return pingMs; } }
    public bool ConnectionLost { get { return welcomed && NowMs - Interlocked.Read(ref lastPacketMs) > CONNECTION_LOST_AFTER_MS; } }
    public bool Welcomed { get { return welcomed; } }
    public uint MyEntityId { get { return myEntityId; } }

    /// <summary>Diagnostics for the automated test harness.</summary>
    public int OwnPieceCount { get { return ownPieces.Count; } }
    public int KnownFoodCount { get { return foodField != null ? foodField.KnownCount : 0; } }
    public int ActiveFoodObjects { get { return foodField != null ? foodField.ActiveCount : 0; } }
    public int PowerupsVisible { get { return powerups.Count; } }
    public int SnapshotsReceived { get { return snapshotsReceived; } }
    private int snapshotsReceived;

    /// <summary>When set, replaces the joystick (used by the automated test harness).</summary>
    public Vector2? DebugInput;

    // ---- hooks for the automated test player (Debug/AutoPilot.cs) ----
    public bool DebugNearestFood(Vector2 from, out Vector2 position)
    {
        position = from;
        return foodField != null && foodField.Nearest(from, out position);
    }

    public bool DebugNearestPowerup(Vector2 from, out Vector2 position)
    {
        position = from;
        float best = float.MaxValue;
        foreach (var p in powerups.Values)
        {
            float d = ((Vector2)p.transform.position - from).sqrMagnitude;
            if (d < best) { best = d; position = p.transform.position; }
        }
        return best < float.MaxValue;
    }

    public float DebugMyMass { get { return lastKnownMass; } }
    public bool DebugUsesCompactProtocol { get { return serverIsV2; } }

    public Vector2 DebugMyPosition
    {
        get { return players.TryGetValue(myEntityId, out var b) ? (Vector2)b.transform.position : Vector2.zero; }
    }

    public byte DebugMyEffects { get { return lastOwnEffects; } }

    /// <summary>Shows the end-of-life panel exactly as a real death would (for screenshots).</summary>
    public void DebugSimulateDeath()
    {
        OnServerDeath(new DeathReport { KillerName = "TestBot", PeakMass = 432f, SurvivedSeconds = 95f, FoodEaten = 31, BlobsEaten = 3, SpikesHit = 1 });
    }

    // With a version-2 server, death comes as an explicit message (killer, stats). Against an older server
    // it has to be inferred: mass was well above spawn size last tick and dropped back to ~spawn size.
    private PlayerData playerData;
    private float lifeStartTime;
    private float sessionPeakMass = PlayerBlob.MASS_MIN;
    private float lastKnownMass = PlayerBlob.MASS_MIN;
    private byte lastOwnEffects;
    private int lastOwnPieceCount = 1;
    private List<(string Name, float Mass)> lastLeaderboard = new List<(string Name, float Mass)>();

    private readonly Dictionary<uint, PlayerBlob> players = new Dictionary<uint, PlayerBlob>();
    private readonly Dictionary<uint, AIBlob> bots = new Dictionary<uint, AIBlob>();
    private readonly Dictionary<uint, VirusBlob> viruses = new Dictionary<uint, VirusBlob>();
    private readonly Dictionary<uint, SawBlob> saws = new Dictionary<uint, SawBlob>();
    private readonly Dictionary<uint, PowerupBlob> powerups = new Dictionary<uint, PowerupBlob>();
    private FoodField foodField;
    private float nextFoodRefresh;
    private readonly List<PlayerBlob> ownPieces = new List<PlayerBlob>();
    private readonly List<Vector2> ownPositions = new List<Vector2>();

    private readonly Queue<Action> mainThreadActions = new Queue<Action>();
    private readonly object queueLock = new object();

    private void Awake()
    {
        instance = this;
    }

    private void Start()
    {
        playerData = PlayerHandleData.LoadOrDefault();
        ApplyStreakAndDailyReset();
        GameAudio.ApplySavedVolume();

        // playerData.nightMode already existed (set from the Main Menu toggle) but nothing ever
        // read it - the background never actually changed. PlayerHUD.updateJoystickColor already
        // adapts joystick tint to Camera.main.backgroundColor, it just needed something to set
        // that color in the first place.
        if (Camera.main != null)
        {
            Camera.main.backgroundColor = playerData.nightMode ? Color.black : Color.white;
        }

        // Developer override: `-server host` / `-port n` on the command line (or BLOB_SERVER), e.g. to test against a local server.
        serverHost = ServerAddress.Host(serverHost);
        serverPort = ServerAddress.Port(serverPort);

        foodField = new FoodField(foodPrefab);
        hud = HudExtras.Create();

        socket = new UdpClient();
        socket.Connect(serverHost, serverPort);
        Interlocked.Exchange(ref lastPacketMs, NowMs);

        running = true;
        receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        receiveThread.Start();

        SendJoin(PlayerPrefs.GetString("username", ""));
        nextJoinRetryTime = Time.unscaledTime + JOIN_RETRY_SECONDS;
    }

    private void OnDestroy()
    {
        running = false;
        socket?.Close();

        if (playerData != null)
        {
            PlayerHandleData.Save(playerData);
        }
    }

    // GameClient.Start() is the one place PlayerData gets loaded per Game-scene session, so streak
    // and daily-quest-reset both live here instead of a second load path elsewhere.
    private void ApplyStreakAndDailyReset()
    {
        int today = Utils.secondsSinceEpoch() / 86400;

        if (playerData.lastPlayedEpochDay == today - 1)
        {
            playerData.currentStreak++;
        }
        else if (playerData.lastPlayedEpochDay != today)
        {
            playerData.currentStreak = 1;
        }
        playerData.lastPlayedEpochDay = today;

        if (playerData.questDay != today)
        {
            playerData.questDay = today;
            playerData.questMassProgress = 0f;
            playerData.questSurviveSeconds = 0f;
            playerData.questTopRankReached = 999;
            playerData.questMassDone = false;
            playerData.questSurviveDone = false;
            playerData.questRankDone = false;
        }

        Achievements.CheckNewlyUnlocked(playerData);
        PlayerHandleData.Save(playerData);
    }

    private void Update()
    {
        // UDP: the Join (or the server's Welcome) can simply be lost, which used to leave the
        // client waiting forever. The server treats a repeat Join from the same endpoint as
        // idempotent, so retrying until Welcome arrives is safe.
        if (!welcomed && Time.unscaledTime >= nextJoinRetryTime)
        {
            nextJoinRetryTime = Time.unscaledTime + JOIN_RETRY_SECONDS;
            SendJoin(PlayerPrefs.GetString("username", ""));
        }

        if (welcomed && Time.unscaledTime >= nextPingTime)
        {
            nextPingTime = Time.unscaledTime + PING_INTERVAL_SECONDS;
            SendPing();
        }

        if (hud != null)
        {
            hud.SetConnectionLost(ConnectionLost);
            hud.SetPing(ConnectionLost ? -1 : pingMs);
        }

        lock (queueLock)
        {
            while (mainThreadActions.Count > 0)
            {
                mainThreadActions.Dequeue().Invoke();
            }
        }

        RefreshFood();
    }

    // Which pellets get a GameObject depends on where the camera is, so it isn't tied to snapshot arrival.
    private void RefreshFood()
    {
        if (foodField == null || Time.unscaledTime < nextFoodRefresh)
        {
            return;
        }
        nextFoodRefresh = Time.unscaledTime + 0.25f;

        Camera cam = Camera.main;
        if (cam == null)
        {
            return;
        }

        // Half the screen's diagonal in world units, plus a margin so pellets exist just before they scroll in.
        float radius = cam.orthographicSize * Mathf.Sqrt(cam.aspect * cam.aspect + 1f) + 15f;
        foodField.Refresh(cam.transform.position, radius);
    }

    private void FixedUpdate()
    {
        if (!welcomed || !players.TryGetValue(myEntityId, out var myBlob))
        {
            return;
        }

        Vector2 dir;
        if (DebugInput.HasValue)
        {
            dir = DebugInput.Value;
        }
        else
        {
            Joystick joystick = myBlob.playerMovement != null ? myBlob.playerMovement.joystick : null;
            if (joystick == null)
            {
                return;
            }
            dir = joystick.Direction;
        }

        SendInput(dir);

        if (dir != Vector2.zero && myBlob.playerHud != null)
        {
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            myBlob.playerHud.blobPointer.rotation = Quaternion.AngleAxis(angle, Vector3.forward);
        }
    }

    // ---- sending ----

    private void SendJoin(string username)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ClientMsg.Join);
        WriteString(w, username);
        w.Write((byte)PlayerPrefs.GetInt(MainMenuHandler.MapSizePrefKey, 3));

        // Trailing fields older servers simply ignore: protocol version, and the colour the player picked (if any).
        w.Write(ProtocolVersion);
        int skin = PlayerPrefs.GetInt(SkinPicker.SkinPrefKey, -1);
        if (skin >= 0)
        {
            w.Write((byte)((skin >> 16) & 0xFF));
            w.Write((byte)((skin >> 8) & 0xFF));
            w.Write((byte)(skin & 0xFF));
        }
        Send(ms.ToArray());
    }

    private void SendInput(Vector2 dir)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ClientMsg.Input);
        w.Write(dir.x);
        w.Write(dir.y);
        Send(ms.ToArray());
    }

    private void SendPing()
    {
        var packet = new byte[5];
        packet[0] = (byte)ClientMsg.Ping;
        BitConverter.GetBytes((uint)(NowMs & 0xFFFFFFFFL)).CopyTo(packet, 1);
        Send(packet);
    }

    /// <summary>Server validates mass/cooldown - this just requests it, the server can reject silently.</summary>
    public void SendSplit()
    {
        Send(new[] { (byte)ClientMsg.Split });
    }

    public void SendEject()
    {
        Send(new[] { (byte)ClientMsg.Eject });
        GameAudio.Play("eject", 0.8f);
    }

    public void SendEmoji(byte emojiId)
    {
        Send(new[] { (byte)ClientMsg.Emoji, emojiId });
    }

    private void Send(byte[] data)
    {
        try { socket.Send(data, data.Length); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    // ---- receiving (background thread) ----

    private void ReceiveLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            byte[] data;
            try
            {
                data = socket.Receive(ref remote);
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            Interlocked.Exchange(ref lastPacketMs, NowMs);

            try
            {
                Decode(data);
            }
            catch (EndOfStreamException)
            {
                // malformed/short packet - ignore
            }
        }
    }

    private void Decode(byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (SnapshotDecoder.IsSnapshot(data[0]))
        {
            DecodeSnapshot(data);
            return;
        }

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        var type = (ServerMsg)r.ReadByte();

        switch (type)
        {
            case ServerMsg.Welcome:
            {
                uint id = r.ReadUInt32();
                int halfWidth = r.ReadInt32();
                // Older servers only send a square's half size; the height was appended later.
                int halfHeight = ms.Position + 4 <= ms.Length ? r.ReadInt32() : halfWidth;
                decoder.HalfWidth = halfWidth;
                decoder.HalfHeight = halfHeight;
                Enqueue(() => OnWelcome(id, halfWidth, halfHeight));
                break;
            }
            case ServerMsg.FoodFull:
            {
                ushort count = r.ReadUInt16();
                var chunk = new List<(uint id, Vector2 pos)>(count);
                for (int i = 0; i < count; i++)
                {
                    chunk.Add((r.ReadUInt32(), new Vector2(r.ReadSingle(), r.ReadSingle())));
                }
                Enqueue(() => ApplyFoodUpdates(chunk));
                break;
            }
            case ServerMsg.EmojiEvent:
            {
                uint entityId = r.ReadUInt32();
                byte emojiId = r.ReadByte();
                Enqueue(() => OnEmojiEvent(entityId, emojiId));
                break;
            }
            case ServerMsg.Died:
            {
                serverIsV2 = true;
                var report = DeathReport.Decode(r);
                Enqueue(() => OnServerDeath(report));
                break;
            }
            case ServerMsg.Pong:
            {
                uint sent = r.ReadUInt32();
                uint now = (uint)(NowMs & 0xFFFFFFFFL);
                pingMs = (int)Math.Min(9999u, unchecked(now - sent));
                break;
            }
            case ServerMsg.TopList:
            {
                var list = TopList.Decode(r);
                Enqueue(() => OnTopList(list));
                break;
            }
        }
    }

    private void DecodeSnapshot(byte[] data)
    {
        var snap = decoder.Decode(data);
        if (snap.IsCompact)
        {
            serverIsV2 = true;
        }

        // UDP can deliver snapshots late or out of order; applying an older one snaps every
        // blob back a tick (reads as jitter, worst over a remote server). Only a small
        // backwards window is dropped so a server restart (tick counter resets) still gets through.
        int tickDelta = (int)(snap.Tick - lastSnapshotTick);
        if (tickDelta <= 0 && tickDelta > -300)
        {
            return;
        }
        lastSnapshotTick = snap.Tick;

        Enqueue(() => OnSnapshot(snap));
    }

    private void Enqueue(Action action)
    {
        lock (queueLock)
        {
            mainThreadActions.Enqueue(action);
        }
    }

    // ---- applying state (main thread) ----

    private void OnWelcome(uint id, int halfWidth, int halfHeight)
    {
        myEntityId = id;
        welcomed = true;
        halfMapWidth = halfWidth;
        halfMapHeight = halfHeight;

        lifeStartTime = Time.time;
        sessionPeakMass = PlayerBlob.MASS_MIN;
        lastKnownMass = PlayerBlob.MASS_MIN;
        nextPingTime = 0f;

        if (Map.instance != null)
        {
            Map.instance.ApplyMapSize(halfWidth, halfHeight);
        }
    }

    private void OnSnapshot(DecodedSnapshot snap)
    {
        snapshotsReceived++;

        var seenPlayers = new HashSet<uint>();
        var seenBots = new HashSet<uint>();
        var seenViruses = new HashSet<uint>();
        var seenSaws = new HashSet<uint>();
        var seenPowerups = new HashSet<uint>();

        if (snap.Leaderboard != null)
        {
            lastLeaderboard = new List<(string Name, float Mass)>(snap.Leaderboard.Count);
            foreach (var entry in snap.Leaderboard)
            {
                lastLeaderboard.Add((entry.Key, entry.Value));
            }
        }

        foreach (var e in snap.Entities)
        {
            // A compact snapshot can mention an entity whose name/colour packet was lost; it's announced
            // again within a few seconds, so just wait for that instead of drawing it wrongly.
            if (!e.HasInfo)
            {
                continue;
            }

            Vector2 position = new Vector2(e.X, e.Y);
            Color32 color = new Color32(e.R, e.G, e.B, e.A);

            if (e.Type == 0) // EntityType.Player
            {
                seenPlayers.Add(e.Id);
                if (!players.TryGetValue(e.Id, out var blob))
                {
                    blob = Instantiate(playerPrefab).GetComponent<PlayerBlob>();
                    blob.Init(e.Id, e.Id == myEntityId, e.Name);
                    players[e.Id] = blob;
                }
                blob.username = e.Name;
                blob.ApplyState(position, e.Scale, color, e.Mass);
                blob.SetEffects(e.Effects);

                if (e.Id == myEntityId)
                {
                    HandleLocalPlayerTick(e, blob);
                }
            }
            else if (e.Type == EntityTypeVirus)
            {
                seenViruses.Add(e.Id);
                if (!viruses.TryGetValue(e.Id, out var blob))
                {
                    blob = Instantiate(virusPrefab).GetComponent<VirusBlob>();
                    blob.Init(e.Id);
                    viruses[e.Id] = blob;
                }
                blob.ApplyState(position, e.Scale, color);
            }
            else if (e.Type == EntityTypeSaw)
            {
                seenSaws.Add(e.Id);
                if (!saws.TryGetValue(e.Id, out var blob))
                {
                    blob = Instantiate(sawPrefab).GetComponent<SawBlob>();
                    blob.Init(e.Id);
                    saws[e.Id] = blob;
                }
                blob.ApplyState(position, e.Scale, color);
            }
            else if (e.Type == EntityTypePowerup)
            {
                seenPowerups.Add(e.Id);
                if (!powerups.TryGetValue(e.Id, out var pickup))
                {
                    pickup = PowerupBlob.Create(e.Id, e.Name);
                    powerups[e.Id] = pickup;
                }
                pickup.ApplyState(position, e.Scale);
            }
            else // EntityType.Ai
            {
                seenBots.Add(e.Id);
                if (!bots.TryGetValue(e.Id, out var blob))
                {
                    blob = Instantiate(aiPrefab).GetComponent<AIBlob>();
                    blob.Init(e.Id, e.Name);
                    bots[e.Id] = blob;
                }
                blob.ApplyState(position, e.Scale, color, e.Mass);
                blob.SetEffects(e.Effects);
            }
        }

        RemoveMissing(players, seenPlayers);
        RemoveMissing(bots, seenBots);
        RemoveMissing(viruses, seenViruses);
        RemoveMissing(saws, seenSaws);
        RemoveMissing(powerups, seenPowerups);

        foreach (var f in snap.Food)
        {
            foodField.SetPosition(f.Id, new Vector2(f.X, f.Y));
        }
        UpdateOwnPieces(snap.GroupOf);

        if (players.TryGetValue(myEntityId, out var mine) && mine.playerHud != null)
        {
            mine.playerHud.SetLeaderboard(lastLeaderboard);
        }
    }

    // After a split the local player owns several PlayerBlobs, but only the primary (myEntityId) is
    // "mine" - the rest look like remote players. The server tells us which entities belong to whom,
    // so the primary can frame the camera on ALL of them and the HUD can show the total score.
    private void UpdateOwnPieces(Dictionary<uint, uint> groupOf)
    {
        if (!players.TryGetValue(myEntityId, out var mine))
        {
            return;
        }

        ownPieces.Clear();
        ownPositions.Clear();
        float totalMass = 0f;
        foreach (var kv in players)
        {
            bool isMine = kv.Key == myEntityId || (groupOf.TryGetValue(kv.Key, out var owner) && owner == myEntityId);
            if (!isMine)
            {
                continue;
            }
            ownPieces.Add(kv.Value);
            ownPositions.Add(kv.Value.transform.position);
            totalMass += kv.Value.currentMass;
        }

        mine.SetOwnPieces(ownPieces);
        if (ownPieces.Count > 1 && mine.playerHud != null)
        {
            mine.playerHud.setScoreCounterText(totalMass);
        }

        // Merging pieces make a sound (splitting is heard in PlayerBlob when a piece pops smaller).
        if (ownPieces.Count < lastOwnPieceCount)
        {
            GameAudio.Play("merge", 0.8f);
        }
        lastOwnPieceCount = ownPieces.Count;

        if (hud != null)
        {
            hud.DrawMap(ownPositions, halfMapWidth, halfMapHeight);
        }
    }

    private void HandleLocalPlayerTick(DecodedEntity e, PlayerBlob blob)
    {
        // Against a server that doesn't send Died, fall back to the mass-drop heuristic.
        if (!serverIsV2)
        {
            bool died = lastKnownMass > PlayerBlob.MASS_MIN * 2f && e.Mass <= PlayerBlob.MASS_MIN * 1.05f;
            if (died)
            {
                float survivedSeconds = Time.time - lifeStartTime;
                HandleDeath(sessionPeakMass, survivedSeconds, blob, null, 0, 0, 0);
                lifeStartTime = Time.time;
                sessionPeakMass = PlayerBlob.MASS_MIN;
            }
        }

        // A little blip when the local cell picks up a pellet: mass ticks up by the small food gain.
        if (e.Mass > lastKnownMass && e.Mass - lastKnownMass <= 2.5f)
        {
            GameAudio.Play("eat", 0.35f, 0.12f, 0.07f);
        }

        // Effects: a chime the moment a new one starts, and the badges in the HUD.
        if (e.Effects != lastOwnEffects)
        {
            byte gained = (byte)(e.Effects & ~lastOwnEffects);
            if (gained != 0)
            {
                GameAudio.Play((gained & ProceduralSprites.EffectShield) != 0 ? "shield" : "powerup", 0.9f);
            }
            lastOwnEffects = e.Effects;
            if (hud != null) hud.SetEffects(e.Effects);
        }

        sessionPeakMass = Mathf.Max(sessionPeakMass, e.Mass);
        lastKnownMass = e.Mass;

        UpdateQuests(e.Mass, e.Name, lastLeaderboard, blob);
    }

    // The explicit end-of-life message from a version-2 server: who ate us and what the life amounted to.
    private void OnServerDeath(DeathReport report)
    {
        if (!players.TryGetValue(myEntityId, out var blob))
        {
            return;
        }

        float peak = Mathf.Max(report.PeakMass, sessionPeakMass);
        HandleDeath(peak, report.SurvivedSeconds, blob, report.KillerName, report.FoodEaten, report.BlobsEaten, report.SpikesHit);

        lifeStartTime = Time.time;
        sessionPeakMass = PlayerBlob.MASS_MIN;
        lastKnownMass = PlayerBlob.MASS_MIN;
    }

    private void HandleDeath(float peakMass, float survivedSeconds, PlayerBlob blob, string killerName, int foodEaten, int blobsEaten, int spikesHit)
    {
        bool isNewRecord = false;
        if (peakMass > playerData.bestMass)
        {
            playerData.bestMass = peakMass;
            isNewRecord = true;
        }
        if (survivedSeconds > playerData.bestSurvivalSeconds)
        {
            playerData.bestSurvivalSeconds = survivedSeconds;
            isNewRecord = true;
        }
        playerData.gamesPlayed++;
        playerData.playtime += Mathf.RoundToInt(survivedSeconds);
        playerData.foodEaten += foodEaten;
        playerData.playersEaten += blobsEaten;
        playerData.virusesEaten += spikesHit;
        playerData.massGained += Mathf.RoundToInt(peakMass);

        int gainedXp = Progression.ExperienceForLife(peakMass, survivedSeconds, blobsEaten);
        int levelsGained = Progression.AddExperience(playerData, gainedXp);

        var newlyUnlocked = Achievements.CheckNewlyUnlocked(playerData);
        PlayerHandleData.Save(playerData);

        GameAudio.Play("death");
#if UNITY_ANDROID || UNITY_IOS
        Handheld.Vibrate();
#endif

        if (blob.playerHud != null)
        {
            string extra = Progression.SummaryLine(killerName, gainedXp, playerData.level, levelsGained);
            blob.playerHud.ShowMatchSummary(peakMass, survivedSeconds, isNewRecord, newlyUnlocked, extra);
        }
    }

    private void UpdateQuests(float mass, string playerName, List<(string Name, float Mass)> leaderboard, PlayerBlob blob)
    {
        int today = Utils.secondsSinceEpoch() / 86400;
        if (playerData.questDay != today)
        {
            playerData.questDay = today;
            playerData.questMassProgress = 0f;
            playerData.questSurviveSeconds = 0f;
            playerData.questTopRankReached = 999;
            playerData.questMassDone = false;
            playerData.questSurviveDone = false;
            playerData.questRankDone = false;
        }

        bool justCompletedAQuest = false;

        if (!playerData.questMassDone)
        {
            playerData.questMassProgress = Mathf.Max(playerData.questMassProgress, mass);
            if (playerData.questMassProgress >= QUEST_MASS_TARGET)
            {
                playerData.questMassDone = true;
                justCompletedAQuest = true;
            }
        }

        if (!playerData.questSurviveDone)
        {
            float currentLifeSeconds = Time.time - lifeStartTime;
            playerData.questSurviveSeconds = Mathf.Max(playerData.questSurviveSeconds, currentLifeSeconds);
            if (playerData.questSurviveSeconds >= QUEST_SURVIVE_TARGET)
            {
                playerData.questSurviveDone = true;
                justCompletedAQuest = true;
            }
        }

        if (!playerData.questRankDone)
        {
            for (int i = 0; i < leaderboard.Count; i++)
            {
                if (leaderboard[i].Name == playerName)
                {
                    if (i + 1 < playerData.questTopRankReached) playerData.questTopRankReached = i + 1;
                    break;
                }
            }
            if (playerData.questTopRankReached <= QUEST_RANK_TARGET)
            {
                playerData.questRankDone = true;
                justCompletedAQuest = true;
            }
        }

        // Progress fields update every tick but only hit disk on an actual state transition (quest
        // completed / achievement unlocked) - a per-tick File.WriteAllText would be wasteful. Any
        // in-flight progress that never completes is still flushed by OnDestroy's Save().
        var newlyUnlocked = Achievements.CheckNewlyUnlocked(playerData);
        if (justCompletedAQuest || newlyUnlocked.Count > 0)
        {
            PlayerHandleData.Save(playerData);
        }

        UpdateDailyPanelDisplay(blob);
    }

    private void UpdateDailyPanelDisplay(PlayerBlob blob)
    {
        if (blob.playerHud == null)
        {
            return;
        }

        string massLine = string.Format("Quest: Reach {0} mass ({1}/{0}){2}",
            (int)QUEST_MASS_TARGET, (int)Mathf.Min(playerData.questMassProgress, QUEST_MASS_TARGET), playerData.questMassDone ? " [done]" : "");
        string surviveLine = string.Format("Quest: Survive {0}s ({1}/{0}){2}",
            (int)QUEST_SURVIVE_TARGET, (int)Mathf.Min(playerData.questSurviveSeconds, QUEST_SURVIVE_TARGET), playerData.questSurviveDone ? " [done]" : "");
        string rankLine = string.Format("Quest: Reach top {0}{1}", QUEST_RANK_TARGET, playerData.questRankDone ? " [done]" : "");
        string streakLine = string.Format("Streak: {0} day(s)", playerData.currentStreak);

        blob.playerHud.SetDailyPanelText(string.Join("\n", new[] { massLine, surviveLine, rankLine, streakLine }));
    }

    private void OnEmojiEvent(uint entityId, byte emojiId)
    {
        if (players.TryGetValue(entityId, out var blob))
        {
            blob.ShowEmoji(emojiId);
        }
    }

    private void OnTopList(TopList list)
    {
        // Answers to a leaderboard request made while playing (the main menu makes its own, see LeaderboardPanel).
        LastTopList = list;
    }

    /// <summary>The most recent leaderboard answer received in-game, if any.</summary>
    public TopList LastTopList { get; private set; }

    public void RequestTopList(byte period)
    {
        Send(new[] { (byte)ClientMsg.Top, period });
    }

    private void ApplyFoodUpdates(List<(uint id, Vector2 pos)> updates)
    {
        foreach (var (id, pos) in updates)
        {
            foodField.SetPosition(id, pos);
        }
    }

    private static void RemoveMissing<T>(Dictionary<uint, T> dict, HashSet<uint> seen) where T : Component
    {
        List<uint> toRemove = null;
        foreach (var kv in dict)
        {
            if (!seen.Contains(kv.Key))
            {
                (toRemove ??= new List<uint>()).Add(kv.Key);
            }
        }

        if (toRemove == null)
        {
            return;
        }

        foreach (var id in toRemove)
        {
            if (dict[id] != null)
            {
                Destroy(dict[id].gameObject);
            }
            dict.Remove(id);
        }
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        if (bytes.Length > 255) Array.Resize(ref bytes, 255);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }
}
