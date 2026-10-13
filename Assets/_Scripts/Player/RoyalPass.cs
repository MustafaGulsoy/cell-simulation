using System.Collections.Generic;
using UnityEngine;

// Free, cosmetic-only progression track. No new currency, no purchases, no server changes - it
// rides entirely on the level/XP system that already exists (Progression.cs). Reaching a tier's
// level unlocks one exclusive skin colour, which shows up appended to SkinPicker's row (see
// SkinPicker.Build) once unlocked - there's nothing to show for a locked tier besides the level
// needed, so unlike Achievements there's no separate "locked" rendering to build.
public static class RoyalPass
{
    private struct Tier
    {
        public int Level;
        public Color32 Reward;
    }

    private static readonly Tier[] Tiers =
    {
        new Tier { Level = 2,  Reward = new Color32(255, 215, 60, 255) },  // gold
        new Tier { Level = 4,  Reward = new Color32(147, 60, 230, 255) },  // royal purple
        new Tier { Level = 6,  Reward = new Color32(220, 40, 70, 255) },   // crimson
        new Tier { Level = 8,  Reward = new Color32(235, 170, 150, 255) }, // rose gold
        new Tier { Level = 10, Reward = new Color32(40, 90, 220, 255) },   // sapphire
        new Tier { Level = 12, Reward = new Color32(190, 120, 60, 255) },  // bronze
        new Tier { Level = 14, Reward = new Color32(225, 225, 215, 255) }, // platinum
        new Tier { Level = 16, Reward = new Color32(200, 20, 60, 255) },   // ruby
        new Tier { Level = 18, Reward = new Color32(255, 180, 40, 255) },  // amber
        new Tier { Level = 20, Reward = new Color32(70, 180, 140, 255) },  // jade
    };

    public static int TierCount => Tiers.Length;

    public static int UnlockedCount(PlayerData data)
    {
        int n = 0;
        foreach (var t in Tiers)
        {
            if (data.level >= t.Level) n++;
        }
        return n;
    }

    /// <summary>The level the next locked tier needs, or -1 once every tier is unlocked.</summary>
    public static int NextUnlockLevel(PlayerData data)
    {
        foreach (var t in Tiers)
        {
            if (data.level < t.Level) return t.Level;
        }
        return -1;
    }

    /// <summary>Reward colours for every tier reached so far, in unlock order.</summary>
    public static List<Color32> UnlockedColors(PlayerData data)
    {
        var colours = new List<Color32>();
        foreach (var t in Tiers)
        {
            if (data.level >= t.Level) colours.Add(t.Reward);
        }
        return colours;
    }
}
