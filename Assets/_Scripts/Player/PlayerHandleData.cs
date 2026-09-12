using System;
using System.IO;
using UnityEngine;

public static class PlayerHandleData
{
    private static string SavePath => Path.Combine(Application.persistentDataPath, "player.json");

    public static PlayerData LoadOrDefault()
    {
        try
        {
            if(!File.Exists(SavePath))
            {
                return new PlayerData();
            }

            return JsonUtility.FromJson<PlayerData>(File.ReadAllText(SavePath));
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load player data: {e}");
            return new PlayerData();
        }
    }
}
