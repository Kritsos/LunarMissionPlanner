using UnityEngine;

/// <summary>
/// Focuses the Main Camera directly above a selected LOLA location and follows
/// it as the Moon translates and rotates.
/// </summary>
[DefaultExecutionOrder(200)]
public sealed class LunarCameraFocusController : MonoBehaviour
{
    private const float MinimumTrackingAltitudeMeters = 750f;

    [Header("References")]
    [SerializeField] private Camera targetCamera;
    [SerializeField] private LunarReferenceFrame referenceFrame;
    [SerializeField] private Transform earthRoot;
    [SerializeField] private SimulationTime simulationTime;

    [Header("Startup view")]
    [Tooltip("Start above the Moon with the camera facing toward Earth.")]
    [SerializeField] private bool setEarthFacingStartupView = true;
    [Tooltip("Starting camera distance from the Moon's center, in lunar radii. Five radii keeps the full Moon in the default view.")]
    [Min(2f)] [SerializeField] private float startupDistanceMoonRadii = 5f;
    [Tooltip("Small offset from the anti-Earth direction. This keeps the Moon beside Earth instead of covering it.")]
    [Range(0f, 45f)] [SerializeField] private float startupOffsetFromAntiEarthDegrees = 15f;

    [Header("Focus view")]
    [Tooltip("Camera height above the selected LOLA location, in physical metres.")]
    [Min(MinimumTrackingAltitudeMeters)] [SerializeField] private float focusAltitudeMeters = 2500f;

    [Tooltip("Scroll-wheel zoom sensitivity while tracking. Typical mouse-wheel input changes altitude by roughly 25–35% per notch.")]
    [Min(0.1f)] [SerializeField] private float scrollZoomSensitivity = 3.5f;

    [Tooltip("Camera angle from local vertical. Zero looks straight down; 35 degrees gives a modest oblique view of the surrounding terrain.")]
    [Range(0f, 75f)] [SerializeField] private float viewAngleFromVerticalDegrees = 35f;

    [Header("Tracking smoothness")]
    [Tooltip("Position response time in seconds. Higher values smooth motion more but let the camera lag farther behind the site.")]
    [Min(0f)] [SerializeField] private float trackingPositionSmoothTime = 0f;

    [Tooltip("Rotation response time in seconds. Higher values soften small orientation changes but let the view lag farther behind.")]
    [Min(0f)] [SerializeField] private float trackingRotationSmoothTime = 0f;

    [Header("Close-up camera clipping")]
    [Tooltip("Near clip plane used while focused. Unity units, not metres.")]
    [Min(0.000001f)] [SerializeField] private float focusNearClipPlane = 0.000005f;

    [Tooltip("Far clip plane used while focused. Unity units, not metres.")]
    [Min(0.001f)] [SerializeField] private float focusFarClipPlane = 0.05f;

    [Tooltip("Restore the camera's original clipping planes after moving this far above the lunar reference radius.")]
    [Min(1000f)] [SerializeField] private float restoreClippingAboveAltitudeMeters = 100000f;

    private bool _isTracking;
    private LunarLocationResult _trackedLocation;
    private FreeCameraController _freeCameraController;
    private bool _freeCameraWasEnabled;
    private bool _hasSavedClippingPlanes;
    private bool _usingFocusClipping;
    private LunarSitePanelsUI _sitePanelsUI;
    private float _originalNearClip;
    private float _originalFarClip;
    private Vector3 _positionSmoothVelocity;
    private bool _hasSetStartupView;

    public bool IsTracking => _isTracking;
    public float FocusAltitudeMeters => focusAltitudeMeters;

    private void Awake()
    {
        ResolveReferences();
    }

    private void Update()
    {
        if (!_usingFocusClipping || !ResolveReferences())
            return;

        if (_isTracking)
        {
            ApplyScrollWheelZoom();
            return;
        }

        LunarCartesianPosition cameraScientific = referenceFrame.WorldToScientific(targetCamera.transform.position);
        double altitudeAboveReference = cameraScientific.Magnitude - referenceFrame.ReferenceRadiusMeters;
        float moonRadiusWorld = GetMoonWorldRadius();
        float cameraToMoonCenter = Vector3.Distance(targetCamera.transform.position, referenceFrame.MoonRoot.position);
        bool clearOfMoonForDefaultClipping = cameraToMoonCenter > moonRadiusWorld + _originalNearClip;

        if (altitudeAboveReference > restoreClippingAboveAltitudeMeters && clearOfMoonForDefaultClipping)
            RestoreDefaultClippingPlanes();
    }

