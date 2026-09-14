using System.Collections.Generic;
using UnityEngine;

// Draws the entity's body as a procedural circle mesh + outline, and owns a cheap elastic
// squash/stretch layer on top of it: PlayerBlob/AIBlob call ReportState() once per snapshot with
// their authoritative position/mass, and this redraws every frame as an ellipse whose stretch
// axis/amount springs toward a target derived from movement speed plus one-shot wall/eat
// impulses. No physics engine involved - purely a cosmetic deformation of the same circle mesh
// that was already being drawn, so it's naturally consistent across clients (each one computes it
// from the same replicated position/mass stream) without needing any extra network data.
public class BlobCircle : MonoBehaviour
{
    [SerializeField] public LineRenderer lineRenderer;
    [SerializeField] private MeshRenderer meshRenderer;
    [SerializeField] private MeshFilter filter;

    private Mesh mesh;
    private float baseRadius = 0.5f;
    private Color currentColor = Color.white;

    private Vector2 continuousTarget;
    private Vector2 impulse;
    private Vector2 drawnSquash;

    private Vector2 lastReportedPosition;
    private float lastReportedTime;
    private bool hasLastReported;
    private float lastReportedMass = -1f;

    private const int Steps = 100;
    private const float ReferenceSpeed = 20f;
    private const float MaxContinuousStretch = 0.12f;
    private const float MaxTotalStretch = 0.35f;
    private const float ImpulseDecayPerSecond = 4f;
    private const float SquashSpringSpeed = 10f;
    private const float WallImpulseStrength = 0.3f;
    private const float WallEdgeEpsilon = 1f;
    private const float EatImpulseStrength = 0.18f;
    private const float EatMassJumpThreshold = 0.5f;

    private void Awake()
    {
        mesh = new Mesh();
        filter.mesh = mesh;
    }

    private void Update()
    {
        impulse = Vector2.Lerp(impulse, Vector2.zero, Time.deltaTime * ImpulseDecayPerSecond);

        var target = continuousTarget + impulse;
        if (target.magnitude > MaxTotalStretch) target = target.normalized * MaxTotalStretch;

        drawnSquash = Vector2.Lerp(drawnSquash, target, Time.deltaTime * SquashSpringSpeed);

        if (meshRenderer.enabled) Redraw();
    }

    public void Spawn()
    {
        lineRenderer.enabled = true;
        meshRenderer.enabled = true;
    }

    public void Despawn(bool bypass = false)
    {
        lineRenderer.enabled = false;
        meshRenderer.enabled = false;
    }

    public void DrawLine(int steps, float radius, Color color)
    {
        baseRadius = radius;
        currentColor = color;
        Redraw();
    }

    public void DrawFilledMesh(int sides, float radius, Color color)
    {
        baseRadius = radius;
        currentColor = color;
        Redraw();
    }

    /// <summary>Feeds this snapshot's authoritative position/mass into the elastic system.
    /// halfMapSize &lt;= 0 skips wall-squash (used by entities that don't care, though callers
    /// simply always have a valid map size in practice).</summary>
    public void ReportState(Vector2 position, float mass, float halfMapSize)
    {
        float now = Time.time;
        if (hasLastReported)
        {
            float dt = Mathf.Max(0.001f, now - lastReportedTime);
            Vector2 vel = (position - lastReportedPosition) / dt;
            float speedNorm = Mathf.Clamp01(vel.magnitude / ReferenceSpeed);
            continuousTarget = vel.sqrMagnitude > 0.0001f
                ? vel.normalized * (speedNorm * MaxContinuousStretch)
                : Vector2.zero;

            if (halfMapSize > 0f)
            {
                if (Mathf.Abs(Mathf.Abs(position.x) - halfMapSize) < WallEdgeEpsilon)
                    impulse += new Vector2(-Mathf.Sign(position.x), 0f) * WallImpulseStrength;
                if (Mathf.Abs(Mathf.Abs(position.y) - halfMapSize) < WallEdgeEpsilon)
                    impulse += new Vector2(0f, -Mathf.Sign(position.y)) * WallImpulseStrength;
            }

            if (lastReportedMass >= 0f && mass > lastReportedMass + EatMassJumpThreshold)
            {
                Vector2 dir = vel.sqrMagnitude > 0.0001f ? vel.normalized : Vector2.right;
                impulse += dir * EatImpulseStrength;
            }
        }

        lastReportedPosition = position;
        lastReportedTime = now;
        lastReportedMass = mass;
        hasLastReported = true;
    }

    private void Redraw()
    {
        float amount = drawnSquash.magnitude;
        float angle = amount > 0.0001f ? Mathf.Atan2(drawnSquash.y, drawnSquash.x) : 0f;
        float stretchR = baseRadius * (1f + amount);
        float squeezeR = baseRadius / (1f + amount * 0.6f); // partial-area-conserving compression

        lineRenderer.positionCount = Steps;
        lineRenderer.loop = true;
        lineRenderer.startWidth = 0.6f;

        Color lineColor = currentColor / 1.15f;
        lineColor.a = 1;
        lineRenderer.startColor = lineColor;
        lineRenderer.endColor = lineColor;

        var points = new Vector3[Steps];
        float cosA = Mathf.Cos(angle), sinA = Mathf.Sin(angle);
        for (int i = 0; i < Steps; i++)
        {
            float t = (float)i / Steps * 2f * Mathf.PI;
            float lx = Mathf.Cos(t) * stretchR;
            float ly = Mathf.Sin(t) * squeezeR;
            float x = lx * cosA - ly * sinA;
            float y = lx * sinA + ly * cosA;
            points[i] = new Vector3(x, y, 0f);
            lineRenderer.SetPosition(i, points[i]);
        }

        mesh.Clear();
        mesh.vertices = points;
        mesh.triangles = DrawFilledTriangles(points);

        var colors = new Color[points.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = currentColor;
        mesh.colors = colors;
    }

    private int[] DrawFilledTriangles(Vector3[] points)
    {
        int triangleAmount = points.Length - 2;
        var triangles = new List<int>(triangleAmount * 3);
        for (int i = 0; i < triangleAmount; i++)
        {
            triangles.Add(0);
            triangles.Add(i + 2);
            triangles.Add(i + 1);
        }
        return triangles.ToArray();
    }
}
