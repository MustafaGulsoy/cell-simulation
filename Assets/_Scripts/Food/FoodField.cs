using System.Collections.Generic;
using UnityEngine;

// All the food on the map. The server tracks ~10,000 pellets on the default map, but a player only ever
// sees the few hundred around them - and 10,000 live GameObjects (each with its own Update) is far too
// heavy for a phone. So this keeps every pellet's POSITION in a dictionary and only gives the ones near
// the camera an actual Food object, recycled from a pool as the camera moves.
public class FoodField
{
    private readonly GameObject prefab;
    private readonly Dictionary<uint, Vector2> positions = new Dictionary<uint, Vector2>();
    private readonly Dictionary<uint, Food> active = new Dictionary<uint, Food>();
    private readonly Stack<Food> pool = new Stack<Food>();
    private readonly List<uint> scratch = new List<uint>();

    public FoodField(GameObject foodPrefab)
    {
        prefab = foodPrefab;
    }

    /// <summary>Total pellets known (near or far).</summary>
    public int KnownCount
    {
        get { return positions.Count; }
    }

    /// <summary>Pellets that currently have a GameObject.</summary>
    public int ActiveCount
    {
        get { return active.Count; }
    }

    /// <summary>Closest known pellet to a point (diagnostics / the automated test player).</summary>
    public bool Nearest(Vector2 from, out Vector2 position)
    {
        float best = float.MaxValue;
        position = from;
        foreach (var p in positions.Values)
        {
            float d = (p - from).sqrMagnitude;
            if (d < best)
            {
                best = d;
                position = p;
            }
        }
        return best < float.MaxValue;
    }

    /// <summary>A position update from the network.</summary>
    public void SetPosition(uint id, Vector2 position)
    {
        positions[id] = position;

        Food obj;
        if (active.TryGetValue(id, out obj))
        {
            // A jump bigger than a flying pellet can travel in a tick means "eaten and respawned
            // elsewhere": retire the object here (the pellet vanishes where it was eaten) instead of
            // sliding it across the map; Refresh brings it back if the new spot is near the camera.
            if (((Vector2)obj.transform.position - position).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                active.Remove(id);
                obj.gameObject.SetActive(false);
                pool.Push(obj);
            }
            else
            {
                obj.SetPosition(position); // ordinary movement (a thrown pellet in flight)
            }
        }
    }

    private const float TeleportDistance = 8f;

    /// <summary>Gives every pellet within <paramref name="radius"/> of <paramref name="focus"/> a Food
    /// object and retires the ones that fell out of range. Cheap enough to run a few times a second.</summary>
    public void Refresh(Vector2 focus, float radius)
    {
        float r2 = radius * radius;
        float releaseR2 = (radius * 1.25f) * (radius * 1.25f); // hysteresis: don't flicker at the edge

        scratch.Clear();
        foreach (var kv in active)
        {
            if ((kv.Value.transform.position - (Vector3)focus).sqrMagnitude > releaseR2 || !positions.ContainsKey(kv.Key))
            {
                scratch.Add(kv.Key);
            }
        }
        foreach (var id in scratch)
        {
            var obj = active[id];
            active.Remove(id);
            obj.gameObject.SetActive(false);
            pool.Push(obj);
        }

        foreach (var kv in positions)
        {
            if (active.ContainsKey(kv.Key)) continue;
            if ((kv.Value - focus).sqrMagnitude > r2) continue;

            Food obj = pool.Count > 0 ? pool.Pop() : Object.Instantiate(prefab).GetComponent<Food>();
            obj.gameObject.SetActive(true);
            obj.Snap(kv.Value);
            active[kv.Key] = obj;
        }
    }
}
