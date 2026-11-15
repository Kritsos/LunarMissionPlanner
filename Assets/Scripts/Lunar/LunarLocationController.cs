using System;
using System.Globalization;
using UnityEngine;

[Serializable]
public struct LunarLocationResult
{
    public double latitudeDeg;
    public double longitudeEastDeg;
    public double elevationMeters;
    public double radiusMeters;
    public double dnBilinear;
    public Vector3 moonLocalPosition;
    public Vector3 unityWorldPosition;
}

/// <summary>
/// The shared location pipeline for both user workflows:
/// (1) geographic coordinates -> LOLA height -> Unity marker;
/// (2) clicked Unity world point -> geographic direction -> LOLA height -> Unity marker.
/// Exposes per-site selection and marker state to the site-panel UI.
/// </summary>
public sealed class LunarLocationController : MonoBehaviour
{
    [Header("Core components")]
    [SerializeField] private LunarDEMReader demReader;
    [SerializeField] private LunarReferenceFrame referenceFrame;

    [Header("Camera focus")]
    [Tooltip("LunarCameraFocusController on the Main Camera. If left empty, the scene is searched at runtime.")]
    [SerializeField] private LunarCameraFocusController cameraFocusController;

    [Header("Marker")]
    [SerializeField] private GameObject markerPrefab;
    [Tooltip("Small radial clearance above the LOLA surface, in physical metres.")]
    [Min(0f)] [SerializeField] private float surfaceOffsetMeters = 20f;
    [Tooltip("Marker diameter in Moon-root local Unity units. Intentionally exaggerated for visibility.")]
    [Min(0.00001f)] [SerializeField] private float markerDiameterMoonLocalUnits = 0.006f;

    private sealed class SitePanelState
    {
        public string LatitudeInput;
        public string LongitudeInput;
        public string Readout = "No landing site selected.";
        public string InputError = "";
        public LunarLocationResult LastSelectedLocation;
        public bool HasSelectedLocation;
        public GameObject Marker;
    }

    private SitePanelState[] _sites;
    private int _activeClickSiteIndex;
    private int _trackedSiteIndex = -1;

    public event Action<LunarLocationResult> LocationSelected;
    public event Action<int, LunarLocationResult> LocationSlotSelected;
    public event Action<int> LocationSlotCleared;
    public string LastReadout => _sites != null ? _sites[0].Readout : "No landing site selected.";
    public LunarReferenceFrame ReferenceFrame => referenceFrame;
    public int ActiveClickSiteIndex => _activeClickSiteIndex;

    public bool TryGetSelectedLocation(out LunarLocationResult result)
    {
        return TryGetSelectedLocation(0, out result);
    }

    public bool TryGetSelectedLocation(int siteIndex, out LunarLocationResult result)
    {
        result = default;
        if (!TryGetSiteState(siteIndex, out SitePanelState site))
            return false;
        result = site.LastSelectedLocation;
        return site.HasSelectedLocation;
    }

    public bool TryGetCoordinateInputs(int siteIndex, out string latitude, out string longitude)
    {
        latitude = "";
        longitude = "";
        if (!TryGetSiteState(siteIndex, out SitePanelState site))
            return false;
        latitude = site.LatitudeInput;
        longitude = site.LongitudeInput;
        return true;
    }

    public bool SetCoordinateInputs(int siteIndex, string latitude, string longitude)
    {
        if (!TryGetSiteState(siteIndex, out SitePanelState site))
            return false;
        string nextLatitude = latitude ?? "";
        string nextLongitude = longitude ?? "";
        if (site.LatitudeInput != nextLatitude || site.LongitudeInput != nextLongitude)
            site.InputError = "";
        site.LatitudeInput = nextLatitude;
        site.LongitudeInput = nextLongitude;
        return true;
    }

    public string GetSiteInputError(int siteIndex)
        => TryGetSiteState(siteIndex, out SitePanelState site) ? site.InputError : "Invalid site index.";

