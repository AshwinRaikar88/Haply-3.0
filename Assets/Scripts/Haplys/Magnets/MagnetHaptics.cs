// Attach this to your Haptic Controller GameObject (same one with Inverse3Controller)
using System.Threading;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;
using UnityEngine;

namespace HaplyMagnets
{
    public class MagnetHaptics : MonoBehaviour
    {
        [Header("References")]
        public Inverse3Controller inverse3;
        public MagnetSimulation   simulation;

        [Header("Haptic Tuning")]
        [Range(0f, 800f)] public float stiffness = 80f;   // was 400 — softer spring = feels heavier
        [Range(0f,  10f)] public float damping   = 4f;    // higher damping = feels like moving through fluid

        private bool _forceEnabled = false;

        private void Start()
        {
            if (inverse3   == null) inverse3   = GetComponent<Inverse3Controller>();
            if (simulation == null) simulation = FindFirstObjectByType<MagnetSimulation>();
        }

        private void OnEnable()  => inverse3.DeviceStateChanged += OnDeviceStateChanged;
        private void OnDisable()
        {
            inverse3.DeviceStateChanged -= OnDeviceStateChanged;
            inverse3.Release();
        }

        // ── Haptic thread ~1kHz ──────────────────────────────────────────
        private void OnDeviceStateChanged(object sender, Inverse3EventArgs args)
        {
            var device = args.DeviceController;
            Vector3 cursorPos = device.CursorPosition;
            Vector3 cursorVel = device.CursorVelocity;

            var states = simulation.GetStateSnapshot();
            if (states == null || states.Length == 0) return;

            Vector3 forceOnCursor = Vector3.zero;
            Vector3 forceOnBall0  = Vector3.zero;

            float diameter = simulation.sphereRadius * 2f;

            for (int i = 0; i < states.Length; i++)
            {
                Vector3 delta = states[i].position - cursorPos;
                float   dist  = delta.magnitude;
                if (dist < 1e-5f) continue;
                Vector3 dir = delta / dist;

                float contact = (states[i].radius + device.Cursor.Radius);
                float magEnd  = contact + diameter * (simulation.magnetRange - simulation.contactRange);

                if (dist < contact)
                {
                    // Feel the ball — hard contact
                    float penetration  = contact - dist;
                    Vector3 relVel     = cursorVel - states[i].velocity;
                    forceOnCursor += dir * penetration * stiffness - relVel * damping;

                    if (i == 0)
                        forceOnBall0 -= dir * penetration * simulation.hapticSpringStrength;
                }
                else if (dist < magEnd)
                {
                    // Feel the magnetic pull — subtle attraction toward each ball
                    float gap     = dist - contact;
                    float maxGap  = magEnd - contact;
                    float falloff = 1f - (gap / maxGap);
                    forceOnCursor += dir * falloff * stiffness * 0.15f; // softer than contact

                    if (i == 0)
                        forceOnBall0 -= dir * falloff * simulation.hapticSpringStrength * 0.15f;
                }
            }

            // Safety ramp-up
            if (!_forceEnabled)
            {
                if (forceOnCursor.magnitude < 1.0f) _forceEnabled = true;
                else forceOnCursor = Vector3.zero;
            }

            device.SetCursorForce(forceOnCursor);
            simulation.QueueHapticForce(forceOnBall0);
        }
    }
}