using UnityEngine;
using UnityEngine.UI;
using System;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.Collections;

/// <summary>
/// Streams position to the xArm server and exposes Grab / Release
/// methods you can wire to UI Buttons in the Inspector.
/// </summary>
public class PositionMapperWithTCPStatus : MonoBehaviour
{
    // ── Unity coordinate ranges ────────────────────────────────────────────
    private float xMinUnity = -0.2f, xMaxUnity = 0.2f;
    private float yMinUnity = -0.2f, yMaxUnity = 0.2f;
    private float zMinUnity = -0.2f, zMaxUnity = 0.2f;

    // ── Robot coordinate ranges (mm) ──────────────────────────────────────
    public float xMinMM = -250f, xMaxMM = 250f;
    public float yMinMM =  150f, yMaxMM = 250f;
    public float zMinMM =   20f, zMaxMM = 550f;

    public bool flipX = false;
    public bool flipY = false;
    public bool flipZ = false;

    // ── TCP ───────────────────────────────────────────────────────────────
    public string serverIP   = "192.168.1.188";
    public int    serverPort = 5005;

    // ── UI ────────────────────────────────────────────────────────────────
    [Header("Status")]
    public Text statusLabel;        // drag any UI Text here for live status

    [Header("Gripper Buttons")]
    [Tooltip("Drag your Grab UI Button here")]
    public Button grabButton;

    [Tooltip("Drag your Release UI Button here")]
    public Button releaseButton;

    // ── Timing ────────────────────────────────────────────────────────────
    [Header("Timing")]
    public float sendInterval   = 0.5f;
    public float reconnectDelay = 3f;

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

        // Wire buttons — safe even if not assigned in Inspector
        if (grabButton   != null) grabButton  .onClick.AddListener(OnGrabPressed);
        if (releaseButton != null) releaseButton.onClick.AddListener(OnReleasePressed);

        // Buttons disabled until we're online
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
    // Gripper public API — called by UI Button onClick events
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

    // ══════════════════════════════════════════════════════════════════════
    // Connection
    // ══════════════════════════════════════════════════════════════════════

    private void TryConnect()
    {
        if (_connecting || _rejected) return;
        _connecting = true;
        StartCoroutine(ConnectCoroutine());
    }

    private IEnumerator ConnectCoroutine()
    {
        while (true)
        {
            SetStatus(ConnState.Connecting, $"Connecting to {serverIP}:{serverPort}…");

            bool ok = false;
            try
            {
                _client = new TcpClient();
                IAsyncResult ar = _client.BeginConnect(serverIP, serverPort, null, null);
                if (ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)) && _client.Connected)
                {
                    _client.EndConnect(ar);
                    _stream = _client.GetStream();
                    ok = true;
                }
                else
                {
                    Debug.LogWarning("[TCP] Connect timed out");
                    _client.Close();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TCP] Connect error: " + ex.Message);
                _client?.Close();
            }

            if (!ok)
            {
                SetStatus(ConnState.Disconnected, $"Unreachable — retry in {reconnectDelay}s");
                yield return new WaitForSeconds(reconnectDelay);
                continue;
            }

            yield return StartCoroutine(ReadWelcomeCoroutine());

            if (_rejected)                                           break;
            if (IsConnected() && _connState == ConnState.Online)     break;

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
        if (string.IsNullOrEmpty(frame)) { SetStatus(ConnState.Disconnected, "Empty welcome"); return; }
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
                _rejected = true;
                SetStatus(ConnState.Rejected, $"{p.reason}: {p.message}");
                SetGripperButtonsInteractable(false);
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
    // Send
    // ══════════════════════════════════════════════════════════════════════

    private void SendPosition()
    {
        Vector3 u = transform.position;

        float mmX = Remap(u.x, xMinUnity, xMaxUnity, xMinMM, xMaxMM, flipX);
        float mmY = Remap(u.z, yMinUnity, yMaxUnity, yMinMM, yMaxMM, flipY);
        float mmZ = Remap(u.y, zMinUnity, zMaxUnity, zMinMM, zMaxMM, flipZ);

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

    /// <summary>Send a pre-built newline-terminated JSON string.</summary>
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

    // ══════════════════════════════════════════════════════════════════════
    // Receive
    // ══════════════════════════════════════════════════════════════════════

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
            ConnState.Online     => "🟢 Online  |  " + msg,
            ConnState.Connecting => "🟡 " + msg,
            ConnState.Rejected   => "🔴 Rejected  |  " + msg,
            _                    => "⚫ " + msg
        };
        Debug.Log("[ArmStatus] " + label);
        if (statusLabel != null) statusLabel.text = label;
    }

    private static float Remap(float v,
                                float fMin, float fMax,
                                float tMin, float tMax, bool flip)
    {
        float t = Mathf.InverseLerp(fMin, fMax, v);
        if (flip) t = 1f - t;
        return Mathf.Lerp(tMin, tMax, t);
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    void OnApplicationQuit() => CloseSocket();

    [Serializable]
    private class WelcomePayload
    {
        public bool   connected;
        public string message = "";
        public string reason  = "";
        public string arm_ip  = "";
    }
}