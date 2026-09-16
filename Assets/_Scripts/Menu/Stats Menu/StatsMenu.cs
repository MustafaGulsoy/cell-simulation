using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class StatsMenu : MonoBehaviour
{
    [SerializeField] TextMeshProUGUI _level;
    [SerializeField] TextMeshProUGUI _experience;

    [SerializeField] TextMeshProUGUI _foodEaten;
    [SerializeField] TextMeshProUGUI _virusesEaten;

    [SerializeField] TextMeshProUGUI _massGained;
    [SerializeField] TextMeshProUGUI _massLost;

    [SerializeField] TextMeshProUGUI _bestMass;
    [SerializeField] TextMeshProUGUI _bestSurvivalTime;
    [SerializeField] TextMeshProUGUI _gamesPlayed;
    [SerializeField] TextMeshProUGUI _currentStreak;
    [SerializeField] TextMeshProUGUI _achievements;

    private void Awake()
    {
        PlayerData data = PlayerHandleData.LoadOrDefault();
        int level = data.level;
        int experience = data.experience;
        int experienceNeeded = Utils.GetNeededExperience(level);

        _level.text = string.Format("Level: {0}", level.ToString());

        _experience.text = string.Format("{0}/{1} ({2}%)", experience, experienceNeeded, (((float)experience / (float)experienceNeeded) * 100).ToString("0.0"));

        _foodEaten.text = string.Format("Food Eaten: {0}", data.foodEaten.ToString());
        _virusesEaten.text = string.Format("Viruses Eaten: {0}", data.virusesEaten.ToString());

        _massGained.text = string.Format("Mass Gained: {0}", data.massGained.ToString());
        _massLost.text = string.Format("Mass Lost: {0}", data.massLost.ToString());

        _bestMass.text = string.Format("Best Mass: {0}", data.bestMass.ToString("0"));
        _bestSurvivalTime.text = string.Format("Best Survival: {0}", FormatSurvivalTime(data.bestSurvivalSeconds));
        _gamesPlayed.text = string.Format("Games Played: {0}", data.gamesPlayed.ToString());
        _currentStreak.text = string.Format("Streak: {0} day(s)", data.currentStreak.ToString());

        string achievementText = Achievements.DisplayText(data);
        _achievements.text = string.Format("Achievements: {0}", string.IsNullOrEmpty(achievementText) ? "none yet" : achievementText);
    }

    private static string FormatSurvivalTime(float seconds)
    {
        int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
        return string.Format("{0:00}:{1:00}", total / 60, total % 60);
    }
}
