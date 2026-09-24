using System.Collections.Generic;
using UnityEngine;

// Small sprites drawn in code (no image files): power-up badges and the glow ring around a blob that
// has an active effect. Every texture is generated once and cached. All are 64 px at 64 pixels-per-unit,
// i.e. exactly ONE world unit across, so a transform's localScale is directly the diameter in world units
// (the same convention BlobCircle uses for blobs).
public static class ProceduralSprites
{
    private const int Size = 64;

    // Effect bit flags, matching Server Entity.EffectMask.
    public const byte EffectSpeed = 1;
    public const byte EffectShield = 2;
    public const byte EffectMagnet = 4;

    public static readonly Color SpeedColor = new Color(1f, 0.82f, 0.24f, 1f);
    public static readonly Color ShieldColor = new Color(0.35f, 0.67f, 1f, 1f);
    public static readonly Color MagnetColor = new Color(0.9f, 0.35f, 0.78f, 1f);

    private static Sprite ring;
    private static Sprite disc;
    private static readonly Dictionary<string, Sprite> badges = new Dictionary<string, Sprite>();

    public static Color KindColor(string kind)
    {
        return kind == "speed" ? SpeedColor : kind == "shield" ? ShieldColor : MagnetColor;
    }

    public static Color ColorOfEffect(byte mask)
    {
        // Shield is the most important thing to notice (it decides fights), then speed, then magnet.
        if ((mask & EffectShield) != 0) return ShieldColor;
        if ((mask & EffectSpeed) != 0) return SpeedColor;
        return MagnetColor;
    }

    /// <summary>Soft ring (transparent centre) used as the glow around a blob.</summary>
    public static Sprite Ring()
    {
        if (ring == null)
        {
            ring = Build(delegate (float x, float y)
            {
                float r = Mathf.Sqrt(x * x + y * y);
                // 0.5 is the rim of the unit circle; a feathered band just inside it.
                float a = Mathf.Clamp01((r - 0.40f) / 0.05f) * Mathf.Clamp01((0.5f - r) / 0.03f);
                return new Color(1f, 1f, 1f, a);
            });
        }
        return ring;
    }

    /// <summary>Filled white disc (tint it with SpriteRenderer.color).</summary>
    public static Sprite Disc()
    {
        if (disc == null)
        {
            disc = Build(delegate (float x, float y)
            {
                float r = Mathf.Sqrt(x * x + y * y);
                return new Color(1f, 1f, 1f, Mathf.Clamp01((0.5f - r) / 0.02f));
            });
        }
        return disc;
    }

    /// <summary>A coloured disc with a white glyph for the given power-up kind ("speed", "shield", "magnet").</summary>
    public static Sprite Badge(string kind)
    {
        Sprite s;
        if (badges.TryGetValue(kind, out s))
        {
            return s;
        }

        Color body = kind == "speed" ? SpeedColor : kind == "shield" ? ShieldColor : MagnetColor;
        s = Build(delegate (float x, float y)
        {
            float r = Mathf.Sqrt(x * x + y * y);
            float disc01 = Mathf.Clamp01((0.5f - r) / 0.02f);
            if (disc01 <= 0f)
            {
                return new Color(0, 0, 0, 0);
            }

            // Darker rim so the badge reads against light and dark backgrounds.
            Color c = Color.Lerp(body * 0.7f, body, Mathf.Clamp01((0.46f - r) / 0.04f));
            c.a = 1f;

            if (Glyph(kind, x, y))
            {
                c = Color.white;
            }
            c.a = disc01;
            return c;
        });
        badges[kind] = s;
        return s;
    }

    // x,y in [-0.5, 0.5], y up.
    private static bool Glyph(string kind, float x, float y)
    {
        switch (kind)
        {
            case "speed":
                // Two chevrons ">>".
                for (int k = 0; k < 2; k++)
                {
                    float cx = -0.16f + k * 0.2f;
                    float d = Mathf.Abs(x - cx) - 0.0f;
                    float edge = Mathf.Abs(y) * 0.55f;   // slope of the chevron arms
                    if (y > -0.24f && y < 0.24f && Mathf.Abs((x - cx) + edge - 0.13f) < 0.055f) return true;
                }
                return false;
            case "shield":
                // Heater shield: flat top, sides curving to a point at the bottom.
                if (y > 0.22f || y < -0.3f) return false;
                float halfWidth = y > 0f ? 0.2f : 0.2f * Mathf.Clamp01(1f - (-y) / 0.3f * 0.95f);
                bool inside = Mathf.Abs(x) < halfWidth;
                bool inner = Mathf.Abs(x) < halfWidth - 0.06f && y < 0.16f && y > -0.2f + Mathf.Abs(x) * 0.6f;
                return inside && !inner;
            default:
                // Horseshoe magnet: an arch on top, two legs going down.
                float r = Mathf.Sqrt(x * x + (y - 0.02f) * (y - 0.02f));
                bool arch = y > 0.02f && r > 0.14f && r < 0.26f;
                bool legs = y <= 0.02f && y > -0.26f && ((x > 0.14f && x < 0.26f) || (x < -0.14f && x > -0.26f));
                return arch || legs;
        }
    }

    // Outline drawn around the local player's cells: a black band hugging the cell and a white band
    // outside it, so the cell stands out on both the light and the dark map. Meant to sit on a child
    // scaled OutlineScale times the blob (see PlayerBlob.SetOutline); the blob's own rim is then at
    // radius BodyRadius / OutlineScale in sprite space.
    public const float OutlineScale = 1.32f;
    // The blob art reaches ~0.55 of the transform scale from its centre (measured in screenshots), a bit past the nominal 0.5.
    private const float BodyRadius = 0.55f;
    private static Sprite outline;

