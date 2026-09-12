using UnityEngine;

// Netcode for GameObjects is no longer used - the server is now a standalone .NET process
// (Server/CellSimulator.Server) talking a custom UDP protocol to GameClient.cs.
// Kept as an inert empty component so scene references to it don't break.
public class NetworkBootstrap : MonoBehaviour
{
}
