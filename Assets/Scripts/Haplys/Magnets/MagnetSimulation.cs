using System.Threading;
using UnityEngine;

namespace HaplyMagnets
{
    public class MagnetSimulation : MonoBehaviour
    {
        [Header("Spheres")]
        public int   numSpheres   = 16;
        public float sphereRadius = 0.05f;
        public Material sphereMaterial; 

        [Header("Magnet Physics")]
        [Range(0f, 500f)] public float magnetStrength  = 25f;   // attraction
        [Range(0f, 500f)] public float repulseStrength = 80f;   // contact repulsion
        public float magnetRange  = 3.5f;   // attraction starts at 3.5x radius
        public float contactRange = 2.05f;  // repulsion starts at 2.05x radius (just touching)

        [Header("Boundary")]
        public float boundaryRadius = 1.5f;   // world units — balls pushed back inside this sphere
        [Range(0f, 200f)] public float boundaryStiffness = 50f;

        [Header("Haptic Spring")]
        [Range(0f, 200f)] public float hapticSpringStrength = 40f;
        [Range(0f,  20f)] public float hapticDamping        = 3f;

        // Shared with MagnetHaptics — written here, read by haptic thread
        [HideInInspector] public MagnetBall[] balls;

        // Thread-safe position/velocity cache read by haptic thread
        public struct BallState
        {
            public Vector3 position;
            public Vector3 velocity;
            public float   radius;
        }

        private BallState[] _stateCache;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        // Force request FROM haptic thread → applied in FixedUpdate on main thread
        private Vector3 _pendingHapticForce;
        private readonly object _forceLock = new object();

        // ── Spawn ────────────────────────────────────────────────────────
        private void Awake()
        {
            balls       = new MagnetBall[numSpheres];
            _stateCache = new BallState[numSpheres];

            for (int i = 0; i < numSpheres; i++)
            {
                // Spiral placement identical to CHAI3D original
                float angle = i * 1.0f;
                float r     = sphereRadius * 2.2f * (i + 2);
                Vector3 pos = new Vector3(
                    r * Mathf.Cos(angle),
                    sphereRadius + 0.001f,   // just above ground
                    r * Mathf.Sin(angle)
                );

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"MagnetBall_{i}";
                go.transform.SetParent(transform);
                go.transform.position   = pos;
                go.transform.localScale = Vector3.one * sphereRadius * 2f;

                if (sphereMaterial != null)
                    go.GetComponent<Renderer>().material = sphereMaterial;

                // Rigidbody — Unity owns gravity and collisions
                var rb             = go.AddComponent<Rigidbody>();
                rb.mass            = 0.05f;
                rb.linearDamping   = 0.5f;
                rb.angularDamping  = 0.5f;
                rb.interpolation   = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

                // SphereCollider is added by CreatePrimitive — just tune it
                var col            = go.GetComponent<SphereCollider>();
                col.material       = CreateBouncyMaterial();

                var ball           = go.AddComponent<MagnetBall>();
                balls[i]           = ball;
            }
        }

        private PhysicsMaterial CreateBouncyMaterial()
        {
            var mat               = new PhysicsMaterial("MagnetBall");
            mat.bounciness        = 0.3f;
            mat.dynamicFriction   = 0.4f;
            mat.staticFriction    = 0.4f;
            mat.frictionCombine   = PhysicsMaterialCombine.Average;
            mat.bounceCombine     = PhysicsMaterialCombine.Average;
            return mat;
        }

        // ── Cache write (main thread, every FixedUpdate) ─────────────────
        private void UpdateCache()
        {
            _lock.EnterWriteLock();
            try
            {
                for (int i = 0; i < balls.Length; i++)
                {
                    _stateCache[i].position = balls[i].rb.position;
                    _stateCache[i].velocity = balls[i].rb.linearVelocity;
                    _stateCache[i].radius   = sphereRadius;
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        // ── Cache read (haptic thread) ────────────────────────────────────
        public BallState[] GetStateSnapshot()
        {
            _lock.EnterReadLock();
            try
            {
                var copy = new BallState[_stateCache.Length];
                System.Array.Copy(_stateCache, copy, copy.Length);
                return copy;
            }
            finally { _lock.ExitReadLock(); }
        }

        // ── Receive force from haptic thread → apply in FixedUpdate ──────
        public void QueueHapticForce(Vector3 force)
        {
            lock (_forceLock) { _pendingHapticForce = force; }
        }

        // ── FixedUpdate: magnets + haptic force via Rigidbody.AddForce ───
        private void FixedUpdate()
        {
            for (int i = 0; i < balls.Length; i++)
            {
                Vector3 posI = balls[i].rb.position;

                // ── Sphere-sphere forces ──────────────────────────────────
                for (int j = i + 1; j < balls.Length; j++)
                {
                    Vector3 posJ = balls[j].rb.position;
                    Vector3 dir  = posJ - posI;
                    float   dist = dir.magnitude;
                    if (dist < 1e-4f) continue;
                    dir /= dist; // normalize

                    float diameter = sphereRadius * 2f;
                    float contact  = diameter * contactRange;
                    float magEnd   = diameter * magnetRange;

                    if (dist < contact)
                    {
                        // Repulsion — pushes overlapping balls apart
                        float overlap   = contact - dist;
                        Vector3 repulse = -dir * overlap * repulseStrength;
                        balls[i].rb.AddForce(repulse,  ForceMode.Force);
                        balls[j].rb.AddForce(-repulse, ForceMode.Force);
                    }
                    else if (dist < magEnd)
                    {
                        // Attraction — pulls nearby balls together
                        // Force peaks just outside contact, fades to zero at magEnd
                        float gap      = dist - contact;
                        float maxGap   = magEnd - contact;
                        float falloff  = 1f - (gap / maxGap);   // 1.0 near contact, 0.0 at edge
                        Vector3 attract = dir * falloff * magnetStrength;
                        balls[i].rb.AddForce(attract,  ForceMode.Force);
                        balls[j].rb.AddForce(-attract, ForceMode.Force);
                    }
                }

                // ── Soft boundary — invisible sphere wall ─────────────────
                float distFromCenter = posI.magnitude;
                if (distFromCenter > boundaryRadius)
                {
                    Vector3 inward = -posI.normalized;
                    float   excess = distFromCenter - boundaryRadius;
                    balls[i].rb.AddForce(inward * excess * boundaryStiffness, ForceMode.Force);
                }
            }

            // ── Haptic spring force on ball[0] ────────────────────────────
            Vector3 hf;
            lock (_forceLock) { hf = _pendingHapticForce; }
            if (hf.sqrMagnitude > 0f)
                balls[0].rb.AddForce(hf, ForceMode.Force);

            // ── Update cache for haptic thread ────────────────────────────
            UpdateCache();
        }
    }
}