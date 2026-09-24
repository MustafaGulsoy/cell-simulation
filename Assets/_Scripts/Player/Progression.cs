using UnityEngine;

// Experience and levels. PlayerData.level/experience existed (and StatsMenu already displays them) but
// nothing ever advanced them; this is the one place that does. The curve for "XP needed for the next
// level" is Utils.GetNeededExperience, unchanged.
public static class Progression
{
    /// <summary>What a finished life is worth: mostly how big you got, plus time survived and cells eaten.</summary>
    public static int ExperienceForLife(float peakMass, float survivedSeconds, int cellsEaten)
    {
        float xp = peakMass * 1.5f + survivedSeconds * 0.5f + cellsEaten * 30f;
        return Mathf.Max(0, Mathf.RoundToInt(xp));
    }

    /// <summary>Adds XP, rolling over into as many levels as it covers. Returns how many levels were gained.
    /// The caller still has to save the data.</summary>
    public static int AddExperience(PlayerData data, int xp)
    {
        if (data.level < 1)
        {
            data.level = 1;
        }

        data.experience += Mathf.Max(0, xp);

        int gained = 0;
        int needed = Utils.GetNeededExperience(data.level);
        while (data.experience >= needed && gained < 100)
        {
            data.experience -= needed;
            data.level++;
            gained++;
            needed = Utils.GetNeededExperience(data.level);
        }
        return gained;
    }

    /// <summary>One line for the end-of-life panel: who ate you, XP earned, level-ups.</summary>
    public static string SummaryLine(string killerName, int gainedXp, int level, int levelsGained)
    {
        string line = string.IsNullOrEmpty(killerName) ? "" : "Eaten by " + killerName + "\n";
        line += "+" + gainedXp + " XP";
        if (levelsGained > 0)
        {
            line += "  -  Level " + level + "!";
        }
        return line;
    }
}
