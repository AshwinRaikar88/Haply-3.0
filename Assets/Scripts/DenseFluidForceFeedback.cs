using System.Threading;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;
using UnityEngine;

namespace Haply.Samples.Tutorials._4C_BoundedFluidForceFeedback
{
    public class BoundedFluidForceFeedback : MonoBehaviour
    {
        public Inverse3Controller inverse3;

        [Header("Fluid Properties")]
        [Range(0f, 5f)]
        public float viscosity = 0.5f;

        [Range(0f, 3f)]
        public float densityGradient = 1f;

        [Header("Force Safety")]
        public float maxForce = 5f;

        [Header("Fluid Volume")]
        public Collider fluidVolume; // assign in inspector

        private struct SceneData
        {
            public Vector3 centerPosition;
            public bool insideVolume;
        }

        private SceneData _cachedSceneData;
        private readonly ReaderWriterLockSlim _cacheLock = new();

        private Vector3 _smoothedVelocity;

        private void Start()
        {
            inverse3 ??= FindFirstObjectByType<Inverse3Controller>();

            if (fluidVolume == null)
                Debug.LogWarning("No fluid volume assigned. Fluid force will never activate.");
        }

        private void FixedUpdate()
        {
            // Compute inside/outside on main thread
            bool inside = false;
            if (fluidVolume != null)
            {
                Vector3 localCursor = inverse3.CursorLocalPosition;
                Vector3 worldCursor = inverse3.transform.TransformPoint(localCursor);

                Vector3 closest = fluidVolume.ClosestPoint(worldCursor);
                inside = (closest - worldCursor).sqrMagnitude < 1e-6f;

            }

            _cacheLock.EnterWriteLock();
            try
            {
                _cachedSceneData.centerPosition = transform.position;
                _cachedSceneData.insideVolume = inside;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private SceneData GetSceneData()
        {
            _cacheLock.EnterReadLock();
            try
            {
                return _cachedSceneData;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        private void OnEnable()
        {
            inverse3.DeviceStateChanged += OnDeviceStateChanged;
        }

        private void OnDisable()
        {
            inverse3.DeviceStateChanged -= OnDeviceStateChanged;
            inverse3.Release();
        }

        private Vector3 FluidForce(Vector3 cursorPos, Vector3 cursorVel, Vector3 center)
        {
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, cursorVel, 0.2f);

            Vector3 drag = -viscosity * _smoothedVelocity;

            float distance = (cursorPos - center).magnitude;
            float densityFactor = 1f + densityGradient / (1f + Mathf.Max(distance, 0.01f));

            return Vector3.ClampMagnitude(drag * densityFactor, maxForce);
        }

        private void OnDeviceStateChanged(object sender, Inverse3EventArgs args)
        {
            var inverse3 = args.DeviceController;
            var sceneData = GetSceneData();

            if (!sceneData.insideVolume)
            {
                inverse3.SetCursorLocalForce(Vector3.zero);
                return;
            }

            var force = FluidForce(inverse3.CursorLocalPosition,
                                   inverse3.CursorLocalVelocity,
                                   sceneData.centerPosition);

            inverse3.SetCursorLocalForce(force);
        }
    }
}
