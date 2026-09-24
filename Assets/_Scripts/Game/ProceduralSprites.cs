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

    private static Sprite Build(System.Func<float, float, Color> pixel)
    {
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var colors = new Color[Size * Size];
        for (int py = 0; py < Size; py++)
        {
            for (int px = 0; px < Size; px++)
            {
                float x = (px + 0.5f) / Size - 0.5f;
                float y = (py + 0.5f) / Size - 0.5f;
                colors[py * Size + px] = pixel(x, y);
            }
        }
        tex.SetPixels(colors);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
    }
}
