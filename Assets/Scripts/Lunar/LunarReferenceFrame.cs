using System;
using UnityEngine;

/// <summary>
/// Bridges the right-handed scientific LOLA ME/PA Cartesian frame and the empirically
/// validated local axes of the imported Moon GLB. The GLB mapping is a reflection
/// (determinant -1), not a quaternion rotation.
///
/// Scientific +X (prime meridian) -> model local -X
/// Scientific +Y (90 deg east)   -> model local -Z
/// Scientific +Z (north)         -> model local +Y
/// </summary>
public sealed class LunarReferenceFrame : MonoBehaviour
{
    [Header("Moon transform")]
    [SerializeField] private Transform moonRoot;

    [Header("Scale calibration")]
    [SerializeField] private double referenceRadiusMeters = 1737400.0;

    [Tooltip("Radius of the visual GLB in Moon-root LOCAL units, before the Moon root's Transform scale. " +
             "0.97118 is the current estimate from the project summary; verify against the renderer bounds.")]
    [SerializeField] private double modelReferenceRadiusMoonLocalUnits = 0.97118;

    [Header("Diagnostics")]
    [SerializeField] private bool warnIfMoonRootScaleIsNotUniform = true;

    public Transform MoonRoot => moonRoot != null ? moonRoot : transform;
    public double ReferenceRadiusMeters => referenceRadiusMeters;
    public double ModelReferenceRadiusMoonLocalUnits => modelReferenceRadiusMoonLocalUnits;
    public double MoonLocalUnitsPerMeter => modelReferenceRadiusMoonLocalUnits / referenceRadiusMeters;

    private void Awake()
    {
        if (moonRoot == null)
            moonRoot = transform;
        ValidateCalibration();
    }

    private void OnValidate()
    {
        if (moonRoot == null)
            moonRoot = transform;
        if (referenceRadiusMeters <= 0.0)
            referenceRadiusMeters = 1737400.0;
        if (modelReferenceRadiusMoonLocalUnits <= 0.0)
            modelReferenceRadiusMoonLocalUnits = 0.97118;
    }

    /// <summary>Scientific ME-frame Cartesian metres -> Moon-root local Unity coordinates.</summary>
    public Vector3 ScientificToMoonLocal(LunarCartesianPosition scientificMeters)
    {
        double scale = MoonLocalUnitsPerMeter;
        return new Vector3(
            (float)(-scientificMeters.x * scale),
            (float)( scientificMeters.z * scale),
            (float)(-scientificMeters.y * scale));
    }

    /// <summary>Moon-root local Unity coordinates -> scientific ME-frame Cartesian metres.</summary>
    public LunarCartesianPosition MoonLocalToScientific(Vector3 modelLocal)
    {
        double inverseScale = 1.0 / MoonLocalUnitsPerMeter;
        return new LunarCartesianPosition(
            -modelLocal.x * inverseScale,
            -modelLocal.z * inverseScale,
             modelLocal.y * inverseScale);
    }

    public Vector3 MoonLocalToWorld(Vector3 modelLocal) => MoonRoot.TransformPoint(modelLocal);
    public Vector3 WorldToMoonLocal(Vector3 worldPosition) => MoonRoot.InverseTransformPoint(worldPosition);

    public Vector3 ScientificToWorld(LunarCartesianPosition scientificMeters)
        => MoonLocalToWorld(ScientificToMoonLocal(scientificMeters));

    public LunarCartesianPosition WorldToScientific(Vector3 worldPosition)
        => MoonLocalToScientific(WorldToMoonLocal(worldPosition));

    [ContextMenu("Log Lunar Reference Frame Calibration")]
    public void LogCalibration()
    {
        Debug.Log(
            $"Lunar reference frame: Rref={referenceRadiusMeters:F3} m, " +
            $"model radius={modelReferenceRadiusMoonLocalUnits:F8} Moon-local units, " +
            $"scale={MoonLocalUnitsPerMeter:G12} Moon-local units/m. " +
            $"Moon root lossy scale={MoonRoot.lossyScale}.", this);
    }

    [ContextMenu("Run Coordinate Round-Trip Tests")]
    public void RunCoordinateRoundTripTests()
    {
        // Includes the five project landmarks and longitude-seam cases.
        double[,] tests =
        {
            { -15.19, 34.60 },
            { 8.12, -38.01 },
            { 9.62, -20.08 },
            { -43.30, -11.22 },
            { 23.73, -47.49 },
            { 0.0, 0.0 },
            { 12.5, 359.99 },
            { -12.5, 0.01 },
            { 45.0, 179.99 },
            { -45.0, -179.99 }
        };

        const double latitudeToleranceDeg = 0.0001;
        const double longitudeToleranceDeg = 0.0001;
        int passed = 0;

        for (int i = 0; i < tests.GetLength(0); i++)
        {
            double latitude = tests[i, 0];
            double longitude = tests[i, 1];
            LunarCartesianPosition scientific = LunarCoordinates.GeographicToScientificCartesian(
                latitude, longitude, referenceRadiusMeters);
            Vector3 modelLocal = ScientificToMoonLocal(scientific);
            LunarCartesianPosition recoveredScientific = MoonLocalToScientific(modelLocal);
            LunarGeographicCoordinate recovered =
                LunarCoordinates.ScientificCartesianToGeographic(recoveredScientific);

            double latitudeError = Math.Abs(recovered.latitudeDeg - latitude);
            double longitudeError = Math.Abs(ShortestLongitudeDifference(
                recovered.longitudeEastDeg,
                LunarCoordinates.NormalizeLongitudeEast(longitude)));
            bool pass = latitudeError <= latitudeToleranceDeg &&
                        longitudeError <= longitudeToleranceDeg;

            if (pass)
                passed++;

            Debug.Log(
                $"[{(pass ? "PASS" : "FAIL")}] Round trip ({latitude:F5}, {longitude:F5}) -> " +
                $"({recovered.latitudeDeg:F6}, {recovered.longitudeEastDeg:F6}); " +
                $"errors lat={latitudeError:G4}°, lon={longitudeError:G4}°.", this);
        }

        Debug.Log($"Lunar coordinate round-trip tests: {passed}/{tests.GetLength(0)} passed.", this);
    }

    private static double ShortestLongitudeDifference(double a, double b)
    {
        double difference = (a - b) % 360.0;
        if (difference > 180.0) difference -= 360.0;
        if (difference < -180.0) difference += 360.0;
        return difference;
    }

    private void ValidateCalibration()
    {
        if (moonRoot == null)
            moonRoot = transform;
        if (referenceRadiusMeters <= 0.0 || modelReferenceRadiusMoonLocalUnits <= 0.0)
            Debug.LogError("Lunar reference-frame radii must be positive.", this);

        if (warnIfMoonRootScaleIsNotUniform)
        {
            Vector3 scale = MoonRoot.lossyScale;
            // A reflected model-frame parent intentionally gives one axis a
            // negative sign. Compare magnitudes so that reflection is not
            // mistaken for non-uniform scale.
            float absX = Mathf.Abs(scale.x);
            float absY = Mathf.Abs(scale.y);
            float absZ = Mathf.Abs(scale.z);
            float max = Mathf.Max(absX, Mathf.Max(absY, absZ));
            float min = Mathf.Min(absX, Mathf.Min(absY, absZ));
            if (max - min > 1e-5f)
                Debug.LogWarning(
                    "Moon root has non-uniform world scale. Geographic conversion still works, " +
                    "but the rendered Moon will be distorted. Use a uniform scale for a spherical body.", this);
        }
    }
}
