using UnityEngine;

// Light/dark map background. The choice lives in PlayerData.nightMode; the game scene applies it at start
// and the in-game button flips it live. The joystick tint already follows the camera colour (PlayerHUD).
public static class Theme
{
    private static readonly Color LightGrid = new Color(0f, 0f, 0f, 0.13f);
    private static readonly Color DarkGrid = new Color(1f, 1f, 1f, 0.09f);

    public static bool Night
    {
        get { return PlayerHandleData.LoadOrDefault().nightMode; }
    }

    public static void Apply(bool night)
    {
        if (Camera.main != null)
        {
            Camera.main.backgroundColor = night ? new Color(0.05f, 0.06f, 0.09f) : Color.white;
        }
        if (Map.instance != null && Map.instance.map != null)
        {
            Map.instance.map.color = night ? DarkGrid : LightGrid;
        }
        // The joystick and score text follow the background colour but were only tinted when the blob
        // spawned, so a live switch left them in the old theme.
        if (PlayerBlob.instance != null && PlayerBlob.instance.playerHud != null)
        {
            PlayerBlob.instance.playerHud.updateJoystickColor();
        }
    }

    /// <summary>Flips and saves the choice, applies it, and returns the new state.</summary>
    public static bool Toggle()
    {
        var data = PlayerHandleData.LoadOrDefault();
        data.nightMode = !data.nightMode;
        PlayerHandleData.Save(data);
        Apply(data.nightMode);
        return data.nightMode;
    }

    public static string Label(bool night)
    {
        return night ? "Dark map: on" : "Dark map: off";
    }
}