    public void SetSiteInputError(int siteIndex, string error)
    {
        if (TryGetSiteState(siteIndex, out SitePanelState site))
            site.InputError = error ?? "";
    }

    public void ClearSiteInputError(int siteIndex)
    {
        if (TryGetSiteState(siteIndex, out SitePanelState site))
            site.InputError = "";
    }

    public bool TryPlaceFromCoordinateInputs(
        int siteIndex,
        out LunarLocationResult result,
        out string error)
    {
        result = default;
        error = null;
        if (!TryGetSiteState(siteIndex, out SitePanelState site))
        {
            error = "Landing-site index must be 0 (A) or 1 (B).";
            return false;
        }

        if (!TryParse(site.LatitudeInput, out double latitude) ||
            !TryParse(site.LongitudeInput, out double longitude))
        {
            error = "Enter finite numeric coordinates using a decimal point (for example, 34.60).";
            site.InputError = error;
            return false;
        }

        return TryPlaceFromCoordinates(siteIndex, latitude, longitude, out result, out error);
    }

    public bool ClearSite(int siteIndex)
    {
        if (!TryGetSiteState(siteIndex, out SitePanelState site))
            return false;

        if (_trackedSiteIndex == siteIndex)
        {
            if (cameraFocusController != null && cameraFocusController.IsTracking)
                cameraFocusController.StopTracking();
            _trackedSiteIndex = -1;
        }

        if (site.Marker != null)
        {
            Destroy(site.Marker);
            site.Marker = null;
        }

        site.LatitudeInput = "";
        site.LongitudeInput = "";
        site.Readout = "No landing site selected.";
        site.InputError = "";
        site.LastSelectedLocation = default;
        site.HasSelectedLocation = false;
        if (_activeClickSiteIndex == siteIndex)
            _activeClickSiteIndex = -1;

        LocationSlotCleared?.Invoke(siteIndex);
        return true;
    }

    private void Awake()
    {
        _sites = new[]
        {
            new SitePanelState { LatitudeInput = "-15.19", LongitudeInput = "34.60" },
            new SitePanelState { LatitudeInput = "0.00", LongitudeInput = "0.00" }
        };

        if (demReader == null)
            demReader = FindFirstObjectByType<LunarDEMReader>();
        if (referenceFrame == null)
            referenceFrame = FindFirstObjectByType<LunarReferenceFrame>();
        ResolveCameraFocusController();

        if (demReader == null)
            Debug.LogError("LunarLocationController: assign a LunarDEMReader component.", this);
        if (referenceFrame == null)
            Debug.LogError("LunarLocationController: assign a LunarReferenceFrame component.", this);
    }

    /// <summary>Workflow B: place a marker from latitude and east-positive longitude.</summary>
    public bool TryPlaceFromCoordinates(
        double latitudeDeg,
        double longitudeEastDeg,
        out LunarLocationResult result,
        out string error)
        => TryPlaceFromCoordinates(0, latitudeDeg, longitudeEastDeg, out result, out error);

