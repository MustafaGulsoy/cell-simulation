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

    // Extras built in code (no scene edits): colour picker and the Top players button. Anchors are
    // screen fractions; nudge these constants to fit the menu layout.
    private static readonly Vector2 SkinAnchor = new Vector2(0.5f, 0f);
    private static readonly Vector2 SkinOffset = new Vector2(0f, 60f);
    private static readonly Vector2 TopAnchor = new Vector2(0.5f, 0f);
    private static readonly Vector2 TopOffset = new Vector2(0f, 300f);

    private void Awake() {
        GameAudio.ApplySavedVolume();

        var extras = RuntimeUi.CreateCanvas("MenuExtras", 30);
        SkinPicker.Build(extras.transform, SkinAnchor, new Vector2(0.5f, 0f), SkinOffset);
        LeaderboardPanel.Create(extras.transform, TopAnchor, new Vector2(0.5f, 0f), TopOffset);

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
