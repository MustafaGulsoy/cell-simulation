using System.Collections;
using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using TMPro;

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

    [SerializeField] private TextMeshProUGUI emojiBubble;
    private const float EMOJI_BUBBLE_DURATION = 1.5f;
    private Coroutine emojiHideCoroutine;

    public Color currentColor;
    private bool colorInitialized;
    private float currentScale = -1f;

    // Position arrives from the server at the 30Hz tick rate, not every render frame - snapping
    // straight to it (as this used to) means the object sits still for ~33ms then teleports, which
    // Cinemachine's follow damping reads as a jolt rather than motion. Most visible right when a
    // split launches an otherwise-still blob, since there's no prior motion to mask the teleport.
    // Smoothing toward the latest reported position every frame (below) turns that into a
    // continuous slide instead, without adding perceptible input lag at this network tick rate.
    private const float PositionSmoothingRate = 20f;
    private Vector2 targetPosition;
    private bool hasTargetPosition;

    // Squash/pop feedback (LeanTween) - separate from the network-driven scale so a snapshot
    // arriving mid-tween can't stomp it: ApplyCombinedScale() always multiplies the two together.
    private float punchScale = 1f;

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
        PlayPopAnimation();

        if (mine)
        {
            instance = this;
            playerHud.ShowPlaying(name);
            playerHud.updateJoystickColor();
        }
        else
        {
            // playerHud is a component on THIS SAME root GameObject (not a separate child) -
            // playerHud.gameObject.SetActive(false) was deactivating the whole remote player,
            // not just its HUD, which is why other real players never rendered at all (bots don't
            // have this problem since AIHud's canvas lives on an actual child object). Hide the
            // HUD canvases specifically instead.
            playerHud.setIdleCanvasActivity(false);
            playerHud.setPlayingCanvasActivity(false);
            virtualCamera.gameObject.SetActive(false);
        }
    }

    public void ApplyState(Vector2 position, float scale, Color color, float mass)
    {
        targetPosition = position;
        if (!hasTargetPosition)
        {
            hasTargetPosition = true;
            transform.position = position; // first sighting of this blob - nothing to smooth from yet
        }

        if (!colorInitialized || color != currentColor)
        {
            currentColor = color;
            blobCircle.DrawLine(100, 0.5f, currentColor);
            blobCircle.DrawFilledMesh(100, 0.5f, currentColor);
            colorInitialized = true;
        }

        if (!Mathf.Approximately(currentScale, scale))
        {
            // A same-tick drop of >15% only happens from a split/virus/saw pop, never from normal
            // growth/shrink pacing - that's the cue for the squash feedback, not a separate flag
            // the server would have to send.
            bool poppedSmaller = currentScale > 0f && scale < currentScale * 0.85f;

            currentScale = scale;
            ApplyCombinedScale();
            UpdateOrderLayer((int)scale);
            UpdateOrthographicSize(scale);

            if (poppedSmaller) PlayPopAnimation();
        }

        if (isMine)
        {
            playerHud.setBlobScoreText(mass);
            playerHud.setScoreCounterText(mass);
        }
    }

    private void Update()
    {
        if (!hasTargetPosition)
        {
            return;
        }

        transform.position = Vector2.Lerp(transform.position, targetPosition, 1f - Mathf.Exp(-PositionSmoothingRate * Time.deltaTime));
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

    private void ApplyCombinedScale()
    {
        if (currentScale < 0f) return; // Init's pop plays before the first ApplyState sets a real scale
        float s = currentScale * punchScale;
        transform.localScale = new Vector3(s, s, 1f);
    }

    /// <summary>A quick squash-then-settle "pop" - plays when this blob first appears (covers a
    /// split clone, which is a brand new entity Id to the client) and again whenever its own scale
    /// suddenly drops (the piece that stayed behind after a split/pop). Drives a multiplier on top
    /// of the network-true scale (see ApplyCombinedScale) so a Snapshot landing mid-tween can't cut
    /// it short.</summary>
    private void PlayPopAnimation()
    {
        LeanTween.cancel(gameObject, false);
        punchScale = 0.55f;
        ApplyCombinedScale();
        LeanTween.value(gameObject, punchScale, 1f, 0.3f)
            .setEase(LeanTweenType.easeOutBack)
            .setOnUpdate((float v) =>
            {
                punchScale = v;
                ApplyCombinedScale();
            });
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

    /// <summary>Called by GameClient when it relays a ServerMsg.EmojiEvent for this entity - shows a
    /// small bubble above the blob for a couple seconds, then hides itself again.</summary>
    public void ShowEmoji(byte emojiId)
    {
        if (emojiBubble == null)
        {
            return;
        }

        emojiBubble.SetText(EmojiGlyph(emojiId));
        emojiBubble.gameObject.SetActive(true);

        if (emojiHideCoroutine != null) StopCoroutine(emojiHideCoroutine);
        emojiHideCoroutine = StartCoroutine(HideEmojiAfterDelay());
    }

    private IEnumerator HideEmojiAfterDelay()
    {
        yield return new WaitForSeconds(EMOJI_BUBBLE_DURATION);
        emojiBubble.gameObject.SetActive(false);
        emojiHideCoroutine = null;
    }

    private static string EmojiGlyph(byte emojiId)
    {
        switch (emojiId)
        {
            case 1: return ":)";
            case 2: return "haha";
            case 3: return ">:(";
            default: return "?";
        }
    }
}
