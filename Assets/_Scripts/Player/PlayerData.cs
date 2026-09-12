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
    public int virusesEaten = 0;
    public int massGained = 0;
    public int massLost = 0;
    public int playtime = 0;

    public PlayerData()
    {
        localUsername = "";
        masterVolume = 1f;
        nightMode = false;
        level = 1;
    }
}
