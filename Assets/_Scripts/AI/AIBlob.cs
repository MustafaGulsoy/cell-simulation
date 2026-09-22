using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Thin visual proxy - AI roam/chase/flee now runs server-side in AiBrain.cs (Server/CellSimulator.Server).
// GameClient.cs drives Init()/ApplyState() from incoming Snapshot packets.
public class AIBlob : MonoBehaviour
{
    public uint entityId;
    public string username = "";

    [SerializeField] public BlobCircle blobCircle;
    [SerializeField] public AIHud aiHud;

    [SerializeField] public SortingGroup sortingGroup;

    public Color currentColor;
    private bool colorInitialized;
    private float currentScale = -1f;

    // Same network-smoothing + squash-pop feedback as PlayerBlob - see its comments for why. Bots
    // get exactly the same "just been split/popped" treatment since virus/saw hazards affect them
    // just as much as players.
    private const float PositionSmoothingRate = 20f;
    private Vector2 targetPosition;
    private bool hasTargetPosition;
    private float punchScale = 1f;
    private Coroutine popCoroutine;

    public void Init(uint id, string name)
    {
        entityId = id;
        username = name;

        blobCircle.Spawn();
        aiHud.Spawn(name);
        PlayPopAnimation();
    }

    public void ApplyState(Vector2 position, float scale, Color color, float mass)
    {
        targetPosition = position;
        if (!hasTargetPosition)
        {
            hasTargetPosition = true;
            transform.position = position;
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
            bool poppedSmaller = currentScale > 0f && scale < currentScale * 0.85f;

            currentScale = scale;
            ApplyCombinedScale();
            UpdateOrderLayer((int)scale);

            if (poppedSmaller) PlayPopAnimation();
        }

        aiHud.setBlobScoreText(mass);
    }

    private void Update()
    {
        if (!hasTargetPosition) return;
        transform.position = Vector2.Lerp(transform.position, targetPosition, 1f - Mathf.Exp(-PositionSmoothingRate * Time.deltaTime));
    }

    private void ApplyCombinedScale()
    {
        if (currentScale < 0f) return;
        float s = currentScale * punchScale;
        transform.localScale = new Vector3(s, s, 1f);
    }

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
        aiHud.blobDetailCanvas.sortingOrder = order;
    }
}
