using UnityEngine;
using UnityEngine.Rendering;

// Thin visual proxy for a virus/explosion hazard - GameWorld.ResolveVirusCollisions on the .NET
// server decides when a cell touching one gets forced-split; this only mirrors position/scale.
// Uses the project's existing Virus sprite/prefab art (SpriteRenderer), not BlobCircle.
public class VirusBlob : MonoBehaviour
{
    // The authored sprite is 5.12 units across at localScale=1 - dividing by this makes the
    // rendered diameter equal to `scale`, matching the convention every other blob prefab uses
    // (BlobCircle's unit circle at localScale=1 is already diameter 1, so localScale==scale there).
    private const float SpriteNativeSize = 5.12f;

    public uint entityId;

    [SerializeField] public SpriteRenderer spriteRenderer;
    [SerializeField] public SortingGroup sortingGroup;

    private bool colorInitialized;
    private float currentScale = -1f;

    // Same network-smoothing + spawn-pop feedback as PlayerBlob/AIBlob/SawBlob - viruses never
    // split/shrink, so this only ever plays once, when a new virus spawns (including its
    // respawn-elsewhere after popping a cell).
    private const float PositionSmoothingRate = 20f;
    private Vector2 targetPosition;
    private bool hasTargetPosition;
    private float punchScale = 1f;

    public void Init(uint id)
    {
        entityId = id;
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
            spriteRenderer.color = new Color(color.r, color.g, color.b, spriteRenderer.color.a);
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
        float localScale = (currentScale / SpriteNativeSize) * punchScale;
        transform.localScale = new Vector3(localScale, localScale, 1f);
    }

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
}
