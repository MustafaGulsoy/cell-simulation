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
    public float currentMass;

    // The server's scale changes in steps (eating a pellet, a merge handing mass over, a split
    // halving it). Snapping the transform to each step reads as a pop; this eases the DRAWN scale
    // toward the reported one every frame instead. currentScale stays the true network value that
    // the pop detection / sorting / camera logic below keys off.
    private const float ScaleSmoothingRate = 14f;
    private float displayScale = -1f;

    // Camera framing: the follow target is a runtime object placed at the centre of ALL this
    // player's pieces (not just the primary one), and the zoom widens enough to keep them all in
    // view. See UpdateCameraFocus.
    private const float CAMERA_GROUP_MARGIN = 1.4f;
    private Transform cameraFocus;
    private float groupExtent;
    private readonly System.Collections.Generic.List<PlayerBlob> ownPieces = new System.Collections.Generic.List<PlayerBlob>();

    // Position arrives from the server at the 30Hz tick rate, not every render frame - snapping
    // straight to it (as this used to) means the object sits still for ~33ms then teleports, which
    // Cinemachine's follow damping reads as a jolt rather than motion. Most visible right when a
    // split launches an otherwise-still blob, since there's no prior motion to mask the teleport.
    // Smoothing toward the latest reported position every frame (below) turns that into a
    // continuous slide instead, without adding perceptible input lag at this network tick rate.
    private const float PositionSmoothingRate = 20f;
    private Vector2 targetPosition;
    private bool hasTargetPosition;

    // Squash/pop feedback - separate from the network-driven scale so a snapshot arriving
    // mid-animation can't stomp it: ApplyCombinedScale() always multiplies the two together.
    private float punchScale = 1f;
    private Coroutine popCoroutine;

    private void Awake()
    {
        blobDetailCanvas.worldCamera = Camera.main;
        if (splitButton != null) splitButton.onClick.AddListener(() => GameClient.instance?.SendSplit());
        if (ejectButton != null) ejectButton.onClick.AddListener(() => GameClient.instance?.SendEject());
    }

    // The prefab's big number under the name ("Blob Level") is a leftover placeholder that nothing ever
    // updated, so every blob showed a constant 1000. It now shows the blob's real mass; the small
    // duplicate ("Blob Score") is hidden.
    private TextMeshProUGUI massLabel;

    private void BindLabels()
    {
        if (massLabel != null || blobDetailCanvas == null) return;

        foreach (var text in blobDetailCanvas.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text.gameObject.name == "Blob Level") massLabel = text;
        }
        if (playerHud != null && playerHud.blobScore != null) playerHud.blobScore.gameObject.SetActive(false);
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
            playerHud.ShowRemoteLabels(name);
        }
    }

    private EffectRing effectRing;
    public byte currentEffects;

    /// <summary>Shows/hides the glow for this blob's active power-up effects (bitmask from the server).</summary>
    public void SetEffects(byte mask)
    {
        currentEffects = mask;
        if (effectRing == null)
        {
            if (mask == 0) return;
            effectRing = EffectRing.Attach(transform);
        }
        effectRing.Set(mask);
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

        currentMass = mass;
        BindLabels();
        if (massLabel != null) massLabel.SetText(mass.ToString("0"));

        if (!Mathf.Approximately(currentScale, scale))
        {
            // A same-tick drop of >15% only happens from a split/virus/saw pop, never from normal
            // growth/shrink pacing - that's the cue for the squash feedback, not a separate flag
            // the server would have to send.
            bool poppedSmaller = currentScale > 0f && scale < currentScale * 0.85f;

            currentScale = scale;
            if (displayScale < 0f) displayScale = scale; // first sighting: nothing to ease from
            ApplyCombinedScale();
            UpdateOrderLayer((int)scale);
            UpdateOrthographicSize(scale);

            if (poppedSmaller)
            {
                PlayPopAnimation();
                if (isMine) GameAudio.Play("split", 0.9f);
            }
        }

        if (isMine)
        {
            playerHud.setBlobScoreText(mass);
            playerHud.setScoreCounterText(mass);
        }
        else
        {
            playerHud.setBlobScoreText(mass);
        }
    }

    private void Update()
    {
        if (displayScale >= 0f && !Mathf.Approximately(displayScale, currentScale))
        {
            displayScale = Mathf.Lerp(displayScale, currentScale, 1f - Mathf.Exp(-ScaleSmoothingRate * Time.deltaTime));
            ApplyCombinedScale();
        }

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

        UpdateCameraFocus();

        // Zoom for the primary piece's size, but never tighter than the whole group needs.
        float targetSize = Mathf.Max(nextOrthographicSize, Mathf.Min(groupExtent * CAMERA_GROUP_MARGIN, Map.orthographicSpectatingSize));
        if (virtualCamera.m_Lens.OrthographicSize != targetSize)
        {
            virtualCamera.m_Lens.OrthographicSize = Mathf.Lerp(virtualCamera.m_Lens.OrthographicSize, targetSize, 3f * Time.deltaTime);
        }
    }

    /// <summary>GameClient hands the primary blob the list of every PlayerBlob this player owns
    /// (itself included) each snapshot.</summary>
    public void SetOwnPieces(System.Collections.Generic.List<PlayerBlob> pieces)
    {
        ownPieces.Clear();
        ownPieces.AddRange(pieces);
    }

    // Follow the size-weighted centre of all own pieces, and remember how far the outermost one
    // reaches from it so LateUpdate can zoom out to fit. With a single piece this is exactly the
    // old behaviour (focus == this transform, extent 0).
    private void UpdateCameraFocus()
    {
        if (cameraFocus == null)
        {
            cameraFocus = new GameObject("CameraFocus").transform;
        }

        Vector3 own = transform.position;
        Vector2 centre = own;
        float extent = 0f;

        if (ownPieces.Count > 1)
        {
            Vector2 weighted = Vector2.zero;
            float totalWeight = 0f;
            foreach (var piece in ownPieces)
            {
                if (piece == null) continue;
                float weight = Mathf.Max(1f, piece.currentScale * piece.currentScale);
                weighted += (Vector2)piece.transform.position * weight;
                totalWeight += weight;
            }

            if (totalWeight > 0f)
            {
                centre = weighted / totalWeight;
                foreach (var piece in ownPieces)
                {
                    if (piece == null) continue;
                    extent = Mathf.Max(extent, Vector2.Distance(centre, piece.transform.position) + piece.currentScale * 0.5f);
                }
            }
        }

        groupExtent = extent;
        cameraFocus.position = new Vector3(centre.x, centre.y, own.z);

        if (virtualCamera.m_Follow != cameraFocus)
        {
            virtualCamera.m_Follow = cameraFocus;
        }
    }

    private void OnDestroy()
    {
        if (cameraFocus != null)
        {
            Destroy(cameraFocus.gameObject);
        }
    }

    private void ApplyCombinedScale()
    {
        if (displayScale < 0f) return; // Init's pop plays before the first ApplyState sets a real scale
        float s = displayScale * punchScale;
        transform.localScale = new Vector3(s, s, 1f);
    }

    /// <summary>A quick squash-then-settle "pop" - plays when this blob first appears (covers a
    /// split clone, which is a brand new entity Id to the client) and again whenever its own scale
    /// suddenly drops (the piece that stayed behind after a split/pop). Drives a multiplier on top
    /// of the network-true scale (see ApplyCombinedScale) so a Snapshot landing mid-tween can't cut
    /// it short.</summary>
    private void PlayPopAnimation()
    {
        if (popCoroutine != null) StopCoroutine(popCoroutine);
        popCoroutine = StartCoroutine(PopAnimation());
    }

    private IEnumerator PopAnimation()
    {
        const float duration = 0.3f;
        const float startScale = 0.55f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            punchScale = Mathf.LerpUnclamped(startScale, 1f, Utils.EaseOutBack(Mathf.Clamp01(elapsed / duration)));
            ApplyCombinedScale();
            yield return null;
        }

        punchScale = 1f;
        ApplyCombinedScale();
        popCoroutine = null;
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