    /// <summary>Places or updates one of the two independent landing-site slots.</summary>
    public bool TryPlaceFromCoordinates(
        int siteIndex,
        double latitudeDeg,
        double longitudeEastDeg,
        out LunarLocationResult result,
        out string error)
    {
        result = default;
        error = null;

        if (!TryGetSiteState(siteIndex, out SitePanelState site))
        {
            error = "Landing-site index must be 0 (A) or 1 (B).";
            return false;
        }

        if (!ValidateComponents(out error))
        {
            site.InputError = error;
            return false;
        }

        if (!demReader.TryGetElevation(latitudeDeg, longitudeEastDeg,
                out LolaElevationSample terrainSample, out error))
        {
            site.InputError = error;
            return false;
        }

        try
        {
            double markerRadius = terrainSample.radiusMeters + surfaceOffsetMeters;
            LunarCartesianPosition scientific = LunarCoordinates.GeographicToScientificCartesian(
                latitudeDeg, terrainSample.longitudeEastDeg, markerRadius);
            Vector3 moonLocal = referenceFrame.ScientificToMoonLocal(scientific);
            Vector3 world = referenceFrame.MoonLocalToWorld(moonLocal);

            CreateOrUpdateMarker(siteIndex, moonLocal);

            result = new LunarLocationResult
            {
                latitudeDeg = latitudeDeg,
                longitudeEastDeg = terrainSample.longitudeEastDeg,
                elevationMeters = terrainSample.elevationMeters,
                radiusMeters = terrainSample.radiusMeters,
                dnBilinear = terrainSample.dnBilinear,
                moonLocalPosition = moonLocal,
                unityWorldPosition = world
            };

            site.LatitudeInput = latitudeDeg.ToString("F6", CultureInfo.InvariantCulture);
            site.LongitudeInput = terrainSample.longitudeEastDeg.ToString("F6", CultureInfo.InvariantCulture);
            site.InputError = "";
            site.Readout = FormatResult(result);
            site.LastSelectedLocation = result;
            site.HasSelectedLocation = true;
            _activeClickSiteIndex = siteIndex;
            if (_trackedSiteIndex == siteIndex && cameraFocusController != null && cameraFocusController.IsTracking)
                cameraFocusController.SetTrackedLocation(result);
            Debug.Log($"Landing site {(char)('A' + siteIndex)} selected.\n{site.Readout}", this);
            LocationSlotSelected?.Invoke(siteIndex, result);
            if (siteIndex == 0)
                LocationSelected?.Invoke(result);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            site.InputError = error;
            return false;
        }
    }

    /// <summary>
    /// Workflow A: convert a click/raycast hit into a geographic direction, then re-query
    /// LOLA and place the marker at the DEM-derived radius. The clicked radius is NOT used
    /// as the terrain elevation.
    /// </summary>
    public bool TryPlaceFromWorldPosition(
        Vector3 worldHitPosition,
        out LunarLocationResult result,
        out string error)
        => TryPlaceFromWorldPosition(0, worldHitPosition, out result, out error);

