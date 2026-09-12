using UnityEngine;

// Movement is now server-authoritative (GameClient sends this joystick's direction as Input packets).
// Kept only as a holder for the scene's joystick reference so existing prefab wiring doesn't break.
public class PlayerMovement : MonoBehaviour
{
    [SerializeField] public Joystick joystick;
}
