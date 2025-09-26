using System.Threading;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;
using UnityEngine;

namespace Haply.Samples.Tutorials._4D_AutoBoundedFluid
{
    public class AutoBoundedFluidForceFeedback : MonoBehaviour
    {
        public Inverse3Controller inverse3;

        [Header("Fluid Properties")]
        [Range(0f, 5f)]
        public float viscosity = 0.5f;       // drag strength
        [Range(0f, 3f)]
        public float densityGradient = 1f;   // stronger near center

        [Header("Force Safety")]
        public float maxForce = 5f;

        [Header("Fluid Detection")]
        public string fluidLayerName = "FluidZone"; // all objects on this layer will be fluid

        private int fluidLayer;
        private bool _insideFluid;

        private struct SceneData
        {
            public Vector3 centerPosition;
            public bool insideFluid;
        }

        private SceneData _cachedSceneData;
        private readonly ReaderWriterLockSlim _cacheLock = new();
        private Vector3 _smoothedVelocity;

        private void Start()
        {
            inverse3 ??= FindFirstObjectByType<Inverse3Controller>();

            fluidLayer = LayerMask.NameToLayer(fluidLayerName);
            if (fluidLayer == -1)
                Debug.LogWarning($"Layer '{fluidLayerName}' does not exist. Fluid will never activate.");
        }

        private void FixedUpdate()
        {
            // Compute center and inside state on main thread
            Vector3 center = transform.position;

            Vector3 localCursor = inverse3.CursorLocalPosition;
            Vector3 worldCursor = inverse3.transform.TransformPoint(localCursor);

            bool inside = false;
            if (fluidLayer != -1)
            {
                // small sphere overlap to detect any fluid object
                Collider[] hits = Physics.OverlapSphere(worldCursor, 0.001f, 1 << fluidLayer);
                inside = hits.Length > 0;
            }

            _cacheLock.EnterWriteLock();
            try
            {
                _cachedSceneData.centerPosition = center;
                _cachedSceneData.insideFluid = inside;
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
            // Smooth velocity
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, cursorVel, 0.2f);

            // Base viscous drag
            Vector3 drag = -viscosity * _smoothedVelocity;

            // Density factor stronger near center
            float distance = (cursorPos - center).magnitude;
            float densityFactor = 1f + densityGradient / (1f + Mathf.Max(distance, 0.01f));

            // Clamp max force
            return Vector3.ClampMagnitude(drag * densityFactor, maxForce);
        }

        private void OnDeviceStateChanged(object sender, Inverse3EventArgs args)
        {
            var inverse3 = args.DeviceController;
            var sceneData = GetSceneData();

            if (!sceneData.insideFluid)
            {
                inverse3.SetCursorLocalForce(Vector3.zero);
                return;
            }

            Vector3 force = FluidForce(inverse3.CursorLocalPosition,
                                       inverse3.CursorLocalVelocity,
                                       sceneData.centerPosition);

            inverse3.SetCursorLocalForce(force);
        }
    }
}