    public bool TryPlaceFromWorldPosition(
        int siteIndex,
        Vector3 worldHitPosition,
        out LunarLocationResult result,
        out string error)
    {
        result = default;
        error = null;

        if (!TryGetSiteState(siteIndex, out SitePanelState site))
        {
            error = "Landing-site index must be 0 (A) or 1 (B).";
            return false;
        }

        if (!ValidateComponents(out error))
        {
            site.InputError = error;
            return false;
        }

        try
        {
            Vector3 modelLocalHit = referenceFrame.WorldToMoonLocal(worldHitPosition);
            if (modelLocalHit.sqrMagnitude < 1e-12f)
            {
                error = "The clicked position is too close to the Moon centre to define a surface location.";
                site.InputError = error;
                return false;
            }

            LunarCartesianPosition scientificDirection = referenceFrame.MoonLocalToScientific(modelLocalHit);
            LunarGeographicCoordinate geographic =
                LunarCoordinates.ScientificCartesianToGeographic(scientificDirection);

            // Reuse the exact same path as coordinate input, so both workflows produce
            // identical positions for identical latitude/longitude values.
            return TryPlaceFromCoordinates(
                siteIndex,
                geographic.latitudeDeg,
                geographic.longitudeEastDeg,
                out result,
                out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            site.InputError = error;
            return false;
        }
    }

    /// <summary>
    /// Focus the camera on the most recently selected location. The focus component
    /// normally lives on Main Camera; it is resolved automatically if not assigned.
    /// </summary>
    public bool TryFocusSelectedLocation()
        => TryFocusSelectedLocation(0);

    public bool TryFocusSelectedLocation(int siteIndex)
    {
        if (!TryGetSelectedLocation(siteIndex, out LunarLocationResult location))
        {
            if (TryGetSiteState(siteIndex, out SitePanelState site))
                site.InputError = "Select a landing site before focusing the camera.";
            return false;
        }

        if (!ResolveCameraFocusController())
        {
            if (TryGetSiteState(siteIndex, out SitePanelState site))
                site.InputError = "LunarCameraFocusController was not found. Add it to Main Camera and assign it here.";
            Debug.LogError("LunarLocationController: LunarCameraFocusController was not found.", this);
            return false;
        }

        if (_trackedSiteIndex == siteIndex && cameraFocusController.IsTracking)
        {
            cameraFocusController.StopTracking();
            _trackedSiteIndex = -1;
            return true;
        }

        if (!cameraFocusController.FocusOnLocation(location))
        {
            if (TryGetSiteState(siteIndex, out SitePanelState site))
                site.InputError = "The camera could not focus on this landing site. Check LunarCameraFocusController references and settings.";
            return false;
        }

        _trackedSiteIndex = siteIndex;
        if (TryGetSiteState(siteIndex, out SitePanelState focusedSite))
            focusedSite.InputError = "";
        return true;
    }

    public bool IsTrackingSite(int siteIndex)
        => _trackedSiteIndex == siteIndex && cameraFocusController != null && cameraFocusController.IsTracking;

    public bool SetActiveClickSite(int siteIndex)
    {
        if (!TryGetSiteState(siteIndex, out _))
            return false;
        _activeClickSiteIndex = siteIndex;
        return true;
    }

    private bool ResolveCameraFocusController()
    {
        if (cameraFocusController != null)
            return true;

        cameraFocusController = FindFirstObjectByType<LunarCameraFocusController>();
        if (cameraFocusController == null && Camera.main != null)
            cameraFocusController = Camera.main.GetComponent<LunarCameraFocusController>();

        return cameraFocusController != null;
    }

    private bool ValidateComponents(out string error)
    {
        if (demReader == null)
        {
            error = "LunarDEMReader is not assigned.";
            return false;
        }
        if (referenceFrame == null)
        {
            error = "LunarReferenceFrame is not assigned.";
            return false;
        }
        error = null;
        return true;
    }

    private bool TryGetSiteState(int siteIndex, out SitePanelState site)
    {
        site = null;
        if (_sites == null || siteIndex < 0 || siteIndex >= _sites.Length)
            return false;
        site = _sites[siteIndex];
        return true;
    }

    private void CreateOrUpdateMarker(int siteIndex, Vector3 moonLocalPosition)
    {
        SitePanelState site = _sites[siteIndex];
        if (site.Marker == null)
        {
            if (markerPrefab != null)
                site.Marker = Instantiate(markerPrefab);
            else
                site.Marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);

            site.Marker.name = $"Landing Site {(char)('A' + siteIndex)}";
            foreach (Collider markerCollider in site.Marker.GetComponentsInChildren<Collider>())
                Destroy(markerCollider);
        }

        Transform moonRoot = referenceFrame.MoonRoot;
        site.Marker.transform.SetParent(moonRoot, false);
        site.Marker.transform.localPosition = moonLocalPosition;
        site.Marker.transform.localRotation = Quaternion.identity;
        site.Marker.transform.localScale = Vector3.one * markerDiameterMoonLocalUnits;
    }

    private static string FormatResult(LunarLocationResult result)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "Selected lunar location\n" +
            "Latitude:  {0:F6}°\n" +
            "Longitude: {1:F6}° E\n" +
            "LOLA elevation: {2:F3} m\n" +
            "Radius: {3:F3} m\n" +
            "Moon-local XYZ: ({4:F6}, {5:F6}, {6:F6})\n" +
            "Unity world XYZ: ({7:F6}, {8:F6}, {9:F6})",
            result.latitudeDeg,
            result.longitudeEastDeg,
            result.elevationMeters,
            result.radiusMeters,
            result.moonLocalPosition.x,
            result.moonLocalPosition.y,
            result.moonLocalPosition.z,
            result.unityWorldPosition.x,
            result.unityWorldPosition.y,
            result.unityWorldPosition.z);
    }

    private static bool TryParse(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               LunarCoordinates.IsFinite(value);
    }
}
