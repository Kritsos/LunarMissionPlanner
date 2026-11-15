
using UnityEngine;

public class CelestialBodyScaler : MonoBehaviour
{
    [Header("Target diameter in Unity units")]
    public float targetDiameter = 1f;

    [ContextMenu("Normalize Model Scale")]
    public void NormalizeModelScale()
    {
        // Find all visible renderers belonging to this body.
        Renderer[] renderers =
            GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
        {
            Debug.LogError("No renderers found!", this);
            return;
        }

        // Calculate the combined bounding box.
        Bounds bounds = renderers[0].bounds;

        foreach (Renderer renderer in renderers)
        {
            bounds.Encapsulate(renderer.bounds);
        }

        // Find the largest dimension.
        float currentDiameter = Mathf.Max(
            bounds.size.x,
            bounds.size.y,
            bounds.size.z
        );

        if (currentDiameter <= 0f)
        {
            Debug.LogError("Invalid model dimensions!", this);
            return;
        }

        // Calculate the scale factor.
        float scaleFactor = targetDiameter / currentDiameter;

        // Apply the scale to the parent object.
        transform.localScale *= scaleFactor;

        Debug.Log(
            $"{name}: Original diameter = {currentDiameter:F4}, " +
            $"Target diameter = {targetDiameter:F4}, " +
            $"Scale factor = {scaleFactor:F6}"
        );
    }
}
