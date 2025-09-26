using UnityEngine;
using System;
using System.Net.Sockets;
using System.Text;

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

    void Start()
    {
        try
        {
            client = new TcpClient(serverIP, serverPort);
            stream = client.GetStream();
            Debug.Log("Connected to arm controller server.");
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to connect to server: " + e.Message);
        }
    }

    void Update()
    {
        if (client == null || stream == null || !client.Connected) return;

        timer += Time.deltaTime;
        if (timer >= updateInterval)
        {
            timer = 0f;

            Vector3 unityPos = transform.position;

            // Map to mm
            Vector3 mmPos = new Vector3(
                Remap(unityPos.x, xMinUnity, xMaxUnity, xMinMM, xMaxMM, flipX),
                Remap(unityPos.y, yMinUnity, yMaxUnity, yMinMM, yMaxMM, flipY),
                Remap(unityPos.z, zMinUnity, zMaxUnity, zMinMM, zMaxMM, flipZ)
            );

            // Create JSON
            string json = string.Format("{{\"x\":{0},\"y\":{1},\"z\":{2}}}", mmPos.x, mmPos.y, mmPos.z);




            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                stream.Write(data, 0, data.Length);
                Debug.Log("mmPos: " + mmPos);
                Debug.Log("Sent to server: " + json);
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to send data: " + e.Message);
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
