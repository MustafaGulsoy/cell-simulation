using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class MainMenuHandler : MonoBehaviour
{
    // GameClient.cs connects here over UDP - the server is now a standalone .NET process
    // (Server/CellSimulator.Server), never a Unity instance, so there's no dedicated-server
    // build/menu-skip path anymore.
    public const string DEFAULT_SERVER_IP = "unseenbounds.easyway-sharing.com";

    // Must match Server/CellSimulator.Server/Game/Rules.cs' MapSize enum ordinal order exactly -
    // GameClient sends this dropdown's value as the raw MapSize byte in the Join packet.
    public const string MapSizePrefKey = "mapSize";
    private static readonly List<string> MapSizeOptions = new() { "Huge", "Large", "Medium", "Small" };
    private const int DefaultMapSizeIndex = 3; // Small

    [SerializeField] private Button playButton;
    [SerializeField] private Button infoButton;
    [SerializeField] private Button discordButton;

    [SerializeField] private TMP_Dropdown mapSizeDropdown;
    [SerializeField] private TMP_InputField serverIpInput;

    // Optional - wire a Toggle to this slot in the Inspector to expose the dark-theme background
    // (PlayerData.nightMode already existed; GameClient.Start()/PlayerHUD.updateJoystickColor now
    // actually act on it). Left unassigned, this is simply a no-op like serverIpInput's guard.
    [SerializeField] private Toggle nightModeToggle;

    // Extras built in code (no scene edits), laid out for the landscape 1280x720 reference: the colour
    // strip along the bottom (below the centre box), Top players and the theme button in the top-right.
    private static readonly Vector2 SkinAnchor = new Vector2(0.5f, 0f);
    private static readonly Vector2 SkinOffset = new Vector2(0f, 14f);
    private static readonly Vector2 TopAnchor = new Vector2(1f, 1f);
    private static readonly Vector2 TopOffset = new Vector2(-14f, -14f);

    private Canvas extras;
    private GameObject mainPage;

    // The extras belong to the main page: hidden while the Profile / Stats pages are open (they used to
    // float on top of those pages) and back when the main page returns.
    private void Update()
    {
        if (extras != null && mainPage != null && extras.gameObject.activeSelf != mainPage.activeInHierarchy)
        {
            extras.gameObject.SetActive(mainPage.activeInHierarchy);
        }
    }

    private void BuildThemeButton(Transform parent)
    {
        var button = RuntimeUi.ButtonWithLabel("Theme", parent, Theme.Label(Theme.Night), 22f, TopAnchor, new Vector2(1f, 1f), TopOffset + new Vector2(0f, -56f), new Vector2(190f, 46f), new Color(0.15f, 0.2f, 0.34f, 0.92f));
        var label = button.GetComponentInChildren<TextMeshProUGUI>();
        button.onClick.AddListener(delegate
        {
            label.text = Theme.Label(Theme.Toggle());
            GameAudio.Play("click");
        });
    }

    private void Awake() {
        GameAudio.ApplySavedVolume();

        // The Profile / Stats boxes are 650 tall on a 720 reference, so on wide phones they ran off the
        // top and bottom of the screen; shrink them a little (they are inactive, hence FindObjectsOfTypeAll).
        foreach (var rect in Resources.FindObjectsOfTypeAll<RectTransform>())
        {
            if (rect.name == "Box" && rect.parent != null && (rect.parent.name == "Profile Menu" || rect.parent.name == "Stats Menu") && rect.gameObject.scene.IsValid())
            {
                rect.localScale = new Vector3(0.8f, 0.8f, 1f);
            }
        }

        // The colour choice starts on "random" every time the menu opens.
        SkinPicker.ResetToRandom();
        mainPage = GameObject.Find("Main Menu");

        extras = RuntimeUi.CreateCanvas("MenuExtras", 30);
        SkinPicker.Build(extras.transform, SkinAnchor, new Vector2(0.5f, 0f), SkinOffset);
        LeaderboardPanel.Create(extras.transform, TopAnchor, new Vector2(1f, 1f), TopOffset);
        BuildThemeButton(extras.transform);

        // Server address is fixed - players never see or edit it.
        if(serverIpInput != null)
        {
            serverIpInput.gameObject.SetActive(false);
        }

        if (mapSizeDropdown != null)
        {
            mapSizeDropdown.ClearOptions();
            mapSizeDropdown.AddOptions(MapSizeOptions);
            mapSizeDropdown.value = PlayerPrefs.GetInt(MapSizePrefKey, DefaultMapSizeIndex);
            mapSizeDropdown.onValueChanged.AddListener(v => PlayerPrefs.SetInt(MapSizePrefKey, v));
        }

        if (nightModeToggle != null)
        {
            var data = PlayerHandleData.LoadOrDefault();
            nightModeToggle.isOn = data.nightMode;
            nightModeToggle.onValueChanged.AddListener(isOn =>
            {
                var current = PlayerHandleData.LoadOrDefault();
                current.nightMode = isOn;
                PlayerHandleData.Save(current);
            });
        }

        playButton.onClick.AddListener(() => {
            SceneManager.LoadScene("Game", LoadSceneMode.Single);
        });

        infoButton.onClick.AddListener(() => {
            Application.OpenURL("https://jonfinity.github.io");
        });

        discordButton.onClick.AddListener(() => {
            Application.OpenURL("https://discord.com/invite/A6hHVCAKcW");
        });
    }
}
