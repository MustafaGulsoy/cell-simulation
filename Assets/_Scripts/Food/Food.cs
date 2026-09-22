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

    private void PlayPopAnimation()
    {
        LeanTween.cancel(gameObject, false);
        punchScale = 0.4f;
        ApplyPunchScale();
        LeanTween.value(gameObject, punchScale, 1f, 0.25f)
            .setEase(LeanTweenType.easeOutBack)
            .setOnUpdate((float v) =>
            {
                punchScale = v;
                ApplyPunchScale();
            });
    }

    private void ApplyPunchScale()
    {
        transform.localScale = Utils.foodScale * punchScale;
    }
}
