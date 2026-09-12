using UnityEngine;
using TMPro;

public class AIHud : MonoBehaviour
{
    [SerializeField] public Canvas blobDetailCanvas;

    [SerializeField] private TextMeshProUGUI blobName;
    [SerializeField] public TextMeshProUGUI blobScore;

    private void Awake()
    {
        blobDetailCanvas.worldCamera = Camera.main;
    }

    private void LateUpdate()
    {
        if(blobDetailCanvas.gameObject.activeSelf)
        {
            blobDetailCanvas.gameObject.SetActive(false);
        }
    }

    public void Spawn(string username)
    {
        blobName.text = username;
        blobName.gameObject.SetActive(true);

        blobScore.text = "";
        blobScore.gameObject.SetActive(true);
    }

    public void setBlobScoreText(float score)
    {
        blobScore.SetText(score.ToString("0"));
    }
}
