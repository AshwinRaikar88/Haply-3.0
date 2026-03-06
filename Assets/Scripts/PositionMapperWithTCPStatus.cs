using UnityEngine;
using UnityEngine.UI;
using System;
using System.Net.Sockets;
using System.Text;
using System.Collections;

/// <summary>
/// Streams this GameObject's position to the xArm server.
/// Guards against NaN/Infinity before every send so the server
/// never receives illegal JSON values.
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

    // ── Optional status label ─────────────────────────────────────────────
    public Text statusLabel;

    // ── Timing ────────────────────────────────────────────────────────────
    [Tooltip("Seconds between position updates sent to the server")]
    public float sendInterval   = 0.5f;

    [Tooltip("Seconds between reconnect attempts")]
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

            if (_rejected)                                    break;
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
        if (string.IsNullOrEmpty(frame)) { SetStatus(ConnState.Disconnected, "Empty welcome"); return; }
        try
        {
            WelcomePayload p = JsonUtility.FromJson<WelcomePayload>(frame);
            if (p.connected)
                SetStatus(ConnState.Online, p.message);
            else
            {
                _rejected = true;
                SetStatus(ConnState.Rejected, $"{p.reason}: {p.message}");
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

        // ── NaN / Infinity guard ───────────────────────────────────────────
        // If Unity position hasn't settled yet (e.g. first frame after spawn),
        // the remapped value can be NaN, which is illegal in JSON and will
        // cause the server to reject the frame.  Skip the send this tick.
        if (!IsFiniteFloat(mmX) || !IsFiniteFloat(mmY) || !IsFiniteFloat(mmZ))
        {
            Debug.LogWarning($"[TCP] Skipping send — non-finite value: x={mmX} y={mmY} z={mmZ}");
            return;
        }

        // Pre-format each float to avoid format-specifier ambiguity inside
        // interpolated strings with escaped braces (which caused "z":F2 bugs).
        string sx   = mmX.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string sy   = mmY.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string sz   = mmZ.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string json = "{\"x\":" + sx + ",\"y\":" + sy + ",\"z\":" + sz + "}\n";

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            _stream.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TCP] Send failed: " + ex.Message);
            CloseSocket();
        }
    }

    private static bool IsFiniteFloat(float v) =>
        !float.IsNaN(v) && !float.IsInfinity(v);

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
        try { _stream?.Close(); } catch { /* ignore */ }
        try { _client?.Close(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
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