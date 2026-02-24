using UnityEngine;

public class OrbMovementTest : MonoBehaviour
{
    public OrbController2D orb;

    void Start()
    {
        if (orb == null)
        {
            Debug.LogError("NO ORB ASSIGNED!");
            return;
        }

        Debug.Log("=== ORB MOVEMENT TEST ===");
        Debug.Log("Orb Position: " + orb.transform.position);

        // Try to move
        InvokeRepeating(nameof(TestMove), 1f, 2f);
    }

    void TestMove()
    {
        Debug.Log("Sending RIGHT command...");
        orb.ReceiveCommand("R");
    }
}
