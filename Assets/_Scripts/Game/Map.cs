using UnityEngine;
using UnityEngine.UI;

// Map bounds are now dictated by the .NET server (Server/CellSimulator.Server/Game/Rules.cs
// MapSizes). GameClient.cs calls ApplyMapSize() once the server reports halfMapSize in Welcome.
public class Map : MonoBehaviour
{
    public static Map instance;

    [SerializeField] private LineRenderer lineRenderer;
    [SerializeField] private Canvas mapCanvas;
    [SerializeField] public Image map;

    public static int halfMapSize = 100;
    public static float orthographicSpectatingSize = 120f;

    private void Awake()
    {
        instance = this;
        ApplyMapSize(halfMapSize);
    }

    public void ApplyMapSize(int half)
    {
        halfMapSize = half;
        orthographicSpectatingSize = half * 1.2f;

        int size = half * 2;
        mapCanvas.GetComponent<RectTransform>().sizeDelta = new Vector2(size * 10, size * 10);
        map.rectTransform.sizeDelta = new Vector2(size * 10, size * 10);

        lineRenderer.positionCount = 4;
        lineRenderer.loop = true;
        lineRenderer.startWidth = 0.5f;
        lineRenderer.SetPosition(0, new Vector2(-half, -half));
        lineRenderer.SetPosition(1, new Vector2(-half, half));
        lineRenderer.SetPosition(2, new Vector2(half, half));
        lineRenderer.SetPosition(3, new Vector2(half, -half));
    }

    public static Vector2 GenerateRandomPosition()
    {
        return new Vector2(Random.Range(-halfMapSize, halfMapSize), Random.Range(-halfMapSize, halfMapSize));
    }
}
