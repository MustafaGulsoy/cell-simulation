using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Thin visual proxy for the saw hazard - GameWorld.ResolveSawCollisions on the .NET server
// decides when a player or bot touching it takes damage; this only mirrors position/scale/color
// (server always sends green, but the color still comes over the wire like every other entity).
public class SawBlob : MonoBehaviour
{
    public uint entityId;

    [SerializeField] public BlobCircle blobCircle;
    [SerializeField] public SortingGroup sortingGroup;

    private bool colorInitialized;
    private float currentScale = -1f;

    // Same network-smoothing + spawn-pop feedback as PlayerBlob/AIBlob - saws never split/shrink,
    // so this only ever plays once, when a new saw spawns (including one launched by saw-feeding).
    private const float PositionSmoothingRate = 20f;
    private Vector2 targetPosition;
    private bool hasTargetPosition;
    private float punchScale = 1f;
    private Coroutine popCoroutine;

    public void Init(uint id)
    {
        entityId = id;
        blobCircle.Spawn();
        PlayPopAnimation();
    }

    public void ApplyState(Vector2 position, float scale, Color color)
    {
        targetPosition = position;
        if (!hasTargetPosition)
        {
            hasTargetPosition = true;
            transform.position = position;
        }

        if (!colorInitialized)
        {
            colorInitialized = true;
            blobCircle.DrawLine(100, 0.5f, color);
            blobCircle.DrawFilledMesh(100, 0.5f, color);
        }

        if (!Mathf.Approximately(currentScale, scale))
        {
            currentScale = scale;
            ApplyCombinedScale();
            sortingGroup.sortingOrder = 100 + (int)scale;
        }
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
}
