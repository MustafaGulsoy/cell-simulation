using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Extra in-game HUD built in code (see RuntimeUi): connection quality, an unmistakable "connection lost"
// banner, a volume button, active power-up badges, a where-am-I map, and a one-time how-to-play hint.
// Positions are constants at the top so they are easy to nudge without hunting through the code.
public class HudExtras : MonoBehaviour
{
    // ---- layout (reference resolution 1280x720, landscape; anchors are screen fractions) ----
    private static readonly Vector2 PingAnchor = new Vector2(0.5f, 1f);
    private static readonly Vector2 PingOffset = new Vector2(0f, -14f);
    // Sound / theme buttons: top-right, in a column just left of the (scene's) leaderboard.
    private static readonly Vector2 MuteAnchor = new Vector2(1f, 1f);
    private static readonly Vector2 MuteOffset = new Vector2(-236f, -12f);
    private static readonly Vector2 MapAnchor = new Vector2(0f, 0f);   // bottom-left: the right side has the joystick and buttons
    private static readonly Vector2 MapOffset = new Vector2(12f, 12f);
    private const float MapSize = 130f;
    private static readonly Vector2 EffectsAnchor = new Vector2(0.5f, 1f);
    private static readonly Vector2 EffectsOffset = new Vector2(0f, -60f);

    private const string TutorialSeenKey = "tutorialSeen";
    private const int MapTextureSize = 64;

    private Canvas canvas;
    private TextMeshProUGUI pingText;
    private GameObject banner;
    private TextMeshProUGUI muteLabel;
    private readonly Image[] effectBadges = new Image[3];
    private Texture2D mapTexture;
    private Color32[] mapPixels;
    private GameObject tutorial;
    private float nextMapDraw;
    private bool lostShown;

    public static HudExtras Create()
    {
        var go = new GameObject("HudExtras");
        return go.AddComponent<HudExtras>();
    }

    private void Awake()
    {
        canvas = RuntimeUi.CreateCanvas("HudExtrasCanvas", 40);
        canvas.transform.SetParent(transform, false);

        pingText = RuntimeUi.Label("Ping", canvas.transform, "", 22f, PingAnchor, new Vector2(0.5f, 1f), PingOffset, new Vector2(200f, 34f), TextAlignmentOptions.Center);

        var bannerImg = RuntimeUi.Panel("ConnectionLost", canvas.transform, new Vector2(0.5f, 0.78f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600f, 88f), new Color(0.75f, 0.1f, 0.1f, 0.88f));
        RuntimeUi.Label("Text", bannerImg.transform, "Connection lost - reconnecting...", 30f, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(580f, 80f), TextAlignmentOptions.Center);
        banner = bannerImg.gameObject;
        banner.SetActive(false);

        BuildMuteButton();
        BuildEffectBadges();
        BuildMap();
        BuildTutorial();
        BuildPauseMenu();
    }

    // ---- pause menu ----

    private GameObject pausePanel;

    /// <summary>True while the pause menu is open (GameClient then holds the cell still and drops Split/Eject/Emoji).</summary>
    public bool Paused { get { return pausePanel != null && pausePanel.activeSelf; } }

    private void BuildPauseMenu()
    {
        // Its own canvas, above the scene's HUD canvases, so the dim covers the whole HUD.
        var pauseCanvas = RuntimeUi.CreateCanvas("PauseCanvas", 500);
        pauseCanvas.transform.SetParent(transform, false);
        var dim = RuntimeUi.Panel("Pause", pauseCanvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000f, 4000f), new Color(0f, 0f, 0f, 0.6f));
        dim.raycastTarget = true; // swallows taps so nothing underneath (split, eject, joystick) is hit
        pausePanel = dim.gameObject;

