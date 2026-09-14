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

    public void Init(uint id)
    {
        entityId = id;
    }

    public void ApplyState(Vector2 position, float scale, Color color)
    {
        transform.position = position;

        if (!colorInitialized)
        {
            colorInitialized = true;
            spriteRenderer.color = new Color(color.r, color.g, color.b, spriteRenderer.color.a);
        }

        if (!Mathf.Approximately(currentScale, scale))
        {
            currentScale = scale;
            float localScale = scale / SpriteNativeSize;
            transform.localScale = new Vector3(localScale, localScale, 1f);
            sortingGroup.sortingOrder = 100 + (int)scale;
        }
    }
}
