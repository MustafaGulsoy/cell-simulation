using UnityEngine;

public class Utils : MonoBehaviour
{
    private static readonly System.DateTime epochStart = new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

    public static Color playingHudTextBrightColor = new Color(1, 1, 1, 0.85f);
    public static Color playingHudTextDarkColor = new Color(0, 0, 0, 0.45f);

    public static Color brightJoystickBackgroundColor = new Color(1, 1, 1, 0.12f);
    public static Color darkJoystickBackgroundColor = new Color(0, 0, 0, 0.12f);
    public static Color brightJoystickHandleColor = new Color(1, 1, 1, 0.14f);
    public static Color darkJoystickHandleColor = new Color(0, 0, 0, 0.14f);

    public static Vector3 scoreWithNamePosition = new Vector3(0, -1.6f, 0);
    public static Vector3 scoreNoNamePosition = new Vector3(0, -0f, 0);

    public static Vector2 foodScale = new Vector2(Food.SCALE, Food.SCALE);

    public static int secondsSinceEpoch()
    {
        return (int)(System.DateTime.UtcNow - epochStart).TotalSeconds;
    }

    public static long millisecondsSinceEpoch()
    {
        return (long)(System.DateTime.UtcNow - epochStart).TotalMilliseconds;
    }

    public static bool isColorAlmostBlack(Color color)
    {
        return (color.r < 0.2 && color.g < 0.2 && color.b < 0.2 && color.a == 1);
    }

    public static bool isColorAlmostWhite(Color color)
    {
        return (color.r > 0.8 && color.g > 0.8 && color.b > 0.8 && color.a == 1);
    }

    public static Color generateFoodColor()
    {
        return Random.ColorHSV(0f, 1f, 1f, 1f, 0.94f, 1f, 1f, 1f);
    }

    public static int GetNeededExperience(int level)
    {
        return (level * level * 700) + 1000;
    }
}
