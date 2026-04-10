// Attach to an empty GameObject called BondManager
// Handles all physics, bond state, thread-safe data exchange,
// and force output for both haptic devices.

using System.Threading;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HaplyBond
{
    public class BondSimulation : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────
        [Header("Haptic Devices")]
        public Inverse3Controller deviceA;
        public Inverse3Controller deviceB;

        [Header("Cursor Visuals")]
        public float cursorDisplayRadius = 0.012f; // metres, visual only

        [Header("Bond Parameters")]
        [Range(0.01f, 0.2f)] public float bondRadius    = 0.04f;  // distance to form bond
        [Range(0.01f, 0.3f)] public float snapDistance  = 0.12f;  // distance bond breaks
        [Range(0f,  800f)]    public float bondStiffness = 300f;   // spring strength
        [Range(0f,   20f)]    public float bondDamping   = 3f;     // velocity damping

        [Header("Contact (cursors touching)")]
        [Range(0f,  800f)]    public float contactStiffness = 500f;
        [Range(0f,   20f)]    public float contactDamping   = 4f;

        [Header("Visuals")]
        public LineRenderer bondLine;   // assign a LineRenderer in inspector for the bond visual

        // ── Bond state (written by haptic thread, read by main) ──────────
        public enum BondState { Unbound, Bonded, Snapped }

        private BondState _bondState     = BondState.Unbound;
        private float     _bondStretch   = 0f;   // 0..1 where 1 = about to snap
        private readonly object _stateLock = new object();

        // ── Thread-safe cursor cache (written by main, read by haptics) ──
        private struct CursorCache
        {
            public Vector3 posA, posB;
            public Vector3 velA, velB;
            public float   radiusA, radiusB;
        }

        private CursorCache _cache;
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();

        // ── Snap impulse state ───────────────────────────────────────────
        private bool  _snapImpulseActive  = false;
        private float _snapImpulseTimer   = 0f;
        private const float SnapImpulseDuration = 0.08f;  // seconds of buzz
        private const float SnapImpulseForce    = 1.5f;   // N — brief kick on snap

        // ── Safety ramp ──────────────────────────────────────────────────
        private bool _forceEnabledA = false;
        private bool _forceEnabledB = false;

        // ── Snap cooldown (prevents instant re-bond after snap) ──────────
        private float _snapCooldown = 0f;
        private const float SnapCooldownDuration = 0.4f;

        // ─────────────────────────────────────────────────────────────────
        // Unity lifecycle
        // ─────────────────────────────────────────────────────────────────
        private void Start()
        {
            // Subscribe both devices to the same haptic callback
            deviceA.DeviceStateChanged += OnDeviceAStateChanged;
            deviceB.DeviceStateChanged += OnDeviceBStateChanged;

            // Initialise cache once devices are ready
            deviceA.Ready.AddListener((d, _) => RefreshCache());
            deviceB.Ready.AddListener((d, _) => RefreshCache());
        }

        private void OnDisable()
        {
            deviceA.DeviceStateChanged -= OnDeviceAStateChanged;
            deviceB.DeviceStateChanged -= OnDeviceBStateChanged;
            deviceA.Release();
            deviceB.Release();
        }

        private void Update()
        {
            // Tick snap cooldown
            if (_snapCooldown > 0f)
                _snapCooldown -= Time.deltaTime;

            // Tick snap impulse
            if (_snapImpulseActive)
            {
                _snapImpulseTimer -= Time.deltaTime;
                if (_snapImpulseTimer <= 0f)
                    _snapImpulseActive = false;
            }

            // Refresh cache every frame
            RefreshCache();

            // Update bond line visual
            UpdateBondVisual();
        }

        // ─────────────────────────────────────────────────────────────────
        // Cache
        // ─────────────────────────────────────────────────────────────────
        private void RefreshCache()
        {
            if (!deviceA.IsReady || !deviceB.IsReady) return;

            _cacheLock.EnterWriteLock();
            try
            {
                _cache.posA    = deviceA.CursorPosition;
                _cache.posB    = deviceB.CursorPosition;
                _cache.velA    = deviceA.CursorVelocity;
                _cache.velB    = deviceB.CursorVelocity;
                _cache.radiusA = deviceA.Cursor.Radius;
                _cache.radiusB = deviceB.Cursor.Radius;
            }
            finally { _cacheLock.ExitWriteLock(); }
        }

        private CursorCache GetCache()
        {
            _cacheLock.EnterReadLock();
            try { return _cache; }
            finally { _cacheLock.ExitReadLock(); }
        }

        // ─────────────────────────────────────────────────────────────────
        // Core force calculation — called by BOTH haptic threads
        // Returns force for device A; force for B is equal and opposite
        // ─────────────────────────────────────────────────────────────────
        private void CalculateForces(CursorCache c,
                                     out Vector3 forceA,
                                     out Vector3 forceB,
                                     out BondState newState,
                                     out float stretch)
        {
            forceA   = Vector3.zero;
            forceB   = Vector3.zero;
            stretch  = 0f;

            Vector3 delta = c.posB - c.posA;
            float   dist  = delta.magnitude;

            if (dist < 1e-6f)
            {
                newState = _bondState;
                return;
            }

            Vector3 dir     = delta / dist;
            float   contact = c.radiusA + c.radiusB;

            // ── 1. Hard contact (cursors overlapping) ────────────────────
            if (dist < contact)
            {
                float   penetration = contact - dist;
                Vector3 relVel      = c.velA - c.velB;
                Vector3 f           = dir * penetration * contactStiffness
                                    - relVel * contactDamping;

                forceA   -= f;   // A pushed away from B
                forceB   += f;   // B pushed away from A
                newState  = BondState.Unbound;
                return;
            }

            // ── 2. Bond logic ─────────────────────────────────────────────
            BondState current;
            lock (_stateLock) { current = _bondState; }

            // Form bond when cursors enter bondRadius
            if (current == BondState.Unbound && dist <= bondRadius && _snapCooldown <= 0f)
                current = BondState.Bonded;

            // Break bond when cursors exceed snapDistance
            if (current == BondState.Bonded && dist >= snapDistance)
            {
                current            = BondState.Snapped;
                _snapImpulseActive = true;
                _snapImpulseTimer  = SnapImpulseDuration;
                _snapCooldown      = SnapCooldownDuration;
            }

            // After snap, return to Unbound (Snapped is a transient state)
            if (current == BondState.Snapped && !_snapImpulseActive)
                current = BondState.Unbound;

            newState = current;

            // ── 3. Bond spring force ──────────────────────────────────────
            if (current == BondState.Bonded)
            {
                float   extension  = dist - contact;
                float   maxExtend  = snapDistance - contact;
                stretch            = Mathf.Clamp01(extension / maxExtend);

                // Spring: grows linearly with stretch
                Vector3 relVel     = c.velA - c.velB;
                Vector3 spring     = dir * extension * bondStiffness;
                Vector3 damp       = dir * Vector3.Dot(relVel, -dir) * bondDamping;

                forceA  += spring + damp;   // A pulled toward B
                forceB  -= spring + damp;   // B pulled toward A
            }

            // ── 4. Snap impulse — brief kick outward on both devices ──────
            if (_snapImpulseActive)
            {
                // Oscillating impulse for tactile "ping"
                float buzz   = Mathf.Sin(_snapImpulseTimer * Mathf.PI / SnapImpulseDuration
                                         * 20f);  // 10 cycles during impulse
                forceA      -= dir * buzz * SnapImpulseForce;
                forceB      += dir * buzz * SnapImpulseForce;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Haptic thread callbacks
        // ─────────────────────────────────────────────────────────────────
        private void OnDeviceAStateChanged(object sender, Inverse3EventArgs args)
        {
            var device = args.DeviceController;
            var c      = GetCache();

            CalculateForces(c, out Vector3 fA, out Vector3 fB,
                            out BondState state, out float stretch);

            // Write bond state back (thread-safe)
            lock (_stateLock)
            {
                _bondState   = state;
                _bondStretch = stretch;
            }

            fA = ApplySafetyRamp(fA, ref _forceEnabledA);
            device.SetCursorForce(fA);
        }

        private void OnDeviceBStateChanged(object sender, Inverse3EventArgs args)
        {
            var device = args.DeviceController;
            var c      = GetCache();

            CalculateForces(c, out Vector3 fA, out Vector3 fB,
                            out BondState _, out float _);

            fB = ApplySafetyRamp(fB, ref _forceEnabledB);
            device.SetCursorForce(fB);
        }

        private Vector3 ApplySafetyRamp(Vector3 force, ref bool enabled)
        {
            if (!enabled)
            {
                if (force.magnitude < 0.5f) enabled = true;
                else return Vector3.zero;
            }
            return force;
        }

        // ─────────────────────────────────────────────────────────────────
        // Visuals
        // ─────────────────────────────────────────────────────────────────
        private void UpdateBondVisual()
        {
            if (bondLine == null) return;
            if (!deviceA.IsReady || !deviceB.IsReady) return;

            BondState state;
            float stretch;
            lock (_stateLock)
            {
                state   = _bondState;
                stretch = _bondStretch;
            }

            bondLine.enabled = (state == BondState.Bonded);

            if (state == BondState.Bonded)
            {
                bondLine.SetPosition(0, deviceA.CursorPosition);
                bondLine.SetPosition(1, deviceB.CursorPosition);

                // Colour: green → red as bond stretches
                Color c = Color.Lerp(Color.green, Color.red, stretch);
                bondLine.startColor = c;
                bondLine.endColor   = c;

                // Width pulses as tension grows
                float w = Mathf.Lerp(0.002f, 0.008f, stretch);
                bondLine.startWidth = w;
                bondLine.endWidth   = w;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Public read for UI
        // ─────────────────────────────────────────────────────────────────
        public (BondState state, float stretch) GetBondInfo()
        {
            lock (_stateLock) { return (_bondState, _bondStretch); }
        }
    }
}