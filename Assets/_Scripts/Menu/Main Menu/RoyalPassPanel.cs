using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Dedicated "Royal Pass" overlay, same open/close-card pattern as LeaderboardPanel. Lists all free
// tiers (reused from RoyalPass.cs) side by side with a Premium column that has no content yet - it's
// built and laid out now so the real premium rewards can just slot in later, but for now it's dimmed
// and crossed with a diagonal "YAKINDA" (coming soon) ribbon so it doesn't read as a working feature.
public class RoyalPassPanel : MonoBehaviour
{
    public static RoyalPassPanel Create(Transform openButtonParent, Vector2 anchor, Vector2 pivot, Vector2 offset)
    {
        var go = new GameObject("RoyalPassPanel");
        var rp = go.AddComponent<RoyalPassPanel>();
        rp.Build(openButtonParent, anchor, pivot, offset);
        return rp;
    }

    private void Build(Transform buttonParent, Vector2 anchor, Vector2 pivot, Vector2 offset)
    {
        var data = PlayerHandleData.LoadOrDefault();
        int unlocked = RoyalPass.UnlockedCount(data);
        int nextLevel = RoyalPass.NextUnlockLevel(data);

        var canvas = RuntimeUi.CreateCanvas("RoyalPassCanvas", 45);
        canvas.transform.SetParent(transform, false);

        var dim = RuntimeUi.Panel("Dim", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000f, 4000f), new Color(0f, 0f, 0f, 0.6f));
        dim.raycastTarget = true;
        var panel = dim.gameObject;

        var card = RuntimeUi.Panel("Card", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 620f), new Color(0.07f, 0.09f, 0.15f, 0.97f));
        RuntimeUi.Label("Title", card.transform, "Royal Pass", 36f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(780f, 50f), TextAlignmentOptions.Center);

        string status = nextLevel < 0
            ? $"{unlocked}/{RoyalPass.TierCount} unlocked - all tiers reached!"
            : $"{unlocked}/{RoyalPass.TierCount} unlocked - next at Lv {nextLevel}";
        RuntimeUi.Label("Status", card.transform, status, 20f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -62f), new Vector2(780f, 28f), TextAlignmentOptions.Center);

        const float colFree = -200f;
        const float colPremium = 200f;
        RuntimeUi.Label("FreeHeader", card.transform, "Free", 22f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(colFree, -102f), new Vector2(180f, 30f), TextAlignmentOptions.Center);
        RuntimeUi.Label("PremiumHeader", card.transform, "Premium", 22f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(colPremium, -102f), new Vector2(180f, 30f), TextAlignmentOptions.Center);

        const float rowStartY = -136f;
        const float rowHeight = 34f;
        for (int i = 0; i < RoyalPass.TierCount; i++)
        {
            float y = rowStartY - i * rowHeight;
            int level = RoyalPass.LevelAt(i);
            bool reached = data.level >= level;
            Color32 swatchColor = reached ? RoyalPass.RewardAt(i) : new Color32(60, 64, 74, 255);
            string freeText = reached ? $"Lv {level}  ✓" : $"Lv {level}";

            RuntimeUi.Panel("FreeSwatch" + i, card.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(colFree - 80f, y - 4f), new Vector2(26f, 26f), swatchColor);
            var freeLabel = RuntimeUi.Label("FreeLabel" + i, card.transform, freeText, 18f, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(colFree - 55f, y), new Vector2(120f, 28f), TextAlignmentOptions.Left);
            if (!reached) freeLabel.color = new Color(1f, 1f, 1f, 0.55f);

            RuntimeUi.Panel("PremiumSwatch" + i, card.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(colPremium - 80f, y - 4f), new Vector2(26f, 26f), new Color32(50, 52, 58, 255));
            var premiumLabel = RuntimeUi.Label("PremiumLabel" + i, card.transform, $"Lv {level}", 18f, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(colPremium - 55f, y), new Vector2(120f, 28f), TextAlignmentOptions.Left);
            premiumLabel.color = new Color(1f, 1f, 1f, 0.35f);
        }

        float lastRowY = rowStartY - (RoyalPass.TierCount - 1) * rowHeight;
        float columnHeight = (rowStartY - lastRowY) + 60f;
        float columnCenterY = rowStartY - (columnHeight - 60f) / 2f;

        // Dim the whole Premium column and cross it with a rotated "coming soon" ribbon - there is no
        // premium track yet, this is just reserving the layout for when there is one.
        RuntimeUi.Panel("PremiumDim", card.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(colPremium, columnCenterY), new Vector2(260f, columnHeight), new Color(0f, 0f, 0f, 0.4f));

        var ribbon = RuntimeUi.Panel("Ribbon", card.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(colPremium, columnCenterY), new Vector2(360f, 50f), new Color(0.85f, 0.35f, 0.1f, 0.92f));
        ribbon.rectTransform.localEulerAngles = new Vector3(0f, 0f, -24f);
        var ribbonLabel = RuntimeUi.Label("RibbonLabel", ribbon.transform, "YAKINDA", 24f, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360f, 50f), TextAlignmentOptions.Center);
        ribbonLabel.fontStyle = FontStyles.Bold;

        var close = RuntimeUi.ButtonWithLabel("Close", card.transform, "Close", 26f, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(200f, 54f), new Color(0.5f, 0.2f, 0.2f, 1f));
        close.onClick.AddListener(delegate { panel.SetActive(false); GameAudio.Play("click"); });

        panel.SetActive(false);

        var open = RuntimeUi.ButtonWithLabel("OpenRoyalPass", buttonParent, "Royal Pass", 22f, anchor, pivot, offset, new Vector2(190f, 46f), new Color(0.15f, 0.2f, 0.34f, 0.92f));
        open.onClick.AddListener(delegate
        {
            panel.SetActive(true);
            GameAudio.Play("click");
        });
    }
}
