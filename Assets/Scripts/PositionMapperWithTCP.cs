using UnityEngine;
using System;
using System.Net.Sockets;
using System.Text;
using System.Collections;

public class PositionMapperWithTCP : MonoBehaviour
{
    // Unity coordinate ranges
    private float xMinUnity = -0.2f, xMaxUnity = 0.2f;
    private float yMinUnity = -0.2f, yMaxUnity = 0.2f;
    private float zMinUnity = -0.2f, zMaxUnity = 0.2f;

    // Target ranges in millimeters
    public float xMinMM = -250f, xMaxMM = 250f;
    public float yMinMM = 150f,  yMaxMM = 250f;
    public float zMinMM = 20f,   zMaxMM = 550f;

    // Flip toggles
    public bool flipX = false;
    public bool flipY = false;
    public bool flipZ = false;

    // TCP settings
    public string serverIP = "192.168.1.188"; 
    public int serverPort = 5005;
    private TcpClient client;
    private NetworkStream stream;

    // Update interval (seconds)
    public float updateInterval = 1f;
    private float timer = 0f;

    // Reconnect settings
    public float reconnectDelay = 5f;
    private bool connecting = false;

    void Start()
    {
        StartCoroutine(ConnectToServer());
    }

    IEnumerator ConnectToServer()
    {
        connecting = true;
        while (client == null || !client.Connected)
        {
            try
            {
                client = new TcpClient();
                var result = client.BeginConnect(serverIP, serverPort, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));

                if (!success)
                {
                    Debug.LogWarning("TCP: Connection attempt timed out.");
                    client.Close();
                }
                else
                {
                    client.EndConnect(result);
                    stream = client.GetStream();
                    Debug.Log("Connected to arm controller server.");
                    break; // exit loop when connected
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("TCP: Failed to connect. Error: " + e.Message);
                if (client != null) client.Close();
            }

            // Wait before retrying
            yield return new WaitForSeconds(reconnectDelay);
        }
        connecting = false;
    }

    void Update()
    {
        // Reconnect if disconnected
        if ((client == null || stream == null || !client.Connected) && !connecting)
        {
            StartCoroutine(ConnectToServer());
            return;
        }

        timer += Time.deltaTime;
        if (timer >= updateInterval)
        {
            timer = 0f;

            Vector3 unityPos = transform.position;

            // Map to mm
            Vector3 mmPos = new Vector3(
                Remap(unityPos.x, xMinUnity, xMaxUnity, xMinMM, xMaxMM, flipX),
                Remap(unityPos.z, yMinUnity, yMaxUnity, yMinMM, yMaxMM, flipY),
                Remap(unityPos.y, zMinUnity, zMaxUnity, zMinMM, zMaxMM, flipZ)
            );

            // Create JSON
            string json = string.Format("{{\"x\":{0},\"y\":{1},\"z\":{2}}}", mmPos.x, mmPos.y, mmPos.z);

            try
            {
                if (stream != null && client.Connected)
                {
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    stream.Write(data, 0, data.Length);
                    Debug.Log("mmPos: " + mmPos);
                    Debug.Log("Sent to server: " + json);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("TCP: Failed to send data. Will retry connection. Error: " + e.Message);
                if (client != null) client.Close();
                client = null;
                stream = null;
            }
        }
    }

    private float Remap(float value, float fromMin, float fromMax, float toMin, float toMax, bool flip)
    {
        float t = Mathf.InverseLerp(fromMin, fromMax, value);
        if (flip) t = 1f - t;
        return Mathf.Lerp(toMin, toMax, t);
    }

    void OnApplicationQuit()
    {
        if (stream != null) stream.Close();
        if (client != null) client.Close();
    }
}
