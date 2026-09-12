using UnityEngine;
using UnityEngine.Rendering;

// Thin visual proxy - AI roam/chase/flee now runs server-side in AiBrain.cs (Server/CellSimulator.Server).
// GameClient.cs drives Init()/ApplyState() from incoming Snapshot packets.
public class AIBlob : MonoBehaviour
{
    public uint entityId;
    public string username = "";

    [SerializeField] public BlobCircle blobCircle;
    [SerializeField] public AIHud aiHud;

    [SerializeField] public SortingGroup sortingGroup;

    public Color currentColor;
    private bool colorInitialized;
    private float currentScale = -1f;

    public void Init(uint id, string name)
    {
        entityId = id;
        username = name;

        blobCircle.Spawn();
        aiHud.Spawn(name);
    }

    public void ApplyState(Vector2 position, float scale, Color color, float mass)
    {
        transform.position = position;

        if (!colorInitialized || color != currentColor)
        {
            currentColor = color;
            blobCircle.DrawLine(100, 0.5f, currentColor);
            blobCircle.DrawFilledMesh(100, 0.5f, currentColor);
            colorInitialized = true;
        }

        if (!Mathf.Approximately(currentScale, scale))
        {
            currentScale = scale;
            transform.localScale = new Vector3(scale, scale, 1f);
            UpdateOrderLayer((int)scale);
        }

        aiHud.setBlobScoreText(mass);
    }

    private void UpdateOrderLayer(int order)
    {
        order += 100;
        sortingGroup.sortingOrder = order;
        aiHud.blobDetailCanvas.sortingOrder = order;
    }
}
