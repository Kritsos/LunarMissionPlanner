using UnityEngine;

/// <summary>
/// Mouse picking for the lunar surface. Before LOLA terrain is generated it can use
/// the Moon's SphereCollider as a directional fallback. Once terrain MeshColliders
/// exist, it preferentially selects the nearest hit on the generated terrain mesh,
/// even if the old spherical collider is intersected earlier along the ray.
/// </summary>
public sealed class LunarLocationPicker : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;
    [SerializeField] private Collider moonCollider;
    [SerializeField] private LunarLocationController locationController;
    [SerializeField] private LunarSitePanelsUI sitePanelsUI;
    [SerializeField] private LunarTerrainMeshBuilder terrainMeshBuilder;
    [SerializeField] private float maxRayDistance = 1000f;

    private void Awake()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>();
        if (targetCamera == null)
            targetCamera = Camera.main;

        if (locationController == null)
            locationController = FindFirstObjectByType<LunarLocationController>();
        if (sitePanelsUI == null)
            sitePanelsUI = FindFirstObjectByType<LunarSitePanelsUI>();
        if (terrainMeshBuilder == null)
            terrainMeshBuilder = FindFirstObjectByType<LunarTerrainMeshBuilder>();
        if (targetCamera == null)
            Debug.LogError("LunarLocationPicker: assign the scene camera.", this);
        if (moonCollider == null)
            Debug.LogError("LunarLocationPicker: assign the Moon's SphereCollider fallback.", this);
        if (locationController == null)
            Debug.LogError("LunarLocationPicker: assign a LunarLocationController.", this);
    }

    private void Update()
    {
        if (!Input.GetMouseButtonDown(0))
            return;
        if (targetCamera == null || locationController == null)
            return;
        if (locationController.ActiveClickSiteIndex < 0)
            return;
        if (sitePanelsUI != null && sitePanelsUI.IsPointerOverPanel())
            return;
        Ray ray = targetCamera.ScreenPointToRay(Input.mousePosition);
        if (!TryFindLunarSurfaceHit(ray, out RaycastHit hit))
            return;

        if (!locationController.TryPlaceFromWorldPosition(locationController.ActiveClickSiteIndex,
                hit.point, out LunarLocationResult result, out string error))
        {
            Debug.LogWarning("Lunar surface selection failed: " + error, this);
        }
    }

    private bool TryFindLunarSurfaceHit(Ray ray, out RaycastHit selectedHit)
    {
        selectedHit = default;
        RaycastHit[] hits = Physics.RaycastAll(
            ray, maxRayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        bool terrainAvailable = terrainMeshBuilder != null && terrainMeshBuilder.HasGeneratedTerrain;
        bool foundPreferred = false;
        float nearestDistance = float.PositiveInfinity;

        // If generated terrain exists, prefer its actual mesh surface over the old sphere.
        // RaycastAll is used because the spherical fallback can otherwise mask terrain
        // valleys or elevated terrain depending on the local radial height.
        foreach (RaycastHit candidate in hits)
        {
            bool accepted = terrainAvailable
                ? terrainMeshBuilder.IsGeneratedTerrainCollider(candidate.collider)
                : candidate.collider == moonCollider;

            if (accepted && candidate.distance < nearestDistance)
            {
                selectedHit = candidate;
                nearestDistance = candidate.distance;
                foundPreferred = true;
            }
        }

        // If terrain generation is active but the ray missed its mesh collider (for
        // example, a hole or an unrendered mesh section), retain the sphere fallback.
        if (!foundPreferred && terrainAvailable)
        {
            foreach (RaycastHit candidate in hits)
            {
                if (candidate.collider == moonCollider && candidate.distance < nearestDistance)
                {
                    selectedHit = candidate;
                    nearestDistance = candidate.distance;
                    foundPreferred = true;
                }
            }
        }

        return foundPreferred;
    }
}
