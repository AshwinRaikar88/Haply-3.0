using UnityEngine;

namespace HaplyMagnets
{
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public class MagnetBall : MonoBehaviour
    {
        [HideInInspector] public Rigidbody rb;
        [HideInInspector] public SphereCollider col;

        private void Awake()
        {
            rb  = GetComponent<Rigidbody>();
            col = GetComponent<SphereCollider>();
        }
    }
}