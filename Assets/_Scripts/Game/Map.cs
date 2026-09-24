using UnityEngine;
using UnityEngine.UI;

// Map bounds are dictated by the .NET server (Server/CellSimulator.Server/Game/GameConfig.cs
// MapWidth/MapHeight). GameClient.cs calls ApplyMapSize() once the server reports the half extents
// in Welcome. The map may be a rectangle; an older server only sends a square's half size.
public class Map : MonoBehaviour
{
    public static Map instance;

    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Canvas mapCanvas;
    [SerializeField] public Image map;

    public static int halfMapSize = 100;   // half width (kept under its old name)
    public static int halfMapHeight = 100;
    public static float orthographicSpectatingSize = 120f;

    private void Awake()
    {
        instance = this;
        ApplyMapSize(halfMapSize, halfMapHeight);
    }

    public void ApplyMapSize(int half)
    {
        ApplyMapSize(half, half);
    }

    public void ApplyMapSize(int halfWidth, int halfHeight)
    {
        halfMapSize = halfWidth;
        halfMapHeight = halfHeight;
        orthographicSpectatingSize = Mathf.Max(halfWidth, halfHeight) * 1.2f;

        Vector2 pixels = new Vector2(halfWidth * 2 * 10, halfHeight * 2 * 10);
        mapCanvas.GetComponent<RectTransform>().sizeDelta = pixels;
        map.rectTransform.sizeDelta = pixels;

        lineRenderer.positionCount = 4;
        lineRenderer.loop = true;
        lineRenderer.startWidth = 0.5f;
        lineRenderer.SetPosition(0, new Vector2(-halfWidth, -halfHeight));
        lineRenderer.SetPosition(1, new Vector2(-halfWidth, halfHeight));
        lineRenderer.SetPosition(2, new Vector2(halfWidth, halfHeight));
        lineRenderer.SetPosition(3, new Vector2(halfWidth, -halfHeight));
    }

    public static Vector2 GenerateRandomPosition()
    {
        return new Vector2(Random.Range(-halfMapSize, halfMapSize), Random.Range(-halfMapHeight, halfMapHeight));
    }
}
