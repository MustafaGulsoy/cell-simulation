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

    [SerializeField] private Button playButton;
    [SerializeField] private Button infoButton;
    [SerializeField] private Button discordButton;

    [SerializeField] private TMP_Dropdown mapSizeDropdown;
    [SerializeField] private TMP_InputField serverIpInput;

    private void Awake() {
        // Server address is fixed - players never see or edit it.
        if(serverIpInput != null)
        {
            serverIpInput.gameObject.SetActive(false);
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
