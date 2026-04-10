using UnityEngine;
using UnityEngine.UI;
using System;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.Collections;

/// <summary>
/// Streams polar/spherical position to the xArm server and exposes Grab / Release
/// methods you can wire to UI Buttons in the Inspector.
///
/// Instead of manually setting XYZ min/max limits, this script derives azimuth
/// (horizontal pan), elevation (vertical tilt), and reach (extension distance)
/// from the tracked object's world position relative to a polar origin point.
/// These are then converted back to Cartesian mm for the xArm protocol.
/// </summary>
public class PolarPositionMapper : MonoBehaviour
{
    // ── Polar / Spherical Mapping ──────────────────────────────────────────
    [Header("Polar Mapping")]

    [Tooltip("The pivot/shoulder point the arm rotates around. Drag an empty GameObject here. Leave null to use world origin.")]
    public Transform polarOrigin;

    [Tooltip("Total azimuth (horizontal pan) swing in degrees. E.g. 120 means the arm sweeps ±60° left/right.")]
    public float azimuthRangeDeg = 120f;

    [Tooltip("Total elevation (vertical tilt) swing in degrees. E.g. 90 means the arm sweeps ±45° up/down.")]
    public float elevationRangeDeg = 90f;

    [Tooltip("Minimum reach in robot mm (arm fully retracted).")]
    public float reachMinMM = 150f;

    [Tooltip("Maximum reach in robot mm (arm fully extended).")]
    public float reachMaxMM = 500f;

    [Tooltip("The Unity-space distance that maps to reachMaxMM.")]
    public float unityMaxReach = 0.4f;

    [Tooltip("Height offset in mm added to Z so elevation=0° is a safe working height above the base.")]
    public float zOffsetMM = 200f;

    [Tooltip("Flip azimuth direction if robot and Unity horizontal axes disagree.")]
    public bool flipAzimuth = false;

    [Tooltip("Flip elevation direction if robot and Unity vertical axes disagree.")]
    public bool flipElevation = false;

    // ── TCP ───────────────────────────────────────────────────────────────
    [Header("Network")]
    public string serverIP   = "192.168.1.188";
    public int    serverPort = 5005;

    // ── Timing ────────────────────────────────────────────────────────────
    [Header("Timing")]
    public float sendInterval   = 0.5f;
    public float reconnectDelay = 3f;

    // ── UI ────────────────────────────────────────────────────────────────
    [Header("Status")]
    public TMPro.TextMeshProUGUI statusLabel;

    [Header("Gripper Buttons")]
    [Tooltip("Drag your Grab UI Button here.")]
    public Button grabButton;

    [Tooltip("Drag your Release UI Button here.")]
    public Button releaseButton;

    [Header("Debug Labels")]
    public TMPro.TextMeshProUGUI azimuthLbl;
    public TMPro.TextMeshProUGUI elevationLbl;
    public TMPro.TextMeshProUGUI reachLbl;
    public TMPro.TextMeshProUGUI xArmXLbl;
    public TMPro.TextMeshProUGUI xArmYLbl;
    public TMPro.TextMeshProUGUI xArmZLbl;

    // ══════════════════════════════════════════════════════════════════════
    // Private state
    // ══════════════════════════════════════════════════════════════════════

    private TcpClient     _client;
    private NetworkStream _stream;

    private float         _sendTimer  = 0f;
    private bool          _connecting = false;
    private bool          _rejected   = false;

    private readonly StringBuilder _recvBuf = new StringBuilder();

    public enum ConnState { Disconnected, Connecting, Online, Rejected }
    private ConnState _connState = ConnState.Disconnected;

    // ══════════════════════════════════════════════════════════════════════
    void Start()
    {
        SetStatus(ConnState.Disconnected, "Starting…");

        if (grabButton    != null) grabButton   .onClick.AddListener(OnGrabPressed);
        if (releaseButton != null) releaseButton.onClick.AddListener(OnReleasePressed);

        SetGripperButtonsInteractable(false);
        TryConnect();
    }

