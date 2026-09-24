using System;
using System.Collections.Generic;

// Local-only, fixed table - no server sync, no unlock editor tooling. Add a row here if a new
// achievement is needed; nothing else has to change.
public static class Achievements
{
    private struct Def
    {
        public string Id;
        public string Text;
        public Func<PlayerData, bool> Condition;
    }

    private static readonly Def[] All =
    {
        new Def { Id = "first_game", Text = "First Game", Condition = d => d.gamesPlayed >= 1 },
        new Def { Id = "mass_100", Text = "100 Mass", Condition = d => d.bestMass >= 100f },
        new Def { Id = "mass_500", Text = "500 Mass", Condition = d => d.bestMass >= 500f },
        new Def { Id = "mass_1000", Text = "1000 Mass", Condition = d => d.bestMass >= 1000f },
        new Def { Id = "streak_3", Text = "3-Day Streak", Condition = d => d.currentStreak >= 3 },
        new Def { Id = "streak_7", Text = "7-Day Streak", Condition = d => d.currentStreak >= 7 },
        new Def { Id = "survive_300", Text = "5-Minute Survival", Condition = d => d.bestSurvivalSeconds >= 300f },
        new Def { Id = "mass_5000", Text = "5000 Mass", Condition = d => d.bestMass >= 5000f },
        new Def { Id = "games_10", Text = "10 Games", Condition = d => d.gamesPlayed >= 10 },
        new Def { Id = "games_50", Text = "50 Games", Condition = d => d.gamesPlayed >= 50 },
        new Def { Id = "eater_10", Text = "Ate 10 Cells", Condition = d => d.playersEaten >= 10 },
        new Def { Id = "eater_100", Text = "Ate 100 Cells", Condition = d => d.playersEaten >= 100 },
        new Def { Id = "spiked_10", Text = "Popped 10 Times", Condition = d => d.virusesEaten >= 10 },
        new Def { Id = "level_5", Text = "Level 5", Condition = d => d.level >= 5 },
        new Def { Id = "level_10", Text = "Level 10", Condition = d => d.level >= 10 },
    };

    /// <summary>Call after mutating data (mass/streak/etc.) - appends any newly-met achievement's id
    /// into data.unlockedAchievements (caller still has to Save()) and returns its display text so
    /// the caller can show it (e.g. in the match summary panel).</summary>
    public static List<string> CheckNewlyUnlocked(PlayerData data)
    {
        List<string> newlyUnlocked = null;
        if (data.unlockedAchievements == null)
        {
            data.unlockedAchievements = new List<string>();
        }

        foreach (var def in All)
        {
            if (data.unlockedAchievements.Contains(def.Id) || !def.Condition(data))
            {
                continue;
            }

            data.unlockedAchievements.Add(def.Id);
            (newlyUnlocked ??= new List<string>()).Add(def.Text);
        }

        return newlyUnlocked ?? new List<string>();
    }

    public static string DisplayText(PlayerData data)
    {
        if (data.unlockedAchievements == null || data.unlockedAchievements.Count == 0)
        {
            return "";
        }

        var texts = new List<string>();
        foreach (var def in All)
        {
            if (data.unlockedAchievements.Contains(def.Id))
            {
                texts.Add(def.Text);
            }
        }
        return string.Join(", ", texts);
    }
}
