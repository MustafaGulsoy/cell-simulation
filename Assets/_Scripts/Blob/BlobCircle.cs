using System.Collections.Generic;
using UnityEngine;

public class BlobCircle : MonoBehaviour
{
    [SerializeField] public LineRenderer lineRenderer;
    [SerializeField] private MeshRenderer meshRenderer;
    [SerializeField] private MeshFilter filter;

    private Mesh mesh;

    private Vector3[] polygonPoints;
    private int[] polygonTriangles;
    private float baseRadius;

    // Small membrane jiggle around the rim, purely cosmetic. wobblePhase is randomised per instance
    // so a cluster of blobs doesn't pulse in lockstep. Stepped at WobbleStepSeconds rather than every
    // render frame - the motion is slow/subtle enough that 20 Hz looks identical to 60 Hz, for a third
    // of the per-blob vertex math and mesh re-upload (this runs once per visible blob, on a mobile target).
    // ponytail: still a full CPU vertex rewrite per step; move to a vertex shader if the live blob
    // count ever grows into the hundreds.
    private const float WobbleAmplitude = 0.035f;
    private const float WobbleFrequency = 5f;
    private const float WobbleSpeed = 2.2f;
    private const float WobbleStepSeconds = 0.05f;
    private float wobblePhase;
    private float nextWobbleTime;

    private void Awake()
    {
        mesh = new Mesh();
        filter.mesh = mesh;
        wobblePhase = Random.value * Mathf.PI * 2f;
    }

    private void Update()
    {
        if (polygonPoints == null || !meshRenderer.enabled) return;
        if (Time.time < nextWobbleTime) return;
        nextWobbleTime = Time.time + WobbleStepSeconds;

        float t = Time.time * WobbleSpeed + wobblePhase;
        int sides = polygonPoints.Length;
        for (int i = 0; i < sides; i++)
        {
            float angle = (float)i / sides * 2f * Mathf.PI;
            float r = baseRadius * (1f + WobbleAmplitude * Mathf.Sin(angle * WobbleFrequency + t));
            float x = Mathf.Cos(angle) * r;
            float y = Mathf.Sin(angle) * r;
            polygonPoints[i] = new Vector3(x, y, 0f);
            if (lineRenderer.enabled) lineRenderer.SetPosition(i, new Vector3(x, y, 0f));
        }
        mesh.vertices = polygonPoints;
        mesh.RecalculateBounds();
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
        lineRenderer.positionCount = steps;
        lineRenderer.loop = true;
        lineRenderer.startWidth = 0.6f;

        Color lineColor = color / 1.15f;
        lineColor.a = 1;
        lineRenderer.startColor = lineColor;
        lineRenderer.endColor = lineColor;

        for(int i = 0; i < steps; i++)
        {
            float circumference = (float)i / steps;

            float radian = circumference * 2 * Mathf.PI;

            float xScale = Mathf.Cos(radian);
            float yScale = Mathf.Sin(radian);

            float x = xScale * radius;
            float y = yScale * radius;

            lineRenderer.SetPosition(i, new Vector2(x, y));
        }
    }

    public void DrawFilledMesh(int sides, float radius, Color color)
    {
        baseRadius = radius;
        polygonPoints = GetCircumferencePoints(sides, radius).ToArray();
        polygonTriangles = DrawFilledTriangles(polygonPoints);

        mesh.Clear();
        mesh.vertices = polygonPoints;
        mesh.triangles = polygonTriangles;

        int length = mesh.vertices.Length;
        Color[] colors = new Color[length];

        for (int i = 0; i < length; i++)
        {
            colors[i] = color;
        }

        mesh.colors = colors;
    }

    private List<Vector3> GetCircumferencePoints(int sides, float radius)
    {
        List<Vector3> points = new List<Vector3>();
        float circumferenceProgressPerStep = (float)1 / sides;
        float TAU = 2 * Mathf.PI;
        float radianProgressPerStep = circumferenceProgressPerStep * TAU;

        for(int i = 0; i < sides; i++)
        {
            float currentRadian = radianProgressPerStep * i;
            points.Add(new Vector3(Mathf.Cos(currentRadian) * radius, Mathf.Sin(currentRadian) * radius,0));
        }
        return points;
    }

    private int[] DrawFilledTriangles(Vector3[] points)
    {
        int triangleAmount = points.Length - 2;
        List<int> newTriangles = new List<int>();
        for(int i = 0; i < triangleAmount; i++)
        {
            newTriangles.Add(0);
            newTriangles.Add(i + 2);
            newTriangles.Add(i + 1);
        }
        return newTriangles.ToArray();
    }
}
