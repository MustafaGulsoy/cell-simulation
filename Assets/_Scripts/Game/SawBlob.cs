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

    public void Init(uint id)
    {
        entityId = id;
        blobCircle.Spawn();
    }

    public void ApplyState(Vector2 position, float scale, Color color)
    {
        transform.position = position;

        if (!colorInitialized)
        {
            colorInitialized = true;
            blobCircle.DrawLine(100, 0.5f, color);
            blobCircle.DrawFilledMesh(100, 0.5f, color);
        }

        if (!Mathf.Approximately(currentScale, scale))
        {
            currentScale = scale;
            transform.localScale = new Vector3(scale, scale, 1f);
            sortingGroup.sortingOrder = 100 + (int)scale;
        }
    }
}
