using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Automated test player for standalone builds. Does NOTHING unless the game is started with
//   -autopilot menu|game   [-shots <dir>] [-server host] [-port n]
// It then plays the real client against a real server - eats pellets, splits, throws mass, chases pickups -
// writing screenshots and a plain-text log to <dir>, and quits. That makes the client testable without a
// person or the Editor (run against a local server: dotnet run --project Server/CellSimulator.Server).
public class AutoPilot : MonoBehaviour
{
    private string mode;
    private string shotsDir;
    private StringBuilder log = new StringBuilder();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        string[] args = Environment.GetCommandLineArgs();
        string mode = null;
        string dir = "autopilot";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-autopilot") mode = args[i + 1];
            if (args[i] == "-shots") dir = args[i + 1];
        }
        if (mode == null || FindAnyObjectByType<AutoPilot>() != null)
        {
            return;
        }

        var go = new GameObject("AutoPilot");
        DontDestroyOnLoad(go);
        var pilot = go.AddComponent<AutoPilot>();
        pilot.mode = mode;
        pilot.shotsDir = dir;
    }

    private void Start()
    {
        Directory.CreateDirectory(shotsDir);
        Log("autopilot start mode=" + mode + " resolution=" + Screen.width + "x" + Screen.height);
        StartCoroutine(mode == "menu" ? RunMenu() : RunGame());
    }

    private void Log(string line)
    {
        string stamped = "[" + Time.realtimeSinceStartup.ToString("0.0") + "s] " + line;
        log.AppendLine(stamped);
        Debug.Log("AUTOPILOT " + stamped);
        File.WriteAllText(Path.Combine(shotsDir, "autopilot.log"), log.ToString());
    }

    private IEnumerator Shot(string name)
    {
        yield return new WaitForEndOfFrame();
        string path = Path.GetFullPath(Path.Combine(shotsDir, name + ".png"));
        ScreenCapture.CaptureScreenshot(path);
        yield return null;
        yield return null;
        Log("screenshot " + name);
    }

    private void Finish(int exitCode)
    {
        Log("done exit=" + exitCode);
        Application.Quit(exitCode);
    }

    // ---- main menu ----

    private IEnumerator RunMenu()
    {
        yield return new WaitForSeconds(2.5f);
        yield return Shot("menu");

        var profile = GameObject.Find("Profile Button");
        if (profile != null)
        {
            profile.GetComponent<Button>().onClick.Invoke();
            yield return new WaitForSeconds(1f);
            yield return Shot("menu-profile");
            Log("in profile: main menu active=" + IsActive("Main Menu") + " extras active=" + IsActive("MenuExtras"));

            var page = GameObject.Find("Profile Menu");
            foreach (var b in page.GetComponentsInChildren<Button>())
            {
                if (b.name != "Back Button") continue;
                b.onClick.Invoke();
                yield return new WaitForSeconds(1f);
                yield return Shot("menu-back");
                Log("back on main: main menu active=" + IsActive("Main Menu") + " extras active=" + IsActive("MenuExtras"));
                break;
            }
        }

        var open = GameObject.Find("OpenLeaderboard");
        if (open != null)
        {
            open.GetComponent<Button>().onClick.Invoke();
            yield return new WaitForSeconds(3.5f);
            yield return Shot("menu-top");
        }
        else
        {
            Log("WARNING: leaderboard button not found");
        }
        Finish(0);
    }

    private static bool IsActive(string name)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t.name == name && t.gameObject.scene.IsValid()) return t.gameObject.activeInHierarchy;
        }
        return false;
    }

    // ---- gameplay ----

    private IEnumerator RunGame()
    {
        yield return new WaitForSeconds(1.5f);
        SceneManager.LoadScene("Game");
        yield return null;

        GameClient client = null;
        float deadline = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < deadline)
        {
            client = GameClient.instance;
            if (client != null && client.Welcomed && client.SnapshotsReceived > 5) break;
            yield return new WaitForSeconds(0.25f);
        }
        if (client == null || !client.Welcomed)
        {
            Log("FAIL: never joined the server");
            Finish(2);
            yield break;
        }
        Log("joined as entity " + client.MyEntityId + ", snapshots=" + client.SnapshotsReceived + ", compact protocol=" + client.DebugUsesCompactProtocol);
        yield return new WaitForSeconds(1.0f);
        yield return Shot("game-start");

        // Eat pellets until big enough to split, steering at the nearest known one.
        float eatDeadline = Time.realtimeSinceStartup + 75f;
        float nextShot = Time.realtimeSinceStartup + 5f;
        while (client.DebugMyMass < 45f && Time.realtimeSinceStartup < eatDeadline)
        {
            Vector2 target;
            Vector2 me = client.DebugMyPosition;
            if (client.DebugNearestPowerup(me, out target) && (target - me).magnitude < 60f) { /* chase the pickup */ }
            else if (!client.DebugNearestFood(me, out target)) target = me + Vector2.right * 10f;

            Vector2 dir = target - me;
            client.DebugInput = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector2.zero;

            if (Time.realtimeSinceStartup > nextShot)
            {
                nextShot += 1000f;
                yield return Shot("game-eating");
            }
            yield return new WaitForSeconds(0.1f);
        }
        Log("mass reached " + client.DebugMyMass.ToString("0.0") + ", ping=" + client.PingMs + "ms, food known=" + client.KnownFoodCount + " active objects=" + client.ActiveFoodObjects);

        // Split, keep moving, watch the pieces and the camera.
        client.SendSplit();
        client.DebugInput = new Vector2(1f, 0.3f).normalized;
        yield return new WaitForSeconds(0.6f);
        yield return Shot("game-split-early");
        yield return new WaitForSeconds(2.0f);
        Log("after split: pieces=" + client.OwnPieceCount + " mass=" + client.DebugMyMass.ToString("0.0"));
        yield return Shot("game-split");

        // Dark theme through the same code path as the HUD button, and back.
        bool wasNight = Theme.Night;
        Theme.Toggle();
        yield return new WaitForSeconds(0.5f);
        yield return Shot(wasNight ? "game-light" : "game-dark");
        Theme.Toggle();

        // Emotes are drawn faces now; pause menu opens and the cell holds still.
        client.SendEmoji(2);
        yield return new WaitForSeconds(0.6f);
        yield return Shot("game-emoji");
        client.TogglePause();
        yield return new WaitForSeconds(0.5f);
        Vector2 pausedAt = client.DebugMyPosition;
        yield return new WaitForSeconds(1.0f);
        Log("paused=" + client.Paused + " drift while paused=" + (client.DebugMyPosition - pausedAt).magnitude.ToString("0.00"));
        yield return Shot("game-paused");
        client.TogglePause();
        Log("resumed, paused=" + client.Paused);

        // Eating while split: the pellet sound has to come from ANY piece, not just the primary.
        int eatBefore = GameAudio.CountOf("eat");
        int piecesBefore = client.OwnPieceCount;
        float eatUntil = Time.realtimeSinceStartup + 8f;
        while (Time.realtimeSinceStartup < eatUntil)
        {
            Vector2 foodTarget;
            Vector2 here = client.DebugMyPosition;
            client.DebugInput = client.DebugNearestFood(here, out foodTarget) ? (foodTarget - here).normalized : Vector2.zero;
            yield return new WaitForSeconds(0.1f);
        }
        Log("eat sounds while split: " + (GameAudio.CountOf("eat") - eatBefore) + " (pieces " + piecesBefore + " -> " + client.OwnPieceCount + ")");

        client.SendEject();
        client.DebugInput = new Vector2(-0.5f, 1f).normalized;
        yield return new WaitForSeconds(1.5f);
        yield return Shot("game-eject");

        // Look for a pickup: circle the map until one comes into view, then go get it and photograph the glow.
        float pickupDeadline = Time.realtimeSinceStartup + 60f;
        float angle = 0f;
        float nextReport = Time.realtimeSinceStartup + 10f;
        bool shotVisible = false;
        while (client.DebugMyEffects == 0 && Time.realtimeSinceStartup < pickupDeadline)
        {
            Vector2 me = client.DebugMyPosition;
            Vector2 pickup;
            Vector2 target;
            if (Time.realtimeSinceStartup > nextReport)
            {
                nextReport += 10f;
                Log("looking for pickups: visible=" + client.PowerupsVisible + " pos=" + me + " compact=" + client.DebugUsesCompactProtocol);
            }

            if (client.DebugNearestPowerup(me, out pickup))
            {
                target = pickup;
                if (!shotVisible)
                {
                    shotVisible = true;
                    Log("pickup in view at " + pickup);
                    yield return Shot("game-powerup-visible");
                }
            }
            else
            {
                angle += 0.05f;
                target = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 90f;   // wide circle around the centre
            }

            Vector2 dir = target - me;
            client.DebugInput = dir.sqrMagnitude > 0.01f ? dir.normalized : Vector2.zero;
            yield return new WaitForSeconds(0.1f);
        }
        if (client.DebugMyEffects != 0)
        {
            Log("collected a power-up, effects mask=" + client.DebugMyEffects);
            client.DebugInput = Vector2.zero;
            yield return new WaitForSeconds(0.8f);
            yield return Shot("game-powerup");
        }
        else
        {
            Log("no power-up collected (none in reach)");
        }

        client.DebugInput = Vector2.zero;
        client.DebugSimulateDeath();
        yield return new WaitForSeconds(0.8f);
        yield return Shot("game-death-panel");

        Log("final: snapshots=" + client.SnapshotsReceived + " ping=" + client.PingMs + "ms pieces=" + client.OwnPieceCount +
            " lost=" + client.ConnectionLost + " foodObjects=" + client.ActiveFoodObjects + "/" + client.KnownFoodCount + " audioPlays=" + GameAudio.PlayedCount);
        Finish(0);
    }
}
