using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Talks the custom UDP protocol to Server/CellSimulator.Server (see its Net/Protocol.cs - this
// class must stay byte-for-byte in sync with it). Replaces Game.cs/Map.cs's old local-spawn
// model and Netcode for GameObjects entirely: the server is authoritative for every entity's
// position/scale/mass/color, this class only mirrors it visually.
public class GameClient : MonoBehaviour
{
    private enum ClientMsg : byte { Join = 1, Input = 2, Split = 3, Eject = 4, Emoji = 5 }
    private enum ServerMsg : byte { Welcome = 1, Snapshot = 2, FoodFull = 3, EmojiEvent = 4 }
    private const byte EntityTypeVirus = 2;
    private const byte EntityTypeSaw = 3;

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

    private uint myEntityId;
    private bool welcomed;

    // Server never tells us who died or why (Devour() only merges mass, no kill log) - death is
    // inferred client-side: mass was well above spawn size last tick, and dropped back to ~spawn
    // size this tick (RespawnAsNew keeps the same entity Id, so the player list never loses us).
    private PlayerData playerData;
    private float lifeStartTime;
    private float sessionPeakMass = PlayerBlob.MASS_MIN;
    private float lastKnownMass = PlayerBlob.MASS_MIN;

    private readonly Dictionary<uint, PlayerBlob> players = new Dictionary<uint, PlayerBlob>();
    private readonly Dictionary<uint, AIBlob> bots = new Dictionary<uint, AIBlob>();
    private readonly Dictionary<uint, VirusBlob> viruses = new Dictionary<uint, VirusBlob>();
    private readonly Dictionary<uint, SawBlob> saws = new Dictionary<uint, SawBlob>();
    private readonly Dictionary<uint, GameObject> food = new Dictionary<uint, GameObject>();

    private readonly Queue<Action> mainThreadActions = new Queue<Action>();
    private readonly object queueLock = new object();

    private struct EntityState
    {
        public uint Id;
        public byte Type;
        public Vector2 Position;
        public float Scale;
        public float Mass;
        public Color32 Color;
        public string Name;
    }

    private void Awake()
    {
        instance = this;
    }

    private void Start()
    {
        playerData = PlayerHandleData.LoadOrDefault();
        ApplyStreakAndDailyReset();

        socket = new UdpClient();
        socket.Connect(serverHost, serverPort);

        running = true;
        receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        receiveThread.Start();

        SendJoin(PlayerPrefs.GetString("username", ""));
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
        lock (queueLock)
        {
            while (mainThreadActions.Count > 0)
            {
                mainThreadActions.Dequeue().Invoke();
            }
        }
    }

    private void FixedUpdate()
    {
        if (!welcomed || !players.TryGetValue(myEntityId, out var myBlob))
        {
            return;
        }

        Joystick joystick = myBlob.playerMovement != null ? myBlob.playerMovement.joystick : null;
        if (joystick == null)
        {
            return;
        }

        Vector2 dir = joystick.Direction;
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

    /// <summary>Server validates mass/cooldown - this just requests it, the server can reject silently.</summary>
    public void SendSplit()
    {
        Send(new[] { (byte)ClientMsg.Split });
    }

    public void SendEject()
    {
        Send(new[] { (byte)ClientMsg.Eject });
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
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        var type = (ServerMsg)r.ReadByte();

        switch (type)
        {
            case ServerMsg.Welcome:
            {
                uint id = r.ReadUInt32();
                int half = r.ReadInt32();
                Enqueue(() => OnWelcome(id, half));
                break;
            }
            case ServerMsg.Snapshot:
            {
                uint tick = r.ReadUInt32();

                byte leaderboardCount = r.ReadByte();
                var leaderboard = new List<(string Name, float Mass)>(leaderboardCount);
                for (int i = 0; i < leaderboardCount; i++)
                {
                    string name = ReadString(r);
                    float mass = r.ReadSingle();
                    leaderboard.Add((name, mass));
                }

                ushort entityCount = r.ReadUInt16();
                var entities = new List<EntityState>(entityCount);
                for (int i = 0; i < entityCount; i++)
                {
                    entities.Add(ReadEntity(r));
                }

                ushort foodCount = r.ReadUInt16();
                var foodUpdates = new List<(uint id, Vector2 pos)>(foodCount);
                for (int i = 0; i < foodCount; i++)
                {
                    foodUpdates.Add((r.ReadUInt32(), new Vector2(r.ReadSingle(), r.ReadSingle())));
                }

                Enqueue(() => OnSnapshot(entities, foodUpdates, leaderboard));
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
        }
    }

    private static EntityState ReadEntity(BinaryReader r)
    {
        return new EntityState
        {
            Id = r.ReadUInt32(),
            Type = r.ReadByte(),
            Position = new Vector2(r.ReadSingle(), r.ReadSingle()),
            Scale = r.ReadSingle(),
            Mass = r.ReadSingle(),
            Color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()),
            Name = ReadString(r),
        };
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        if (bytes.Length > 255) Array.Resize(ref bytes, 255);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        byte len = r.ReadByte();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private void Enqueue(Action action)
    {
        lock (queueLock)
        {
            mainThreadActions.Enqueue(action);
        }
    }

    // ---- applying state (main thread) ----

    private void OnWelcome(uint id, int halfMapSize)
    {
        myEntityId = id;
        welcomed = true;

        lifeStartTime = Time.time;
        sessionPeakMass = PlayerBlob.MASS_MIN;
        lastKnownMass = PlayerBlob.MASS_MIN;

        if (Map.instance != null)
        {
            Map.instance.ApplyMapSize(halfMapSize);
        }
    }

    private void OnSnapshot(List<EntityState> entities, List<(uint id, Vector2 pos)> foodUpdates, List<(string Name, float Mass)> leaderboard)
    {
        var seenPlayers = new HashSet<uint>();
        var seenBots = new HashSet<uint>();
        var seenViruses = new HashSet<uint>();
        var seenSaws = new HashSet<uint>();

        foreach (var e in entities)
        {
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
                blob.ApplyState(e.Position, e.Scale, e.Color, e.Mass);

                if (e.Id == myEntityId)
                {
                    HandleLocalPlayerTick(e, leaderboard, blob);
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
                blob.ApplyState(e.Position, e.Scale, e.Color);
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
                blob.ApplyState(e.Position, e.Scale, e.Color);
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
                blob.ApplyState(e.Position, e.Scale, e.Color, e.Mass);
            }
        }

        RemoveMissing(players, seenPlayers);
        RemoveMissing(bots, seenBots);
        RemoveMissing(viruses, seenViruses);
        RemoveMissing(saws, seenSaws);

        ApplyFoodUpdates(foodUpdates);

        if (players.TryGetValue(myEntityId, out var mine) && mine.playerHud != null)
        {
            mine.playerHud.SetLeaderboard(leaderboard);
        }
    }

    private void HandleLocalPlayerTick(EntityState e, List<(string Name, float Mass)> leaderboard, PlayerBlob blob)
    {
        bool died = lastKnownMass > PlayerBlob.MASS_MIN * 2f && e.Mass <= PlayerBlob.MASS_MIN * 1.05f;
        if (died)
        {
            float survivedSeconds = Time.time - lifeStartTime;
            HandleDeath(sessionPeakMass, survivedSeconds, blob);
            lifeStartTime = Time.time;
            sessionPeakMass = PlayerBlob.MASS_MIN;
        }

        sessionPeakMass = Mathf.Max(sessionPeakMass, e.Mass);
        lastKnownMass = e.Mass;

        UpdateQuests(e, leaderboard, blob);
    }

    private void HandleDeath(float peakMass, float survivedSeconds, PlayerBlob blob)
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

        var newlyUnlocked = Achievements.CheckNewlyUnlocked(playerData);
        PlayerHandleData.Save(playerData);

        if (blob.playerHud != null)
        {
            blob.playerHud.ShowMatchSummary(peakMass, survivedSeconds, isNewRecord, newlyUnlocked);
        }
    }

    private void UpdateQuests(EntityState e, List<(string Name, float Mass)> leaderboard, PlayerBlob blob)
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
            playerData.questMassProgress = Mathf.Max(playerData.questMassProgress, e.Mass);
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
                if (leaderboard[i].Name == e.Name)
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

    private void ApplyFoodUpdates(List<(uint id, Vector2 pos)> updates)
    {
        foreach (var (id, pos) in updates)
        {
            if (!food.TryGetValue(id, out var obj))
            {
                obj = Instantiate(foodPrefab);
                food[id] = obj;
            }
            obj.transform.position = pos;
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
}
