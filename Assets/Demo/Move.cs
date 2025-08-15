using UnityEngine;

public class Move : MonoBehaviour
{
    [SerializeField] private float force = 100;
    private void Start()
    {
        InvokeRepeating("Jump", 0, 5);
    }

    private void Jump()
    {
        GetComponent<Rigidbody2D>().AddForce(Vector3.right * force, ForceMode2D.Impulse);
        force *= -1;
    }
}