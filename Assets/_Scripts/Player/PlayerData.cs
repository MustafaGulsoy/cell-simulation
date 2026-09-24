using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class PlayerData
{
    //local username
    public string localUsername;

    //settings
    public float masterVolume;
    public bool nightMode;

    //stats
    public int level;
    public int experience;
    public int foodEaten = 0;
    public int virusesEaten = 0;   // times a spike popped us (the server counts spike hits per life)
    public int playersEaten = 0;   // cells eaten (players and bots)
    public int massGained = 0;
    public int massLost = 0;
    public int playtime = 0;

    //personal records (GameClient tracks the running session values, see sessionPeakMass/lifeStartTime)
    public float bestMass = 0f;
    public float bestSurvivalSeconds = 0f;
    public int gamesPlayed = 0;

    //daily quests - 3 fixed quests, reset whenever questDay falls behind today's epoch-day
    public int questDay = 0;
    public float questMassProgress = 0f;
    public float questSurviveSeconds = 0f;
    public int questTopRankReached = 999; // lower is better, 999 = not ranked yet today
    public bool questMassDone = false;
    public bool questSurviveDone = false;
    public bool questRankDone = false;

    //streak
    public int currentStreak = 0;
    public int lastPlayedEpochDay = 0;

    //achievements - unlocked ids from Achievements.cs's fixed table
    public List<string> unlockedAchievements = new List<string>();

    public PlayerData()
    {
        localUsername = "";
        masterVolume = 1f;
        nightMode = false;
        level = 1;
    }
}
