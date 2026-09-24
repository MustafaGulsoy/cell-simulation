using UnityEngine;

// The pulsing glow around a blob that has an active power-up effect (shield = blue, speed = yellow,
// magnet = magenta). Created lazily as a child of the blob, so a blob that never gets an effect pays nothing.
public class EffectRing : MonoBehaviour
{
    private SpriteRenderer sr;
    private byte mask;
    private float phase;

    public static EffectRing Attach(Transform blob)
    {
        var go = new GameObject("EffectRing");
        go.transform.SetParent(blob, false);
        go.transform.localScale = Vector3.one * 1.28f;
        var ring = go.AddComponent<EffectRing>();
        ring.sr = go.AddComponent<SpriteRenderer>();
        ring.sr.sprite = ProceduralSprites.Ring();
        ring.sr.sortingOrder = 20;   // above the blob's own body within its sorting group
        ring.sr.enabled = false;
        ring.phase = Random.value * 6.28f;
        return ring;
    }

    public void Set(byte effectMask)
    {
        mask = effectMask;
        sr.enabled = effectMask != 0;
        if (effectMask != 0)
        {
            sr.color = ProceduralSprites.ColorOfEffect(effectMask);
        }
    }

    private void Update()
    {
        if (mask == 0) return;

        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 5f + phase);
        transform.localScale = Vector3.one * (1.24f + 0.08f * pulse);

        var c = sr.color;
        c.a = 0.55f + 0.4f * pulse;
        sr.color = c;
    }
}
