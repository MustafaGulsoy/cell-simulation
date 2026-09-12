using System.Collections;
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

    private void Awake()
    {
        mesh = new Mesh();
        filter.mesh = mesh;
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
