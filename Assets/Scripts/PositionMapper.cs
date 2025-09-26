using UnityEngine;

public class PositionMapper : MonoBehaviour
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

    // Update interval (seconds)
    public float updateInterval = 1f;
    private float timer = 0f;

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= updateInterval)
        {
            timer = 0f; // reset timer

            Vector3 unityPos = transform.position;

            // Map to mm
            Vector3 mmPos = new Vector3(
                Remap(unityPos.x, xMinUnity, xMaxUnity, xMinMM, xMaxMM, flipX),
                Remap(unityPos.z, yMinUnity, yMaxUnity, yMinMM, yMaxMM, flipY),
                Remap(unityPos.y, zMinUnity, zMaxUnity, zMinMM, zMaxMM, flipZ)
            );

            Debug.Log("MM Position: " + mmPos);
        }
    }

    // Remap with optional flip
    private float Remap(float value, float fromMin, float fromMax, float toMin, float toMax, bool flip)
    {
        float t = Mathf.InverseLerp(fromMin, fromMax, value); // 0–1 normalized
        if (flip) t = 1f - t; // invert axis if needed
        return Mathf.Lerp(toMin, toMax, t);
    }
}
