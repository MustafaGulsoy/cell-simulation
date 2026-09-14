using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Thin visual proxy - all simulation (movement, growth, eating, AI) now lives on the .NET
// server (Server/CellSimulator.Server). GameClient.cs drives Init()/ApplyState() from
// incoming Snapshot packets. Split/clone/mass-shoot/recombine are V2 and not implemented here.
public class PlayerBlob : MonoBehaviour
{
    public const float MASS_MIN = 5f;

    public static PlayerBlob instance;

    public uint entityId;
    public bool isMine;
    public string username = "";

    [SerializeField] public BlobCircle blobCircle;
    [SerializeField] public PlayerHUD playerHud;
    [SerializeField] public PlayerMovement playerMovement;

    [SerializeField] private CinemachineVirtualCamera virtualCamera;
    private const float CAMERA_SIZE_MIN = 20f;
    private float nextOrthographicSize = CAMERA_SIZE_MIN;

    [SerializeField] private SortingGroup sortingGroup;
    [SerializeField] public Canvas blobDetailCanvas;

    // Split/eject only take effect for the local player - for a remote PlayerBlob, playerHud
    // (and these buttons under it) stays inactive entirely, so wiring them unconditionally here
    // is harmless.
    [SerializeField] private Button splitButton;
    [SerializeField] private Button ejectButton;

    public Color currentColor;
    private bool colorInitialized;
    private float currentScale = -1f;

    private void Awake()
    {
        blobDetailCanvas.worldCamera = Camera.main;
        if (splitButton != null) splitButton.onClick.AddListener(() => GameClient.instance?.SendSplit());
        if (ejectButton != null) ejectButton.onClick.AddListener(() => GameClient.instance?.SendEject());
    }

    public void Init(uint id, bool mine, string name)
    {
        entityId = id;
        isMine = mine;
        username = name;

        blobCircle.Spawn();

        if (mine)
        {
            instance = this;
            playerHud.ShowPlaying(name);
        }
        else
        {
            playerHud.gameObject.SetActive(false);
            virtualCamera.gameObject.SetActive(false);
        }
    }

    public void ApplyState(Vector2 position, float scale, Color color, float mass)
    {
        transform.position = position;

        if (!colorInitialized || color != currentColor)
        {
            currentColor = color;
            blobCircle.DrawLine(100, 0.5f, currentColor);
            blobCircle.DrawFilledMesh(100, 0.5f, currentColor);
            colorInitialized = true;
        }

        if (!Mathf.Approximately(currentScale, scale))
        {
            currentScale = scale;
            transform.localScale = new Vector3(scale, scale, 1f);
            UpdateOrderLayer((int)scale);
            UpdateOrthographicSize(scale);
        }

        if (isMine)
        {
            playerHud.setBlobScoreText(mass);
            playerHud.setScoreCounterText(mass);
        }
    }

    private void LateUpdate()
    {
        if (!isMine)
        {
            return;
        }

        if (virtualCamera.m_Follow != transform)
        {
            virtualCamera.m_Follow = transform;
        }

        if (virtualCamera.m_Lens.OrthographicSize != nextOrthographicSize)
        {
            virtualCamera.m_Lens.OrthographicSize = Mathf.Lerp(virtualCamera.m_Lens.OrthographicSize, nextOrthographicSize, 3f * Time.deltaTime);
        }
    }

    private void UpdateOrderLayer(int order)
    {
        order += 100;
        sortingGroup.sortingOrder = order;
        blobDetailCanvas.sortingOrder = order;
    }

    private void UpdateOrthographicSize(float scale)
    {
        float m = Mathf.Sqrt(scale + 26);
        float calc = (m * m) / 1.4f;
        nextOrthographicSize = Mathf.Clamp(calc, CAMERA_SIZE_MIN, Map.orthographicSpectatingSize);
    }
}
