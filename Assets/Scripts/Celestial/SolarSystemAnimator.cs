using UnityEngine;

/// <summary>
/// Applies cached JPL states to the scene for display. Scientific states remain in
/// double-precision km and km/s; only the final visual positions are scaled to Unity.
/// </summary>
[DisallowMultipleComponent]
public sealed class SolarSystemAnimator : MonoBehaviour
{
    private const double KilometersPerAu = 149597870.7;

    [Header("Simulation")]
    [SerializeField] private SimulationTime simulationTime;

    [Header("Scene bodies")]
    [SerializeField] private Transform sunRoot;
    [SerializeField] private Transform earthRoot;
    [Tooltip("Earth GLB root. Its local +Y is north, +X east, and -Z is the Greenwich prime meridian.")]
    [SerializeField] private Transform earthModelFrameAlignment;
    [Tooltip("Outer Moon transform. The model-frame alignment and existing Moon root are children of this transform.")]
    [SerializeField] private Transform moonOrbitRoot;
    [Tooltip("Parent of the existing Moon root. It implements the fixed inverse GLB reflection for rendering.")]
    [SerializeField] private Transform moonModelFrameAlignment;
    [SerializeField] private Light sunLight;

    [Header("Visualization only")]
    [Tooltip("Earth's average heliocentric distance in Unity units. The Sun remains at the origin.")]
    [Min(1f)] [SerializeField] private double earthOrbitRadiusUnits = 150.0;
    [Tooltip("The current project uses approximately 12,742 km per Unity unit for the Earth-Moon separation.")]
    [Min(1f)] [SerializeField] private double earthMoonKilometersPerUnityUnit = 12742.0;
    [Min(0f)] [SerializeField] private double moonOrbitVisualMultiplier = 1.0;
    [Tooltip("Fixed display rotation from J2000 axes to Unity world axes. This never changes scientific data.")]
    [SerializeField] private Quaternion j2000ToUnityRotation = Quaternion.identity;

    private DoubleQuaternion _j2000ToUnity;
    private Vector3 _fixedSunPosition;

    private void Awake()
    {
        if (simulationTime == null)
            simulationTime = FindFirstObjectByType<SimulationTime>();
        if (sunRoot == null)
            sunRoot = FindByName("Sun");
        if (earthRoot == null)
            earthRoot = FindByName("Earth");
        if (earthModelFrameAlignment == null && earthRoot != null && earthRoot.childCount > 0)
            earthModelFrameAlignment = earthRoot.GetChild(0);
        if (sunLight == null)
        {
            Transform lightTransform = FindByName("Sunlight");
            if (lightTransform != null)
                sunLight = lightTransform.GetComponent<Light>();
        }

        _j2000ToUnity = DoubleQuaternion.FromUnity(j2000ToUnityRotation);
        j2000ToUnityRotation = _j2000ToUnity.ToUnityQuaternion();
        if (sunRoot != null)
            _fixedSunPosition = sunRoot.position;

        ApplyModelFrameAlignment();
        ValidateReferences();
    }

    private void LateUpdate()
    {
        if (simulationTime == null || !simulationTime.TryGetCurrentState(out EphemerisState state))
            return;
        if (sunRoot == null || earthRoot == null || moonOrbitRoot == null)
            return;

        // The cache positions are from the Sun center in J2000. The overview
        // deliberately compresses Sun-Earth distance while retaining the local
        // Earth-Moon separation so the Moon's motion remains legible.
        double unitsPerKmForHeliocentricView = earthOrbitRadiusUnits / KilometersPerAu;
        DoubleVector3 earthVisual = _j2000ToUnity.Rotate(state.earthHeliocentricPositionKm) * unitsPerKmForHeliocentricView;
        DoubleVector3 moonRelativeEarthKm = state.moonHeliocentricPositionKm - state.earthHeliocentricPositionKm;
        DoubleVector3 moonVisualOffset = _j2000ToUnity.Rotate(moonRelativeEarthKm) *
                                         (moonOrbitVisualMultiplier / earthMoonKilometersPerUnityUnit);

        sunRoot.position = _fixedSunPosition;
        earthRoot.position = _fixedSunPosition + earthVisual.ToUnityVector3();
        moonOrbitRoot.position = _fixedSunPosition + earthVisual.ToUnityVector3() + moonVisualOffset.ToUnityVector3();

        // Earth orientation is evaluated from absolute ET so pause, seeking,
        // and high playback rates all produce the same physical pose.
        earthRoot.rotation = j2000ToUnityRotation * EarthRotationModel.BodyToJ2000(state.etSeconds);

        // SPICE supplies body-fixed ME -> J2000. The fixed J2000-to-Unity mapping
        // is composed here; the GLB reflection is handled by the child pivot.
        moonOrbitRoot.rotation = j2000ToUnityRotation * state.moonBodyToJ2000.ToUnityQuaternion();

        if (sunLight != null)
        {
            Vector3 rayDirection = moonOrbitRoot.position - sunRoot.position;
            if (rayDirection.sqrMagnitude > 1e-12f)
                sunLight.transform.rotation = Quaternion.LookRotation(rayDirection.normalized, Vector3.up);
        }
    }

    private void ApplyModelFrameAlignment()
    {
        if (earthModelFrameAlignment != null)
        {
            // Model-local +X is east, +Y is north, and -Z points through
            // Greenwich. The cyclic rotation maps +X -> IAU +Y (east),
            // +Y -> IAU +Z (north), and +Z -> IAU +X. Reflecting model-local
            // Z first therefore maps -Z -> IAU +X (prime meridian). The full
            // model-to-IAU map is improper, so it needs this reflection in
            // addition to the quaternion. Keep both fixed transforms
            // separate from the time-varying Earth attitude.
            earthModelFrameAlignment.localPosition = Vector3.zero;
            earthModelFrameAlignment.localRotation = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
            earthModelFrameAlignment.localScale = new Vector3(1f, 1f, -1f);
        }

        if (moonModelFrameAlignment == null)
            return;

        // The handover mapping is m = M*b, so model-local -> body-fixed is M^-1.
        // M^-1 = Rx(+90 deg) * diag(-1, 1, 1). Keep this improper reflection
        // separate from the proper SPICE quaternion rotation.
        moonModelFrameAlignment.localPosition = Vector3.zero;
        moonModelFrameAlignment.localRotation = Quaternion.Euler(90f, 0f, 0f);
        moonModelFrameAlignment.localScale = new Vector3(-1f, 1f, 1f);
    }

    private void ValidateReferences()
    {
        if (simulationTime == null)
            Debug.LogError($"[{nameof(SolarSystemAnimator)}] SimulationTime is not assigned.", this);
        if (sunRoot == null || earthRoot == null || moonOrbitRoot == null)
            Debug.LogError($"[{nameof(SolarSystemAnimator)}] Assign Sun, Earth, and Moon orbit transforms.", this);
        if (earthModelFrameAlignment == null)
            Debug.LogError(
                $"[{nameof(SolarSystemAnimator)}] The Earth model-frame alignment transform is not assigned. " +
                "The globe will not align its north pole and Greenwich meridian with the Earth orientation model.", this);
        if (moonModelFrameAlignment == null)
            Debug.LogError(
                $"[{nameof(SolarSystemAnimator)}] The Moon model-frame alignment pivot is not assigned. " +
                "Lunar body orientation will not preserve the existing GLB frame mapping.", this);
    }

    private static Transform FindByName(string objectName)
    {
        GameObject found = GameObject.Find(objectName);
        return found != null ? found.transform : null;
    }
}
