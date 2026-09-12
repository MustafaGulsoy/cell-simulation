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
        mainMenuButton.onClick.AddListener(() => {
            SceneManager.LoadScene("Main Menu", LoadSceneMode.Single);
        });

        leaderboardSlots = new[] { leaderboardFirst, leaderboardSecond, leaderboardThird, leaderboardFourth, leaderboardFifth };

        setIdleCanvasActivity(true);
        setPlayingCanvasActivity(false);
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

    public void updateJoystickColor()
    {
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
}