    private void ApplyScrollWheelZoom()
    {
        if (_sitePanelsUI != null && _sitePanelsUI.IsPointerOverPanel())
            return;

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < 0.0001f)
            return;

        // Positive wheel input zooms in. The camera pose is recomputed in
        // LateUpdate while keeping the selected body-fixed point as its target.
        float scale = Mathf.Exp(-scroll * scrollZoomSensitivity);
        focusAltitudeMeters = Mathf.Max(MinimumTrackingAltitudeMeters, focusAltitudeMeters * scale);
    }

    private void LateUpdate()
    {
        if (!_hasSetStartupView && setEarthFacingStartupView && ResolveReferences())
            TrySetEarthFacingStartupView();

        if (!_isTracking)
            return;

        if (!ResolveReferences() || !TryCalculateFocusPose(_trackedLocation, out Vector3 position, out Quaternion rotation))
        {
            StopTracking();
            return;
        }

        float deltaTime = Time.unscaledDeltaTime;
        Vector3 smoothedPosition;
        if (trackingPositionSmoothTime <= 0f)
        {
            _positionSmoothVelocity = Vector3.zero;
            smoothedPosition = position;
        }
        else
        {
            smoothedPosition = Vector3.SmoothDamp(
                targetCamera.transform.position,
                position,
                ref _positionSmoothVelocity,
                trackingPositionSmoothTime,
                Mathf.Infinity,
                deltaTime);
        }
        Quaternion smoothedRotation = SmoothRotation(
            targetCamera.transform.rotation,
            rotation,
            trackingRotationSmoothTime,
            deltaTime);

        targetCamera.transform.SetPositionAndRotation(smoothedPosition, smoothedRotation);
        UpdateFocusClippingPlanes(smoothedPosition, _trackedLocation.moonLocalPosition);
    }

    /// <summary>Start tracking a selected location, or retarget an active camera track.</summary>
    public bool FocusOnLocation(LunarLocationResult location)
    {
        if (!ResolveReferences() || !TryCalculateFocusPose(location, out Vector3 position, out Quaternion rotation))
            return false;

        bool startingTracking = !_isTracking;
        if (startingTracking)
            SaveDefaultClippingPlanesIfNeeded();

        _trackedLocation = location;
        _isTracking = true;
        _positionSmoothVelocity = Vector3.zero;
        if (startingTracking)
            targetCamera.transform.SetPositionAndRotation(position, rotation);

        if (startingTracking)
        {
            _usingFocusClipping = true;
            DisableFreeCameraWhileTracking();
        }
        UpdateFocusClippingPlanes(targetCamera.transform.position, _trackedLocation.moonLocalPosition);

        Debug.Log(
            $"Tracking lunar location {location.latitudeDeg:F5}°, {location.longitudeEastDeg:F5}°E " +
            $"at {focusAltitudeMeters:F0} m nominal altitude.", this);
        return true;
    }

    /// <summary>Toggle tracking: start on the supplied location or stop the current track.</summary>
    public bool ToggleTrackingOnLocation(LunarLocationResult location)
    {
        if (_isTracking)
        {
            StopTracking();
            return true;
        }

        return FocusOnLocation(location);
    }

    /// <summary>Retarget the camera when the user selects another location during tracking.</summary>
    public bool SetTrackedLocation(LunarLocationResult location)
    {
        if (!_isTracking || !ResolveReferences() ||
            !TryCalculateFocusPose(location, out _, out _))
            return false;

        _trackedLocation = location;
        _positionSmoothVelocity = Vector3.zero;
        UpdateFocusClippingPlanes(targetCamera.transform.position, _trackedLocation.moonLocalPosition);
        return true;
    }

    /// <summary>Stop following the Moon, leaving the camera at its current world pose.</summary>
    public void StopTracking()
    {
        _isTracking = false;
        _positionSmoothVelocity = Vector3.zero;
        RestoreFreeCameraAfterTracking();
    }

    private static Quaternion SmoothRotation(
        Quaternion current,
        Quaternion target,
        float smoothTimeSeconds,
        float deltaTime)
    {
        if (smoothTimeSeconds <= 0f)
            return target;
        if (deltaTime <= 0f)
            return current;

        // Exponential smoothing is frame-rate independent and avoids applying
        // every tiny frame-to-frame change directly to the camera orientation.
        float blend = 1f - Mathf.Exp(-deltaTime / smoothTimeSeconds);
        return Quaternion.Slerp(current, target, blend);
    }

    /// <summary>Restore the camera's clipping planes to their values before the close-up view.</summary>
    public void RestoreDefaultClippingPlanes()
    {
        if (targetCamera == null || !_hasSavedClippingPlanes)
            return;

        targetCamera.nearClipPlane = _originalNearClip;
        targetCamera.farClipPlane = _originalFarClip;
        _usingFocusClipping = false;
        _hasSavedClippingPlanes = false;
    }

    private bool TryCalculateFocusPose(
        LunarLocationResult location,
        out Vector3 cameraWorld,
        out Quaternion cameraRotation)
    {
        cameraWorld = Vector3.zero;
        cameraRotation = Quaternion.identity;

        if (!LunarCoordinates.IsFinite(location.latitudeDeg) ||
            location.latitudeDeg < -90.0 || location.latitudeDeg > 90.0 ||
            !LunarCoordinates.IsFinite(location.longitudeEastDeg))
        {
            Debug.LogError("LunarCameraFocusController: selected location has invalid coordinates.", this);
            return false;
        }

        LunarCartesianPosition site = referenceFrame.MoonLocalToScientific(location.moonLocalPosition);
        if (site.Magnitude <= 1.0)
        {
            Debug.LogError("LunarCameraFocusController: selected location is too close to the Moon centre.", this);
            return false;
        }

        double latitudeRad = location.latitudeDeg * System.Math.PI / 180.0;
        double longitudeRad = LunarCoordinates.NormalizeLongitudeEast(location.longitudeEastDeg) * System.Math.PI / 180.0;
        double sinLatitude = System.Math.Sin(latitudeRad);
        double cosLatitude = System.Math.Cos(latitudeRad);
        double sinLongitude = System.Math.Sin(longitudeRad);
        double cosLongitude = System.Math.Cos(longitudeRad);

        // Use the documented ME-frame local basis. At the default zero view
        // angle, radialUp puts the camera directly above the site. Geographic
        // north remains a stable camera-up reference in both hemispheres;
        // Unity world-up can become singular near the lunar poles.
        LunarCartesianPosition radialUp = new LunarCartesianPosition(
            cosLatitude * cosLongitude,
            cosLatitude * sinLongitude,
            sinLatitude);
        LunarCartesianPosition lunarNorth = new LunarCartesianPosition(
            -sinLatitude * cosLongitude,
            -sinLatitude * sinLongitude,
            cosLatitude);
        LunarCartesianPosition lunarEast = new LunarCartesianPosition(
            -sinLongitude,
            cosLongitude,
            0.0);

        double horizontalMeters = focusAltitudeMeters *
                                  System.Math.Tan(viewAngleFromVerticalDegrees * System.Math.PI / 180.0);
        LunarCartesianPosition cameraScientific = new LunarCartesianPosition(
            site.x + radialUp.x * focusAltitudeMeters + lunarNorth.x * horizontalMeters,
            site.y + radialUp.y * focusAltitudeMeters + lunarNorth.y * horizontalMeters,
            site.z + radialUp.z * focusAltitudeMeters + lunarNorth.z * horizontalMeters);

        Vector3 targetWorld = referenceFrame.MoonLocalToWorld(location.moonLocalPosition);
        cameraWorld = referenceFrame.MoonLocalToWorld(referenceFrame.ScientificToMoonLocal(cameraScientific));
        Vector3 forward = targetWorld - cameraWorld;
        if (forward.sqrMagnitude < 1e-12f)
        {
            Debug.LogError("LunarCameraFocusController: could not calculate a camera viewing direction.", this);
            return false;
        }

        Vector3 northWorld = ScientificDirectionToWorld(site, lunarNorth) - targetWorld;
        if (northWorld.sqrMagnitude < 1e-12f || Vector3.Cross(forward, northWorld).sqrMagnitude < 1e-10f)
            northWorld = ScientificDirectionToWorld(site, lunarEast) - targetWorld;
        if (northWorld.sqrMagnitude < 1e-12f)
            northWorld = ScientificDirectionToWorld(site, radialUp) - targetWorld;

        cameraRotation = Quaternion.LookRotation(forward.normalized, northWorld.normalized);
        return true;
    }

    private Vector3 ScientificDirectionToWorld(LunarCartesianPosition origin, LunarCartesianPosition direction)
    {
        const double directionStepMeters = 1000.0;
        LunarCartesianPosition point = new LunarCartesianPosition(
            origin.x + direction.x * directionStepMeters,
            origin.y + direction.y * directionStepMeters,
            origin.z + direction.z * directionStepMeters);
        return referenceFrame.ScientificToWorld(point);
    }

    private void DisableFreeCameraWhileTracking()
    {
        _freeCameraController = targetCamera.GetComponent<FreeCameraController>();
        if (_freeCameraController == null)
            return;

        _freeCameraWasEnabled = _freeCameraController.enabled;
        if (_freeCameraWasEnabled)
            _freeCameraController.enabled = false;
    }

    private void RestoreFreeCameraAfterTracking()
    {
        if (_freeCameraController == null || !_freeCameraWasEnabled)
            return;

        _freeCameraController.enabled = true;
        _freeCameraController.SynchronizeLookAnglesToTransform();
    }

    private void SaveDefaultClippingPlanesIfNeeded()
    {
        if (_usingFocusClipping || _hasSavedClippingPlanes)
            return;

        _originalNearClip = targetCamera.nearClipPlane;
        _originalFarClip = targetCamera.farClipPlane;
        _hasSavedClippingPlanes = true;
    }

    private void UpdateFocusClippingPlanes(Vector3 cameraPosition, Vector3 targetMoonLocalPosition)
    {
        if (targetCamera == null || referenceFrame == null)
            return;

        Vector3 targetWorld = referenceFrame.MoonLocalToWorld(targetMoonLocalPosition);
        float distanceToTarget = Vector3.Distance(cameraPosition, targetWorld);
        float moonRadiusWorld = GetMoonWorldRadius();

        // Keep the selected surface in the view at any tracking zoom distance,
        // while growing the near plane slowly enough to avoid cutting through the Moon.
        float nearLimit = Mathf.Max(focusNearClipPlane, moonRadiusWorld * 0.1f);
        targetCamera.nearClipPlane = Mathf.Clamp(distanceToTarget * 0.0001f, focusNearClipPlane, nearLimit);
        targetCamera.farClipPlane = Mathf.Max(focusFarClipPlane, distanceToTarget * 2f + moonRadiusWorld);
    }

    private float GetMoonWorldRadius()
    {
        return (float)(referenceFrame.ModelReferenceRadiusMoonLocalUnits *
                       referenceFrame.MoonRoot.lossyScale.magnitude / Mathf.Sqrt(3f));
    }

    private bool TrySetEarthFacingStartupView()
    {
        if (simulationTime != null && !simulationTime.IsReady)
            return false;

        Transform moonRoot = referenceFrame.MoonRoot;
        if (earthRoot == null || moonRoot == null)
            return false;

        Vector3 earthDirection = earthRoot.position - moonRoot.position;
        if (earthDirection.sqrMagnitude < 1e-8f)
            return false;
        earthDirection.Normalize();

        Vector3 sideDirection = Vector3.Cross(earthDirection, Vector3.up);
        if (sideDirection.sqrMagnitude < 1e-8f)
            sideDirection = Vector3.Cross(earthDirection, Vector3.forward);
        sideDirection.Normalize();

        float offsetRadians = startupOffsetFromAntiEarthDegrees * Mathf.Deg2Rad;
        Vector3 cameraFromMoonDirection = -earthDirection * Mathf.Cos(offsetRadians) +
                                          sideDirection * Mathf.Sin(offsetRadians);
        Vector3 cameraPosition = moonRoot.position +
                                 cameraFromMoonDirection * (GetMoonWorldRadius() * startupDistanceMoonRadii);
        Quaternion cameraRotation = Quaternion.LookRotation(earthDirection, sideDirection);

        targetCamera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
        _freeCameraController = GetComponent<FreeCameraController>();
        if (_freeCameraController != null)
            _freeCameraController.SynchronizeLookAnglesToTransform();

        _hasSetStartupView = true;
        return true;
    }

    private bool ResolveReferences()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>();
        if (targetCamera == null)
            targetCamera = Camera.main;
        if (referenceFrame == null)
            referenceFrame = FindFirstObjectByType<LunarReferenceFrame>();
        if (earthRoot == null)
        {
            GameObject earthObject = GameObject.Find("Earth");
            if (earthObject != null)
                earthRoot = earthObject.transform;
        }
        if (simulationTime == null)
            simulationTime = FindFirstObjectByType<SimulationTime>();
        if (_sitePanelsUI == null)
            _sitePanelsUI = FindFirstObjectByType<LunarSitePanelsUI>();

        if (targetCamera == null || referenceFrame == null)
        {
            Debug.LogError(
                "LunarCameraFocusController: assign a Camera and LunarReferenceFrame in the Inspector.", this);
            return false;
        }

        return true;
    }
}
