using UnityEngine;
using UnityEngine.UI;

// Lets the player pick their blob colour in the main menu (built in code, see RuntimeUi). The choice is
// saved as a packed RGB int in PlayerPrefs and sent in the Join packet; -1 means "random colour like before".
// The server sanitises whatever it receives (see Rgba.Sanitize), so this is only a convenience, not trust.
public static class SkinPicker
{
    public const string SkinPrefKey = "skinColor";

    public static readonly Color32[] Palette =
    {
        new Color32(255, 107, 107, 255), // coral
        new Color32(255, 159, 67, 255),  // orange
        new Color32(254, 202, 87, 255),  // yellow
        new Color32(29, 209, 161, 255),  // teal
        new Color32(72, 219, 251, 255),  // sky
        new Color32(84, 160, 255, 255),  // blue
        new Color32(162, 155, 254, 255), // violet
        new Color32(255, 159, 243, 255), // pink
        new Color32(200, 214, 229, 255), // silver
    };

    public static int Pack(Color32 c)
    {
        return (c.r << 16) | (c.g << 8) | c.b;
    }

    public static Color32 Unpack(int packed)
    {
        return new Color32((byte)((packed >> 16) & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)(packed & 0xFF), 255);
    }

    /// <summary>Builds the picker under <paramref name="parent"/>. Returns a refresh action (highlights the chosen swatch).</summary>
    public static void Build(Transform parent, Vector2 anchor, Vector2 pivot, Vector2 offset)
    {
        const float swatch = 84f;
        const float gap = 14f;
        int count = Palette.Length + 1; // + "random"
        float width = count * swatch + (count - 1) * gap + 40f;

        var panel = RuntimeUi.Panel("SkinPicker", parent, anchor, pivot, offset, new Vector2(width, swatch + 96f), RuntimeUi.PanelColor);
        RuntimeUi.Label("Title", panel.transform, "Your colour", 34f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(width, 44f), TMPro.TextAlignmentOptions.Center);

        var outlines = new Image[count];
        for (int i = 0; i < count; i++)
        {
            bool isRandom = i == Palette.Length;
            Color32 colour = isRandom ? new Color32(90, 96, 110, 255) : Palette[i];
            int packed = isRandom ? -1 : Pack(colour);
            float x = 20f + i * (swatch + gap);

            var frame = RuntimeUi.Panel("Frame" + i, panel.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(x, 14f), new Vector2(swatch, swatch), Color.clear);
            outlines[i] = frame;

            var button = RuntimeUi.ButtonWithLabel("Swatch" + i, frame.transform, isRandom ? "?" : "", 44f, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(swatch - 14f, swatch - 14f), colour);
            int captured = packed;
            int index = i;
            button.onClick.AddListener(delegate
            {
                PlayerPrefs.SetInt(SkinPrefKey, captured);
                PlayerPrefs.Save();
                Highlight(outlines, index);
                GameAudio.Play("click");
            });
        }

        int saved = PlayerPrefs.GetInt(SkinPrefKey, -1);
        int selected = Palette.Length;
        for (int i = 0; i < Palette.Length; i++)
        {
            if (Pack(Palette[i]) == saved) selected = i;
        }
        Highlight(outlines, selected);
    }

    private static void Highlight(Image[] outlines, int selected)
    {
        for (int i = 0; i < outlines.Length; i++)
        {
            outlines[i].color = i == selected ? new Color(1f, 1f, 1f, 0.95f) : Color.clear;
        }
    }
}
