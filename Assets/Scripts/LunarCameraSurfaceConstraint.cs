using UnityEngine;

/// <summary>
/// Prevents the free camera from entering the lunar terrain.
/// The constraint is evaluated in the scientific lunar body-fixed frame and
/// uses LOLA elevation, not the rendered mesh or Unity lighting/colliders.
///
/// Attach this component to the Main Camera. LateUpdate runs after ordinary
/// Update-based camera movement scripts so it can correct their final position.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class LunarCameraSurfaceConstraint : MonoBehaviour
{
    [Header("Scientific terrain references")]
    [SerializeField] private LunarDEMReader demReader;
    [SerializeField] private LunarReferenceFrame referenceFrame;

    [Header("Surface clearance")]
    [Tooltip("Minimum radial clearance above the LOLA surface, in metres. " +
             "500 m is a practical starting value for the project's compressed scene scale and distant Moon origin.")]
    [Min(0f)] [SerializeField] private float minimumClearanceMeters = 500f;

    [Tooltip("Skip repeated DEM queries when the camera has not moved relative to the Moon.")]
    [Min(0f)] [SerializeField] private float localMovementThreshold = 0.0000001f;

    [SerializeField] private bool logFirstCorrection = true;

    private bool _hasLastLocalPosition;
    private Vector3 _lastLocalPosition;
    private bool _hasLoggedCorrection;
    private bool _hasLoggedError;

    private void Awake()
    {
        ResolveReferences();
    }

    private void LateUpdate()
    {
        if (!enabled || !ResolveReferences())
            return;

        Vector3 moonLocal = referenceFrame.WorldToMoonLocal(transform.position);
        if (_hasLastLocalPosition &&
            (moonLocal - _lastLocalPosition).sqrMagnitude <= localMovementThreshold * localMovementThreshold)
        {
            return;
        }

        _lastLocalPosition = moonLocal;
        _hasLastLocalPosition = true;

        LunarCartesianPosition scientific = referenceFrame.MoonLocalToScientific(moonLocal);
        double currentRadius = scientific.Magnitude;
        if (currentRadius <= 1.0)
            return;

        LunarGeographicCoordinate geographic;
        try
        {
            geographic = LunarCoordinates.ScientificCartesianToGeographic(scientific);
        }
        catch (System.Exception ex)
        {
            LogErrorOnce("Could not convert camera position to lunar coordinates: " + ex.Message);
            return;
        }

        if (!demReader.TryGetElevation(
                geographic.latitudeDeg,
                geographic.longitudeEastDeg,
                out LolaElevationSample terrain,
                out string error))
        {
            LogErrorOnce("LOLA camera-clearance query failed: " + error);
            return;
        }

        double requiredRadius = terrain.radiusMeters + minimumClearanceMeters;
        if (currentRadius >= requiredRadius)
            return;

        double scale = requiredRadius / currentRadius;
        LunarCartesianPosition correctedScientific = new LunarCartesianPosition(
            scientific.x * scale,
            scientific.y * scale,
            scientific.z * scale);

        transform.position = referenceFrame.ScientificToWorld(correctedScientific);
        _lastLocalPosition = referenceFrame.WorldToMoonLocal(transform.position);

        if (logFirstCorrection && !_hasLoggedCorrection)
        {
            Debug.Log(
                $"Lunar camera constraint moved the camera above the LOLA surface. " +
                $"Location: {geographic.latitudeDeg:F5}°, {geographic.longitudeEastDeg:F5}°E; " +
                $"terrain radius: {terrain.radiusMeters:F2} m; " +
                $"minimum clearance: {minimumClearanceMeters:F1} m.", this);
            _hasLoggedCorrection = true;
        }
    }

    private bool ResolveReferences()
    {
        if (demReader == null)
            demReader = FindFirstObjectByType<LunarDEMReader>();
        if (referenceFrame == null)
            referenceFrame = FindFirstObjectByType<LunarReferenceFrame>();

        if (demReader == null || referenceFrame == null)
        {
            LogErrorOnce("Assign LunarDEMReader and LunarReferenceFrame in the Inspector.");
            return false;
        }

        return true;
    }

    private void LogErrorOnce(string message)
    {
        if (_hasLoggedError)
            return;

        Debug.LogError("LunarCameraSurfaceConstraint: " + message, this);
        _hasLoggedError = true;
    }
}