    void Update()
    {
        if (_rejected) return;

        if (!IsConnected())
        {
            if (!_connecting)
            {
                if (_connState == ConnState.Online)
                    SetStatus(ConnState.Disconnected, "Connection lost");
                TryConnect();
            }
            return;
        }

        if (_connState != ConnState.Online) return;

        DrainIncoming();

        _sendTimer += Time.deltaTime;
        if (_sendTimer < sendInterval) return;
        _sendTimer = 0f;
        SendPosition();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Gripper public API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Called by the Grab UI Button.</summary>
    public void OnGrabPressed()
    {
        Debug.Log("[Gripper] Grab pressed");
        SendRaw("{\"gripper\":\"grab\"}\n");
    }

    /// <summary>Called by the Release UI Button.</summary>
    public void OnReleasePressed()
    {
        Debug.Log("[Gripper] Release pressed");
        SendRaw("{\"gripper\":\"release\"}\n");
    }

    /// <summary>Sends a terminate command to the Python server.</summary>
    public void TerminatePythonServer()
    {
        Debug.Log("[TCP] Sending terminate_server command");
        SendRaw("{\"terminate_server\": true}\n");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Position — polar/spherical mapping
    // ══════════════════════════════════════════════════════════════════════

    private void SendPosition()
    {
        // ── 1. Direction from polar origin to this object ──────────────
        Vector3 origin = polarOrigin != null ? polarOrigin.position : Vector3.zero;
        Vector3 dir    = transform.position - origin;

        float reach = dir.magnitude;

        // Azimuth: horizontal angle around Y axis (in degrees)
        float azDeg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        // Elevation: vertical angle above the horizontal plane (in degrees)
        float horizontalDist = new Vector2(dir.x, dir.z).magnitude;
        float elDeg          = Mathf.Atan2(dir.y, horizontalDist) * Mathf.Rad2Deg;

        // ── 2. Normalise angles and reach to [0, 1] ────────────────────
        float azNorm = Mathf.InverseLerp(-azimuthRangeDeg   * 0.5f,  azimuthRangeDeg   * 0.5f, azDeg);
        float elNorm = Mathf.InverseLerp(-elevationRangeDeg * 0.5f,  elevationRangeDeg * 0.5f, elDeg);
        float rNorm  = Mathf.InverseLerp(0f, unityMaxReach, reach);

        if (flipAzimuth)   azNorm = 1f - azNorm;
        if (flipElevation) elNorm = 1f - elNorm;

        // ── 3. Scale back to physical angle ranges ─────────────────────
        float mmReach = Mathf.Lerp(reachMinMM, reachMaxMM, Mathf.Clamp01(rNorm));
        float azRad   = Mathf.Lerp(-azimuthRangeDeg   * 0.5f, azimuthRangeDeg   * 0.5f, azNorm) * Mathf.Deg2Rad;
        float elRad   = Mathf.Lerp(-elevationRangeDeg * 0.5f, elevationRangeDeg * 0.5f, elNorm) * Mathf.Deg2Rad;

        // ── 4. Convert polar → Cartesian mm for xArm protocol ──────────
        //   X = left/right,  Y = forward (reach projected flat),  Z = height
        float mmX = mmReach * Mathf.Cos(elRad) * Mathf.Sin(azRad);
        float mmY = mmReach * Mathf.Cos(elRad) * Mathf.Cos(azRad);
        float mmZ = mmReach * Mathf.Sin(elRad) + zOffsetMM;

        // ── 5. Update debug labels ─────────────────────────────────────
        if (azimuthLbl   != null) azimuthLbl  .text = $"az:    {azDeg:F1}°";
        if (elevationLbl != null) elevationLbl.text = $"el:    {elDeg:F1}°";
        if (reachLbl     != null) reachLbl    .text = $"reach: {reach:F3} u";
        if (xArmXLbl     != null) xArmXLbl    .text = $"x: {mmX:F1} mm";
        if (xArmYLbl     != null) xArmYLbl    .text = $"y: {mmY:F1} mm";
        if (xArmZLbl     != null) xArmZLbl    .text = $"z: {mmZ:F1} mm";

        if (!IsFinite(mmX) || !IsFinite(mmY) || !IsFinite(mmZ))
        {
            Debug.LogWarning($"[TCP] Skipping send — non-finite: x={mmX} y={mmY} z={mmZ}");
            return;
        }

        string sx = mmX.ToString("F2", CultureInfo.InvariantCulture);
        string sy = mmY.ToString("F2", CultureInfo.InvariantCulture);
        string sz = mmZ.ToString("F2", CultureInfo.InvariantCulture);

        SendRaw("{\"x\":" + sx + ",\"y\":" + sy + ",\"z\":" + sz + "}\n");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Connection
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Call this (e.g. from a UI Button) to clear the rejected state and retry
    /// after the server has been restarted or the previous client slot has freed up.
    /// </summary>
    public void ResetAndReconnect()
    {
        Debug.Log("[TCP] Manual reconnect requested — clearing rejected flag");
        _rejected = false;
        CloseSocket();
        TryConnect();
    }

    private void TryConnect()
    {
        if (_connecting || _rejected) return;
        _connecting = true;
        StartCoroutine(ConnectCoroutine());
    }

    private IEnumerator ConnectCoroutine()
    {
        // Always start fresh — a stale _rejected from a previous Play session
        // would otherwise block all connection attempts silently.
        _rejected = false;

        while (true)
        {
            SetStatus(ConnState.Connecting, $"Connecting to {serverIP}:{serverPort}…");

            // Kick off async connect without blocking the main thread
            _client = new TcpClient();
            IAsyncResult ar = null;
            bool connectThrew = false;
            try { ar = _client.BeginConnect(serverIP, serverPort, null, null); }
            catch (Exception ex)
            {
                Debug.LogWarning("[TCP] BeginConnect error: " + ex.Message);
                connectThrew = true;
            }

            bool ok = false;

            if (!connectThrew)
            {
                // Poll every frame so we never block the main thread
                float elapsed = 0f;
                while (elapsed < 5f && !ar.IsCompleted)
                {
                    elapsed += Time.deltaTime;
                    yield return null;
                }

                if (!ar.IsCompleted)
                {
                    // Genuine timeout — server unreachable
                    Debug.LogWarning("[TCP] Connect timed out after 5s");
                }
                else
                {
                    // Always call EndConnect — it throws SocketException if refused
                    try
                    {
                        _client.EndConnect(ar);
                        _stream = _client.GetStream();
                        ok = true;
                    }
                    catch (SocketException sex)
                    {
                        Debug.LogWarning($"[TCP] Connection refused ({sex.SocketErrorCode}): {sex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[TCP] EndConnect error: {ex.Message}");
                    }
                }
            }

            if (!ok)
            {
                try { _client?.Close(); } catch { /* ignore */ }
                _client = null;
                SetStatus(ConnState.Disconnected, $"Unreachable — retry in {reconnectDelay}s");
                yield return new WaitForSeconds(reconnectDelay);
                continue;
            }

            yield return StartCoroutine(ReadWelcomeCoroutine());

            if (_rejected)                                       break;
            if (IsConnected() && _connState == ConnState.Online) break;

            CloseSocket();
            yield return new WaitForSeconds(reconnectDelay);
        }

        _connecting = false;
    }

    private IEnumerator ReadWelcomeCoroutine()
    {
        float timeout = 4f;
        float elapsed = 0f;
        _recvBuf.Clear();

        while (elapsed < timeout)
        {
            if (_stream != null && _stream.DataAvailable)
            {
                byte[] tmp = new byte[1024];
                int n = 0;
                try { n = _stream.Read(tmp, 0, tmp.Length); }
                catch (Exception ex)
                {
                    Debug.LogWarning("[TCP] Welcome read error: " + ex.Message);
                    SetStatus(ConnState.Disconnected, "Welcome read failed");
                    yield break;
                }
                _recvBuf.Append(Encoding.UTF8.GetString(tmp, 0, n));
            }

            string raw = _recvBuf.ToString();
            int    nl  = raw.IndexOf('\n');
            if (nl >= 0)
            {
                string frame = raw.Substring(0, nl).Trim();
                _recvBuf.Remove(0, nl + 1);
                HandleWelcomeFrame(frame);
                yield break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }

        Debug.LogWarning("[TCP] No welcome frame within timeout");
        SetStatus(ConnState.Disconnected, "No welcome — server may be busy");
    }

    private void HandleWelcomeFrame(string frame)
    {
        if (string.IsNullOrEmpty(frame))
        {
            Debug.LogWarning("[TCP] Empty welcome frame — will retry");
            SetStatus(ConnState.Disconnected, "Empty welcome");
            return;
        }
        try
        {
            WelcomePayload p = JsonUtility.FromJson<WelcomePayload>(frame);
            if (p.connected)
            {
                SetStatus(ConnState.Online, p.message);
                SetGripperButtonsInteractable(true);
            }
            else
            {
                // Server explicitly rejected (slot taken). Don't set _rejected=true
                // here — just log and let the coroutine retry after a delay so that
                // once the server frees the slot we reconnect automatically.
                Debug.LogWarning($"[TCP] Server rejected connection: {p.reason} — {p.message}. Retrying…");
                SetStatus(ConnState.Disconnected, $"Rejected: {p.reason} — retrying");
                CloseSocket();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TCP] Bad welcome JSON: " + ex.Message);
            SetStatus(ConnState.Disconnected, "Bad welcome payload");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Send / Receive
    // ══════════════════════════════════════════════════════════════════════

    private void SendRaw(string jsonWithNewline)
    {
        if (!IsConnected())
        {
            Debug.LogWarning("[TCP] SendRaw: not connected");
            return;
        }
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(jsonWithNewline);
            _stream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TCP] Send failed: " + ex.Message);
            CloseSocket();
        }
    }

    private void DrainIncoming()
    {
        if (_stream == null || !_stream.DataAvailable) return;
        try
        {
            byte[] tmp = new byte[2048];
            int n = _stream.Read(tmp, 0, tmp.Length);
            _recvBuf.Append(Encoding.UTF8.GetString(tmp, 0, n));

            string s  = _recvBuf.ToString();
            int    nl = s.IndexOf('\n');
            while (nl >= 0)
            {
                string frame = s.Substring(0, nl).Trim();
                s  = s.Substring(nl + 1);
                nl = s.IndexOf('\n');
                if (!string.IsNullOrEmpty(frame))
                    Debug.Log("[TCP] Server: " + frame);
            }
            _recvBuf.Clear();
            _recvBuf.Append(s);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TCP] Drain error: " + ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    private bool IsConnected()
    {
        try { return _client != null && _client.Connected && _stream != null; }
        catch { return false; }
    }

    private void CloseSocket()
    {
        SetGripperButtonsInteractable(false);
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
    }

    private void SetGripperButtonsInteractable(bool on)
    {
        if (grabButton    != null) grabButton   .interactable = on;
        if (releaseButton != null) releaseButton.interactable = on;
    }

    private void SetStatus(ConnState state, string msg)
    {
        _connState = state;
        string label = state switch
        {
            ConnState.Online     => "Online",
            ConnState.Connecting => "Connecting",
            ConnState.Rejected   => "Rejected",
            _                    => "Offline"
        };
        Debug.Log("[ArmStatus] " + label + " — " + msg);
        if (statusLabel != null) statusLabel.text = label;
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    void OnApplicationQuit() => CloseSocket();

    // ══════════════════════════════════════════════════════════════════════
    // Serialisable types
    // ══════════════════════════════════════════════════════════════════════

    [Serializable]
    private class WelcomePayload
    {
        public bool   connected;
        public string message = "";
        public string reason  = "";
        public string arm_ip  = "";
    }
}