using UnityEngine;

// A power-up lying on the map (speed / shield / magnet). Built entirely in code from the procedural
// badge sprite, so it needs no prefab. Mirrors the server entity's position/size; pulses gently so it
// stands out from the food.
public class PowerupBlob : MonoBehaviour
{
    public uint entityId;
    public string kind;

    private SpriteRenderer badge;
    private SpriteRenderer halo;
    private Vector2 targetPosition;
    private float size = 5f;
    private float pulseOffset;

    public static PowerupBlob Create(uint id, string kind)
    {
        var go = new GameObject("Powerup " + kind);
        var blob = go.AddComponent<PowerupBlob>();
        blob.entityId = id;
        blob.kind = kind;
        blob.pulseOffset = (id % 7) * 0.9f;

        // Above food (which sits at the default order) but below cells, so blobs pass over pickups.
        blob.halo = NewSprite(go.transform, "Halo", ProceduralSprites.Ring(), 4);
        blob.halo.color = ProceduralSprites.KindColor(kind);
        blob.badge = NewSprite(go.transform, "Badge", ProceduralSprites.Badge(kind), 5);
        return blob;
    }

    private static SpriteRenderer NewSprite(Transform parent, string name, Sprite sprite, int order)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        var sr = child.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        return sr;
    }

    public void ApplyState(Vector2 position, float scale)
    {
        if (transform.position == Vector3.zero && targetPosition == Vector2.zero)
        {
            transform.position = position; // first sighting: appear in place
        }
        targetPosition = position;
        size = scale;
    }

    private void Update()
    {
        transform.position = Vector2.Lerp(transform.position, targetPosition, 1f - Mathf.Exp(-20f * Time.deltaTime));

        float pulse = 1f + 0.08f * Mathf.Sin(Time.time * 3.2f + pulseOffset);
        badge.transform.localScale = Vector3.one * size * pulse;
        halo.transform.localScale = Vector3.one * size * (1.35f + 0.1f * Mathf.Sin(Time.time * 3.2f + pulseOffset + 1.5f));
    }
}
