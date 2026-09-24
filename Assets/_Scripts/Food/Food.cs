using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Food : MonoBehaviour
{
    public const float SCALE = 2.5f;

    [SerializeField] private SpriteRenderer spriteRenderer;

    // Ejected pellets get a position update almost every tick while they're flying (30Hz) - smooth
    // that instead of snapping, same as every other entity. A same-tick jump bigger than a pellet
    // could ever travel in one tick only means "this food got eaten and respawned elsewhere",
    // which is also the cue for the pop feedback below.
    private const float PositionSmoothingRate = 20f;
    private const float TeleportJumpThreshold = 5f;
    private Vector2 targetPosition;
    private bool hasTargetPosition;
    private float punchScale = 1f;
    private Coroutine popCoroutine;

    private void Awake()
    {
        ResetObject();
    }

    private void LateUpdate()
    {
        if(transform.localScale == Vector3.zero)
        {
            ResetObject();
        }
    }

    private void Update()
    {
        if (!hasTargetPosition) return;
        transform.position = Vector2.Lerp(transform.position, targetPosition, 1f - Mathf.Exp(-PositionSmoothingRate * Time.deltaTime));
    }

    public void ResetObject()
    {
        spriteRenderer.color = Utils.generateFoodColor();
        transform.localScale = Utils.foodScale;
    }

    /// <summary>Called by GameClient for every food position update in a Snapshot/FoodFull packet.</summary>
    public void SetPosition(Vector2 position)
    {
        bool isRespawn = hasTargetPosition && Vector2.Distance(targetPosition, position) > TeleportJumpThreshold;
        targetPosition = position;

        if (!hasTargetPosition)
        {
            hasTargetPosition = true;
            transform.position = position;
            return;
        }

        if (isRespawn)
        {
            ResetObject();
            PlayPopAnimation();
        }
    }

    /// <summary>Puts a recycled pellet straight at a position (no slide, no pop) - FoodField reuses a
    /// small pool of these for whichever pellets are currently near the camera.</summary>
    public void Snap(Vector2 position)
    {
        if (popCoroutine != null)
        {
            StopCoroutine(popCoroutine);
            popCoroutine = null;
        }
        punchScale = 1f;
        targetPosition = position;
        hasTargetPosition = true;
        transform.position = position;
        transform.localScale = Utils.foodScale;
    }

    private void PlayPopAnimation()
    {
        if (popCoroutine != null) StopCoroutine(popCoroutine);
        popCoroutine = StartCoroutine(PopAnimation());
    }

    private IEnumerator PopAnimation()
    {
        const float duration = 0.25f;
        const float startScale = 0.4f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            punchScale = Mathf.LerpUnclamped(startScale, 1f, Utils.EaseOutBack(Mathf.Clamp01(elapsed / duration)));
            ApplyPunchScale();
            yield return null;
        }

        punchScale = 1f;
        ApplyPunchScale();
        popCoroutine = null;
    }

    private void ApplyPunchScale()
    {
        transform.localScale = Utils.foodScale * punchScale;
    }
}
