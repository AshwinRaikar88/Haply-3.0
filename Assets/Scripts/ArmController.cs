using UnityEngine;
using UnityEngine.UI;
using System;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.Collections;
using System.Threading;
using Haply.Inverse.DeviceControllers;
using Haply.Inverse.DeviceData;

/// <summary>
/// Streams position to the xArm server and exposes Grab / Release
/// methods you can wire to UI Buttons in the Inspector.
/// </summary>
public class ArmController : MonoBehaviour
{
    public Inverse3Controller inverse3;
    public VerseGripController verseGrip;

    [Header("Cursor Settings")]
    [Range(0, 1)]
    public float speed = 0.5f;

    [Range(0, 0.5f)]
    public float movementLimitRadius = 0.075f;

    private Vector3 _targetPosition;
	[Tooltip("GameObject nested under the haptic origin — its localPosition is used as the home cursor position")]
	public Transform homePositionTarget;
	private Vector3 HomeLocalPosition =>
    	homePositionTarget != null
        	? homePositionTarget.localPosition
        	: Vector3.zero;

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
    public TMPro.TextMeshProUGUI statusLabel;

    [Header("Gripper Buttons")]
    [Tooltip("Drag your Grab UI Button here")]
    public Button grabButton;

    [Tooltip("Drag your Release UI Button here")]
    public Button releaseButton;

    // ── Timing ────────────────────────────────────────────────────────────
    [Header("Timing")]
    public float reconnectDelay = 3f;

    // ══════════════════════════════════════════════════════════════════════
    // Thread-safe scene data cache
    // The Haply DeviceStateChanged callback fires on a background thread.
    // We write Unity transform data here on FixedUpdate (main thread),
    // then read it safely inside the callback using ReaderWriterLockSlim.
    // ══════════════════════════════════════════════════════════════════════

    private struct SceneData
    {
        public Vector3 cursorWorldPosition;
    }

    private SceneData _cachedSceneData;
    private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();

    private SceneData GetSceneData()
    {
        _cacheLock.EnterReadLock();
        try   { return _cachedSceneData; }
        finally { _cacheLock.ExitReadLock(); }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Private TCP state
    // ══════════════════════════════════════════════════════════════════════

    private TcpClient     _client;
    private NetworkStream _stream;

    private bool _connecting = false;
    private bool _rejected   = false;

    private readonly StringBuilder _recvBuf = new StringBuilder();

    public enum ConnState { Disconnected, Connecting, Online, Rejected }
    private ConnState _connState = ConnState.Disconnected;

    // ══════════════════════════════════════════════════════════════════════
    // Unity lifecycle
    // ══════════════════════════════════════════════════════════════════════

    void Start()
	{
    	//_targetPosition = homePosition;

    	SetStatus(ConnState.Disconnected, "Starting…");

   		if (grabButton    != null) grabButton.onClick.AddListener(OnGrabPressed);
    	if (releaseButton != null) releaseButton.onClick.AddListener(OnReleasePressed);

    	SetGripperButtonsInteractable(false);
    	TryConnect();

    	StartCoroutine(ApplyHomePositionNextFrame());
	}

	private IEnumerator ApplyHomePositionNextFrame()
	{
    	yield return null;
    	yield return null;
    	_targetPosition = HomeLocalPosition;
    	inverse3.SetCursorLocalPosition(_targetPosition);
    	Debug.Log($"[Cursor] Home applied: {_targetPosition} (from {homePositionTarget?.name})");
	}

    private void OnEnable()
    {
        // FIX: seed _targetPosition from the actual cursor so the arm
        // doesn't drift toward Vector3.zero on the very first GetButton() tick.
        _targetPosition = inverse3.CursorLocalPosition;

        verseGrip.DeviceStateChanged += OnDeviceStateChanged;
    }

    private void OnDisable()
    {
        verseGrip.DeviceStateChanged -= OnDeviceStateChanged;
        inverse3.Release();
    }

    void FixedUpdate()
    {
        // Write main-thread Unity data into the cache every physics tick.
        _cacheLock.EnterWriteLock();
        try
        {
            _cachedSceneData.cursorWorldPosition = transform.position;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
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
    }

    // ══════════════════════════════════════════════════════════════════════
    // Haply device callback  (background thread)
    // ══════════════════════════════════════════════════════════════════════

    private void OnDeviceStateChanged(object sender, VerseGripEventArgs args)
    {
        var grip      = args.DeviceController;
        var direction = grip.Orientation * Vector3.forward;

        // Grab the main-thread snapshot — no Unity API calls here.
        var sceneData = GetSceneData();

        if (grip.GetButtonDown())
        {
            _targetPosition = inverse3.CursorLocalPosition;
            SendPositionFromCache(sceneData);
        }

        if (grip.GetButton())
        {
            _targetPosition += direction * (0.0001f * speed);

            var workspaceCenter = inverse3.WorkspaceCenterLocalPosition;
            _targetPosition = Vector3.ClampMagnitude(
                                  _targetPosition - workspaceCenter,
                                  movementLimitRadius)
                              + workspaceCenter;
        }

        //if (grip.GetButtonUp())
        //{
        //    _targetPosition = inverse3.CursorLocalPosition;
        //    SendPositionFromCache(sceneData);
        //}

        inverse3.SetCursorLocalPosition(_targetPosition);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Public gripper methods (wired to UI Buttons)
    // ══════════════════════════════════════════════════════════════════════

    public void OnGrabPressed()
    {
        Debug.Log("[Gripper] Grab pressed");
        SendRaw("{\"gripper\":\"grab\"}\n");
    }

    public void OnReleasePressed()
    {
        Debug.Log("[Gripper] Release pressed");
        SendRaw("{\"gripper\":\"release\"}\n");
    }

    public void TerminatePythonServer()
    {
        Debug.Log("[TCP] Sending terminate_server command");
        SendRaw("{\"terminate_server\": true}\n");
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

            if (_rejected)                                        break;
            if (IsConnected() && _connState == ConnState.Online)  break;

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

    /// <summary>Thread-safe: uses the pre-fetched SceneData snapshot.</summary>
    private void SendPositionFromCache(SceneData sceneData)
    {
        Vector3 u = sceneData.cursorWorldPosition;

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
        if (grabButton    != null) grabButton.interactable    = on;
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