using UnityEngine;

public class AppQuit : MonoBehaviour
{
    /// <summary>
    /// Call this from a UI Button's OnClick() event to exit the application.
    /// Works in a built player; in the Editor it stops Play Mode instead.
    /// </summary>
    public void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}