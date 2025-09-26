/*
 * Haply VerseGrip → UFactory xArm TCP Bridge
 * 
 * On VerseGrip button release, send the cursor local position
 * as JSON { "x":..., "y":..., "z":... } to a Python socket server.
 * 
 * Uses UnityEngine.JsonUtility (no external dependencies).
 */

using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;
using UnityEngine;
using System;
using System.Net.Sockets;
using System.Text;

namespace Haply.Samples.Tutorials._6_VerseGripToXArm
{
    public class VerseGripToXArm : MonoBehaviour
    {
        public Inverse3Controller inverse3;
        public VerseGripController verseGrip;

        [Header("Cursor Settings")]
        [Range(0, 1)]
        public float speed = 0.5f;

        [Range(0, 0.5f)]
        public float movementLimitRadius = 0.075f;

        private Vector3 _targetPosition;

        [Header("TCP Settings")]
        public string serverIp = "192.168.1.100"; // <-- change to your Python server IP
        public int serverPort = 5005;

        private TcpClient _client;
        private NetworkStream _stream;

        [Serializable]
        private class PositionPayload
        {
            public float x;
            public float y;
            public float z;
        }

        private void Awake()
        {
            inverse3 ??= FindFirstObjectByType<Inverse3Controller>();
            verseGrip ??= FindFirstObjectByType<VerseGripController>();

            inverse3.Ready.AddListener((inverse3Controller, args) =>
            {
                _targetPosition = inverse3Controller.WorkspaceCenterLocalPosition;
            });

            // Connect to Python server
            try
            {
                _client = new TcpClient(serverIp, serverPort);
                _stream = _client.GetStream();
                Debug.Log($"Connected to xArm server at {serverIp}:{serverPort}");
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to connect to xArm server: " + e.Message);
            }
        }

        private void OnEnable()
        {
            verseGrip.DeviceStateChanged += OnDeviceStateChanged;
        }

        private void OnDisable()
        {
            verseGrip.DeviceStateChanged -= OnDeviceStateChanged;
            inverse3.Release();

            _stream?.Close();
            _client?.Close();
        }

        private void OnDeviceStateChanged(object sender, VerseGripEventArgs args)
        {
            var grip = args.DeviceController;
            var direction = grip.Orientation * Vector3.forward;

            if (grip.GetButtonDown())
            {
                _targetPosition = inverse3.CursorLocalPosition;
            }

            if (grip.GetButton())
            {
                _targetPosition += direction * (0.0001f * speed);

                var workspaceCenter = inverse3.WorkspaceCenterLocalPosition;
                _targetPosition = Vector3.ClampMagnitude(_targetPosition - workspaceCenter, movementLimitRadius)
                                 + workspaceCenter;
            }

            if (grip.GetButtonUp())
            {
                SendPositionToServer(_targetPosition);
            }

            inverse3.SetCursorLocalPosition(_targetPosition);
        }

        private void SendPositionToServer(Vector3 pos)
        {
            if (_stream == null || !_client.Connected)
            {
                Debug.LogWarning("Not connected to xArm server.");
                return;
            }

            // Unity units = meters → convert to mm for xArm
            
            // Convert Unity meters → mm
            // console.Write($"pos.x = {pos.x}");
            // console.Write($"pos.y = {pos.y}");
            Debug.Log($"pos.z = {pos.z}");

            float x = pos.x * 1000f;
            float y = pos.y * 1000f;
            float z = pos.z * -100f;

            // Clamp to xArm limits
            x = Mathf.Clamp(x, 50f, 250f);
            y = Mathf.Clamp(y, 60f, 350f);
            z = Mathf.Clamp(z, 50f, 400f);

            var payload = new PositionPayload
            {
                x = x,
                y = y,
                z = z
            };

            try
            {
                string json = JsonUtility.ToJson(payload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                // _stream.Write(bytes, 0, bytes.Length);
                Debug.Log("Sent to xArm: " + json);
            }
            catch (Exception e)
            {
                Debug.LogError("Send failed: " + e.Message);
            }
        }

        #region Optional GUI + Gizmos
        private void OnDrawGizmos()
        {
            if (inverse3 == null) return;
            Gizmos.color = Color.gray;
            Gizmos.DrawWireSphere(inverse3.WorkspaceCenterLocalPosition,
                movementLimitRadius + inverse3.Cursor.Radius);
        }

        private void OnGUI()
        {
            if (verseGrip == null) return;
            const float width = 600;
            const float height = 60;
            var rect = new Rect((Screen.width - width) / 2, Screen.height - height - 10, width, height);

            var text = verseGrip.GetButton()
                ? "Rotate VerseGrip to move cursor. Release to send position to xArm."
                : "Hold VerseGrip button to move cursor. Release to send.";

            GUI.Box(rect, text, CenteredStyle());
        }

        private static GUIStyle CenteredStyle()
        {
            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white },
                fontSize = 14
            };
            return style;
        }
        #endregion
    }
}