    public static Sprite Outline()
    {
        if (outline == null)
        {
            const float edge = BodyRadius / OutlineScale;
            const float black = 0.034f;
            outline = Build(delegate (float x, float y)
            {
                float r = Mathf.Sqrt(x * x + y * y);
                float inner = Mathf.Clamp01((r - edge) / 0.006f);
                float outer = Mathf.Clamp01((0.4995f - r) / 0.006f);
                float a = inner * outer;
                return r < edge + black ? new Color(0f, 0f, 0f, a) : new Color(1f, 1f, 1f, a);
            }, 128);
        }
        return outline;
    }

    // ---- emotes: round faces drawn from a few shapes ----

    private static readonly Dictionary<byte, Sprite> emojis = new Dictionary<byte, Sprite>();

    /// <summary>Emote ids used by the Emoji message: 1 = smile, 2 = laughing, 3 = angry.</summary>
    public static Sprite Emoji(byte id)
    {
        if (id < 1 || id > 3) id = 1;
        Sprite s;
        if (emojis.TryGetValue(id, out s))
        {
            return s;
        }

        var ink = new Color(0.2f, 0.11f, 0.05f, 1f);
        var yellow = new Color(1f, 0.82f, 0.18f, 1f);
        var red = new Color(0.95f, 0.32f, 0.2f, 1f);
        s = Build(delegate (float x, float y)
        {
            float r = Mathf.Sqrt(x * x + y * y);
            float alpha = Mathf.Clamp01((0.49f - r) / 0.012f);
            if (alpha <= 0f)
            {
                return new Color(0, 0, 0, 0);
            }

            // Face: lighter towards the top-left, dark rim.
            Color face = id == 3 ? red : yellow;
            Color c = Color.Lerp(face * 0.82f, Color.Lerp(face, Color.white, 0.25f), Mathf.Clamp01(0.5f + (-x + y) * 1.1f));
            c = Color.Lerp(c, new Color(0.45f, 0.25f, 0.05f), Mathf.Clamp01((r - 0.44f) / 0.04f));
            c.a = 1f;

            float mark = 0f;
            Color markColor = ink;
            if (id == 1)
            {
                mark = Mathf.Max(Blob(x, y, -0.16f, 0.1f, 0.055f, 0.075f), Blob(x, y, 0.16f, 0.1f, 0.055f, 0.075f), y < -0.08f ? Arc(x, y, 0f, 0.02f, 0.27f, 0.025f) : 0f);
            }
            else if (id == 2)
            {
                float eyes = Mathf.Max(y > 0.06f ? Arc(x, y, -0.17f, 0.06f, 0.085f, 0.022f) : 0f, y > 0.06f ? Arc(x, y, 0.17f, 0.06f, 0.085f, 0.022f) : 0f);
                float mouth = y < -0.04f ? Cover(Mathf.Sqrt(x * x + (y + 0.04f) * (y + 0.04f)) - 0.27f) : 0f;
                mark = Mathf.Max(eyes, mouth);
                if (mouth > 0.5f)
                {
                    // Open mouth: dark red with a pink tongue and a white row of teeth.
                    markColor = new Color(0.5f, 0.08f, 0.1f, 1f);
                    float mr = Mathf.Sqrt(x * x + (y + 0.04f) * (y + 0.04f));
                    if (y > -0.1f) markColor = Color.white;
                    else if (y < -0.2f && mr < 0.22f && Mathf.Abs(x) < 0.15f) markColor = new Color(1f, 0.5f, 0.55f, 1f);
                }
            }
            else
            {
                float brow = Mathf.Max(Segment(x, y, -0.27f, 0.23f, -0.06f, 0.13f, 0.028f), Segment(x, y, 0.27f, 0.23f, 0.06f, 0.13f, 0.028f));
                float eyes = Mathf.Max(Blob(x, y, -0.15f, 0.04f, 0.05f, 0.05f), Blob(x, y, 0.15f, 0.04f, 0.05f, 0.05f));
                float frown = y > -0.31f ? Arc(x, y, 0f, -0.36f, 0.2f, 0.024f) : 0f;
                mark = Mathf.Max(brow, eyes, frown);
            }

            c = Color.Lerp(c, markColor, mark);
            c.a = alpha;
            return c;
        }, 96);
        emojis[id] = s;
        return s;
    }

    private static float Cover(float signedDistance)
    {
        return Mathf.Clamp01(0.5f - signedDistance / 0.012f);
    }

    private static float Blob(float x, float y, float cx, float cy, float rx, float ry)
    {
        float d = Mathf.Sqrt((x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry)) - 1f;
        return Cover(d * Mathf.Min(rx, ry));
    }

    private static float Arc(float x, float y, float cx, float cy, float radius, float halfWidth)
    {
        float d = Mathf.Abs(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius) - halfWidth;
        return Cover(d);
    }

    private static float Segment(float x, float y, float ax, float ay, float bx, float by, float halfWidth)
    {
        float dx = bx - ax, dy = by - ay;
        float t = Mathf.Clamp01(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy));
        float px = ax + t * dx - x, py = ay + t * dy - y;
        return Cover(Mathf.Sqrt(px * px + py * py) - halfWidth);
    }

    private static Sprite Build(System.Func<float, float, Color> pixel, int size = Size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var colors = new Color[size * size];
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                float x = (px + 0.5f) / size - 0.5f;
                float y = (py + 0.5f) / size - 0.5f;
                colors[py * size + px] = pixel(x, y);
            }
        }
        tex.SetPixels(colors);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
