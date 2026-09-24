using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

// V1: no idle/respawn state machine - GameClient shows the playing HUD as soon as the
// server Welcomes us. Level/experience/recombine/split UI are V2 and removed for now.
public class PlayerHUD : MonoBehaviour
{
    [SerializeField] private Button mainMenuButton;

    [SerializeField] private GameObject matchSummaryPanel;
    [SerializeField] private TextMeshProUGUI matchSummaryMassText;
    [SerializeField] private TextMeshProUGUI matchSummaryTimeText;
    [SerializeField] private TextMeshProUGUI matchSummaryRecordText;
    [SerializeField] private TextMeshProUGUI matchSummaryAchievementText;
    [SerializeField] private Button matchSummaryDismissButton;
    private const float MATCH_SUMMARY_DURATION = 4f;
    private Coroutine matchSummaryHideCoroutine;

    [SerializeField] private TextMeshProUGUI dailyPanelText;

    [SerializeField] private Button emojiButton1;
    [SerializeField] private Button emojiButton2;
    [SerializeField] private Button emojiButton3;

    [SerializeField] public TextMeshProUGUI leaderboardFirst;
    [SerializeField] public TextMeshProUGUI leaderboardSecond;
    [SerializeField] public TextMeshProUGUI leaderboardThird;
    [SerializeField] public TextMeshProUGUI leaderboardFourth;
    [SerializeField] public TextMeshProUGUI leaderboardFifth;
    [SerializeField] private TextMeshProUGUI scoreCounter;

    [SerializeField] private TextMeshProUGUI blobName;
    [SerializeField] public TextMeshProUGUI blobScore;
    [SerializeField] public Transform blobPointer;

    [SerializeField] private Canvas playingCanvas;
    [SerializeField] private Canvas idleCanvas;

    [SerializeField] public TMP_InputField usernameInput;

    [SerializeField] public Image joystickBackground;
    [SerializeField] public Image joystickHandle;

    private TextMeshProUGUI[] leaderboardSlots;

    private void Awake()
    {
        // The top-left button used to drop straight to the main menu; it now opens the pause menu
        // (Resume / Main menu, see HudExtras).
        mainMenuButton.onClick.AddListener(() => {
            if (GameClient.instance != null) GameClient.instance.TogglePause();
            else SceneManager.LoadScene("Main Menu", LoadSceneMode.Single);
        });

        PlaceQuests();
        UseEmojiFaces();

        leaderboardSlots = new[] { leaderboardFirst, leaderboardSecond, leaderboardThird, leaderboardFourth, leaderboardFifth };

        setIdleCanvasActivity(true);
        setPlayingCanvasActivity(false);

        if (matchSummaryDismissButton != null) matchSummaryDismissButton.onClick.AddListener(HideMatchSummary);
        if (matchSummaryPanel != null) matchSummaryPanel.SetActive(false);

        if (emojiButton1 != null) emojiButton1.onClick.AddListener(() => GameClient.instance?.SendEmoji(1));
        if (emojiButton2 != null) emojiButton2.onClick.AddListener(() => GameClient.instance?.SendEmoji(2));
        if (emojiButton3 != null) emojiButton3.onClick.AddListener(() => GameClient.instance?.SendEmoji(3));
    }

    // Daily quests: left edge, vertically centred (they sat under the score and got in the way of the
    // top-left buttons).
    private void PlaceQuests()
    {
        var panel = dailyPanelText != null ? dailyPanelText.transform.parent as RectTransform : null;
        if (panel == null) return;

        panel.anchorMin = panel.anchorMax = new Vector2(0f, 0.5f);
        panel.pivot = new Vector2(0f, 0.5f);
        panel.anchoredPosition = new Vector2(12f, 0f);
        panel.sizeDelta = new Vector2(440f, 130f);
    }