        var card = RuntimeUi.Panel("Card", pausePanel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 330f), new Color(0.07f, 0.09f, 0.15f, 0.97f));
        RuntimeUi.Label("Title", card.transform, "Paused", 40f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(380f, 54f), TextAlignmentOptions.Center);

        var resume = RuntimeUi.ButtonWithLabel("Resume", card.transform, "Resume", 30f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -86f), new Vector2(280f, 62f), new Color(0.15f, 0.55f, 0.35f, 1f));
        resume.onClick.AddListener(delegate { SetPaused(false); });

        var menu = RuntimeUi.ButtonWithLabel("MainMenu", card.transform, "Main menu", 30f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -164f), new Vector2(280f, 62f), new Color(0.55f, 0.2f, 0.2f, 1f));
        menu.onClick.AddListener(delegate { UnityEngine.SceneManagement.SceneManager.LoadScene("Main Menu", UnityEngine.SceneManagement.LoadSceneMode.Single); });

        RuntimeUi.Label("Note", card.transform, "The online game keeps running - your cell stands still while paused.", 16f, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 12f), new Vector2(380f, 56f), TextAlignmentOptions.Center);

        pausePanel.SetActive(false);
    }

    public void SetPaused(bool paused)
    {
        if (pausePanel == null || pausePanel.activeSelf == paused) return;
        pausePanel.SetActive(paused);
        GameAudio.Play("click");
    }

    private void Update()
    {
        // Escape is the Android back button too.
        if (Input.GetKeyDown(KeyCode.Escape)) SetPaused(!Paused);
    }

    // Coming back from the phone's settings / another app lands on the pause menu, not mid-fight.
    private void OnApplicationPause(bool pause)
    {
        if (pause) SetPaused(true);
    }

    private void BuildMuteButton()
    {
        if (!RuntimeUi.HasEventSystem) return;

        var button = RuntimeUi.ButtonWithLabel("Mute", canvas.transform, "", 18f, MuteAnchor, new Vector2(1f, 1f), MuteOffset, new Vector2(150f, 42f), RuntimeUi.PanelColor);
        muteLabel = button.GetComponentInChildren<TextMeshProUGUI>();
        RefreshMuteLabel();
        button.onClick.AddListener(delegate
        {
            var data = PlayerHandleData.LoadOrDefault();
            data.masterVolume = GameAudio.NextVolume(data.masterVolume);
            PlayerHandleData.Save(data);
            AudioListener.volume = data.masterVolume;
            RefreshMuteLabel();
            GameAudio.Play("click");
        });

        var theme = RuntimeUi.ButtonWithLabel("Theme", canvas.transform, Theme.Label(Theme.Night), 18f, MuteAnchor, new Vector2(1f, 1f), MuteOffset + new Vector2(0f, -50f), new Vector2(150f, 42f), RuntimeUi.PanelColor);
        var themeLabel = theme.GetComponentInChildren<TextMeshProUGUI>();
        theme.onClick.AddListener(delegate
        {
            themeLabel.text = Theme.Label(Theme.Toggle());
            GameAudio.Play("click");
        });
    }

    private void RefreshMuteLabel()
    {
        if (muteLabel == null) return;
        float v = AudioListener.volume;
        muteLabel.text = v <= 0.01f ? "Sound off" : v < 0.75f ? "Sound 50%" : "Sound 100%";
    }

    private void BuildEffectBadges()
    {
        var row = RuntimeUi.Rect("Effects", canvas.transform, EffectsAnchor, new Vector2(0.5f, 1f), EffectsOffset, new Vector2(200f, 60f));
        string[] kinds = { "speed", "shield", "magnet" };
        for (int i = 0; i < 3; i++)
        {
            var img = RuntimeUi.Panel(kinds[i], row, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2((i - 1) * 66f, 0f), new Vector2(56f, 56f), Color.white);
            img.sprite = ProceduralSprites.Badge(kinds[i]);
            img.gameObject.SetActive(false);
            effectBadges[i] = img;
        }
    }

    private void BuildMap()
    {
        var frame = RuntimeUi.Panel("Map", canvas.transform, MapAnchor, new Vector2(0f, 0f), MapOffset, new Vector2(MapSize, MapSize), new Color(0.02f, 0.03f, 0.06f, 0.82f));
        var raw = new GameObject("Dots", typeof(RectTransform)).AddComponent<RawImage>();
        raw.raycastTarget = false;
        var rt = raw.rectTransform;
        rt.SetParent(frame.transform, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(4f, 4f);
        rt.offsetMax = new Vector2(-4f, -4f);

        mapTexture = new Texture2D(MapTextureSize, MapTextureSize, TextureFormat.RGBA32, false);
        mapTexture.filterMode = FilterMode.Point;
        mapPixels = new Color32[MapTextureSize * MapTextureSize];
        raw.texture = mapTexture;
    }

    private void BuildTutorial()
    {
        if (PlayerPrefs.GetInt(TutorialSeenKey, 0) != 0) return;

        // Sits between the player (screen centre) and the joysticks so it never hides either.
        var panel = RuntimeUi.Panel("Tutorial", canvas.transform, new Vector2(0.5f, 0.3f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 150f), new Color(0.05f, 0.07f, 0.12f, 0.8f));
        RuntimeUi.Label("Text", panel.transform, "<b>How to play</b>\nDrag the joystick to move and eat pellets.\nEat cells smaller than you, run from bigger ones.\nSplit to attack; avoid green spikes when big.\nGrab glowing badges: speed, shield, magnet.", 19f,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(620f, 140f), TextAlignmentOptions.Center);
        tutorial = panel.gameObject;
        Invoke("HideTutorial", 9f);
    }

    private void HideTutorial()
    {
        if (tutorial != null) tutorial.SetActive(false);
        PlayerPrefs.SetInt(TutorialSeenKey, 1);
        PlayerPrefs.Save();
    }

    public void SetPing(int milliseconds)
    {
        if (pingText == null) return;
        if (milliseconds < 0)
        {
            pingText.text = "";
            return;
        }
        pingText.text = milliseconds + " ms";
        pingText.color = milliseconds < 90 ? new Color(0.45f, 0.95f, 0.5f) : milliseconds < 180 ? new Color(1f, 0.88f, 0.35f) : new Color(1f, 0.45f, 0.4f);
    }

    public void SetConnectionLost(bool lost)
    {
        if (banner == null || lost == lostShown) return;
        lostShown = lost;
        banner.SetActive(lost);
        if (lost) GameAudio.Play("warning");
    }

    public void SetEffects(byte mask)
    {
        effectBadges[0].gameObject.SetActive((mask & ProceduralSprites.EffectSpeed) != 0);
        effectBadges[1].gameObject.SetActive((mask & ProceduralSprites.EffectShield) != 0);
        effectBadges[2].gameObject.SetActive((mask & ProceduralSprites.EffectMagnet) != 0);
    }

    /// <summary>Draws the whole map as a small square with one dot per own piece (the server only tells
    /// us about what's near, so this shows WHERE on the big map you are, not what's on it).</summary>
    public void DrawMap(List<Vector2> ownPositions, float halfWidth, float halfHeight)
    {
        if (Time.unscaledTime < nextMapDraw || mapTexture == null) return;
        nextMapDraw = Time.unscaledTime + 0.15f;

        var background = new Color32(20, 26, 38, 200);
        for (int i = 0; i < mapPixels.Length; i++) mapPixels[i] = background;

        // Border.
        var border = new Color32(90, 110, 150, 255);
        for (int i = 0; i < MapTextureSize; i++)
        {
            mapPixels[i] = border;
            mapPixels[(MapTextureSize - 1) * MapTextureSize + i] = border;
            mapPixels[i * MapTextureSize] = border;
            mapPixels[i * MapTextureSize + MapTextureSize - 1] = border;
        }

        foreach (var p in ownPositions)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt((p.x / Mathf.Max(1f, halfWidth) * 0.5f + 0.5f) * (MapTextureSize - 1)), 1, MapTextureSize - 2);
            int y = Mathf.Clamp(Mathf.RoundToInt((p.y / Mathf.Max(1f, halfHeight) * 0.5f + 0.5f) * (MapTextureSize - 1)), 1, MapTextureSize - 2);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    mapPixels[(y + dy) * MapTextureSize + (x + dx)] = new Color32(255, 255, 255, 255);
                }
            }
        }

        mapTexture.SetPixels32(mapPixels);
        mapTexture.Apply(false);
    }
}