    // The three emote buttons showed ":)" / "haha" / ">:(" as text and, worse, the labels didn't match
    // what was sent. Each button now shows the drawn face of the emote id it actually sends.
    private void UseEmojiFaces()
    {
        var buttons = new[] { emojiButton1, emojiButton2, emojiButton3 };
        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            if (button == null) continue;

            foreach (var text in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                text.gameObject.SetActive(false);
            }

            var face = new GameObject("Face", typeof(RectTransform)).AddComponent<Image>();
            face.sprite = ProceduralSprites.Emoji((byte)(i + 1));
            face.preserveAspect = true;
            face.raycastTarget = false;
            var rt = face.rectTransform;
            rt.SetParent(button.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 6f);
            rt.offsetMax = new Vector2(-6f, -6f);
        }
    }

    public void setIdleCanvasActivity(bool active)
    {
        if(idleCanvas.gameObject.activeSelf != active)
        {
            idleCanvas.gameObject.SetActive(active);
        }
    }

    public void setPlayingCanvasActivity(bool active)
    {
        if(playingCanvas.gameObject.activeSelf != active)
        {
            playingCanvas.gameObject.SetActive(active);
        }
    }

    public void ShowPlaying(string username)
    {
        setPlayingCanvasActivity(true);
        setIdleCanvasActivity(false);

        bool hasUsername = !string.IsNullOrEmpty(username);
        blobScore.rectTransform.localPosition = hasUsername ? Utils.scoreWithNamePosition : Utils.scoreNoNamePosition;

        blobName.text = username;
        blobName.gameObject.SetActive(true);

        blobScore.gameObject.SetActive(true);
        blobPointer.gameObject.SetActive(true);
        scoreCounter.gameObject.SetActive(true);
    }

    /// <summary>Name and score label under a blob that ISN'T the local player's (other players, and the
    /// extra pieces of a split player). Without this they showed the prefab's placeholder text.</summary>
    public void ShowRemoteLabels(string username)
    {
        bool hasUsername = !string.IsNullOrEmpty(username);
        blobScore.rectTransform.localPosition = hasUsername ? Utils.scoreWithNamePosition : Utils.scoreNoNamePosition;

        blobName.text = username;
        blobName.gameObject.SetActive(hasUsername);
        blobScore.gameObject.SetActive(true);
    }

    public void setScoreCounterText(float score)
    {
        scoreCounter.SetText(string.Format("Score: {0}", score.ToString("0")));
    }

    public void setBlobScoreText(float score)
    {
        blobScore.SetText(score.ToString("0"));
    }

    public void SetLeaderboard(List<(string Name, float Mass)> entries)
    {
        for(int i = 0; i < leaderboardSlots.Length; i++)
        {
            leaderboardSlots[i].SetText(i < entries.Count
                ? string.Format("{0}. {1}: {2}", i + 1, entries[i].Name, entries[i].Mass.ToString("0"))
                : "");
        }
    }

    // The score / recombine texts are authored dark (readable on white) and vanished on the dark map.
    // They are found once (everything under the playing canvas sharing the score counter's colour) and
    // swapped between their authored colour and the bright HUD colour whenever the theme flips.
    private readonly List<TextMeshProUGUI> themedTexts = new List<TextMeshProUGUI>();
    private readonly List<Color> themedLightColors = new List<Color>();
    private int themedFor = -1; // -1 unknown, 0 light, 1 dark

    private void TintHudText(bool dark)
    {
        int state = dark ? 1 : 0;
        if (state == themedFor || scoreCounter == null || playingCanvas == null) return;

        if (themedTexts.Count == 0)
        {
            Color authored = scoreCounter.color;
            foreach (var text in playingCanvas.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (Mathf.Abs(text.color.r - authored.r) + Mathf.Abs(text.color.g - authored.g) + Mathf.Abs(text.color.b - authored.b) < 0.05f)
                {
                    themedTexts.Add(text);
                    themedLightColors.Add(text.color);
                }
            }
        }

        for (int i = 0; i < themedTexts.Count; i++)
        {
            themedTexts[i].color = dark ? Utils.playingHudTextBrightColor : themedLightColors[i];
        }
        themedFor = state;
    }

    public void updateJoystickColor()
    {
        TintHudText(Utils.isColorAlmostBlack(Camera.main.backgroundColor));

        if(Utils.isColorAlmostBlack(Camera.main.backgroundColor) && joystickBackground.color != Utils.brightJoystickBackgroundColor)
        {
            joystickBackground.color = Utils.brightJoystickBackgroundColor;
        } else if(Utils.isColorAlmostWhite(Camera.main.backgroundColor) && joystickBackground.color != Utils.darkJoystickBackgroundColor)
        {
            joystickBackground.color = Utils.darkJoystickBackgroundColor;
        }

        if(Utils.isColorAlmostBlack(Camera.main.backgroundColor) && joystickHandle.color != Utils.brightJoystickHandleColor)
        {
            joystickHandle.color = Utils.brightJoystickHandleColor;
        } else if(Utils.isColorAlmostWhite(Camera.main.backgroundColor) && joystickHandle.color != Utils.darkJoystickHandleColor)
        {
            joystickHandle.color = Utils.darkJoystickHandleColor;
        }
    }

    // There's no real spectator mode (the server respawns the same entity instantly, see
    // GameClient's death heuristic) - this is just a ~4s non-blocking recap of the life that just
    // ended, dismissible early by tapping it. The game keeps running underneath the whole time.
    public void ShowMatchSummary(float massReached, float survivedSeconds, bool isNewRecord, List<string> newlyUnlockedAchievements, string extraLine = null)
    {
        if (matchSummaryPanel == null)
        {
            return;
        }

        matchSummaryMassText.SetText(string.Format("Mass reached: {0}", massReached.ToString("0")));
        matchSummaryTimeText.SetText(string.Format("Survived: {0}", FormatTime(survivedSeconds)));
        ShowExtraSummary(extraLine);
        matchSummaryRecordText.gameObject.SetActive(isNewRecord);

        bool hasNewAchievements = newlyUnlockedAchievements != null && newlyUnlockedAchievements.Count > 0;
        if (hasNewAchievements)
        {
            matchSummaryAchievementText.SetText("Achievement unlocked: " + string.Join(", ", newlyUnlockedAchievements));
        }
        matchSummaryAchievementText.gameObject.SetActive(hasNewAchievements);

        matchSummaryPanel.SetActive(true);

        if (matchSummaryHideCoroutine != null) StopCoroutine(matchSummaryHideCoroutine);
        matchSummaryHideCoroutine = StartCoroutine(HideMatchSummaryAfterDelay());
    }

    // Who ate you / XP earned: its own little card just under the summary panel, so the panel's
    // authored layout isn't disturbed by extra lines.
    private GameObject extraSummaryCard;
    private TextMeshProUGUI extraSummaryText;

    private void ShowExtraSummary(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (extraSummaryCard != null) extraSummaryCard.SetActive(false);
            return;
        }

        if (extraSummaryCard == null)
        {
            var parent = (RectTransform)matchSummaryPanel.transform;
            // Above the panel: below it are the emoji buttons.
            var card = RuntimeUi.Panel("ExtraSummary", parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(parent.rect.width > 10f ? parent.rect.width : 780f, 100f), RuntimeUi.PanelColor);
            extraSummaryText = RuntimeUi.Label("Text", card.transform, "", 28f, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(720f, 96f), TextAlignmentOptions.Center);
            extraSummaryCard = card.gameObject;
        }

        extraSummaryText.text = text;
        extraSummaryCard.SetActive(true);
    }

    private IEnumerator HideMatchSummaryAfterDelay()
    {
        yield return new WaitForSeconds(MATCH_SUMMARY_DURATION);
        HideMatchSummary();
    }

    private void HideMatchSummary()
    {
        if (matchSummaryHideCoroutine != null)
        {
            StopCoroutine(matchSummaryHideCoroutine);
            matchSummaryHideCoroutine = null;
        }
        if (matchSummaryPanel != null) matchSummaryPanel.SetActive(false);
    }

    private static string FormatTime(float seconds)
    {
        int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
        return string.Format("{0:00}:{1:00}", total / 60, total % 60);
    }

    public void SetDailyPanelText(string text)
    {
        if (dailyPanelText != null) dailyPanelText.SetText(text);
    }
}
