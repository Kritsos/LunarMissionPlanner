using System;
using System.Collections;
using System.Globalization;
using UnityEngine;

[Serializable]
public struct LunarSitePanelSettings
{
    [Min(0f)] public float panelAreaSquareMeters;
    [Range(0f, 1f)] public float panelEfficiency;
    public bool panelParallelToSurface;
    [Range(0f, 90f)] public float fixedPanelTiltDegrees;
    [Range(0f, 360f)] public float fixedPanelAzimuthDegrees;
    [Min(0f)] public float panelHeightMeters;
}

public struct LunarSitePanelReadout
{
    public bool hasSite;
    public bool horizonReady;
    public bool hasLatestResult;
    public LunarSurfaceSite surfaceSite;
    public LunarVisibilityResult visibility;
    public SolarPowerResult power;
    public double accumulatedEnergyWh;
    public double horizonProgress;
    public int horizonSamples;
    public string status;
    public string calculationError;
    public string energyMessage;
}

/// <summary>
/// Calculates per-site Module 3 results from LOLA geometry and the DE440 simulation clock.
/// Site-panel settings and runtime calculation state are indexed independently for A and B.
/// </summary>
public sealed class LunarVisibilityPresenter : MonoBehaviour
{
    [Header("Project inputs")]
    [SerializeField] private LunarLocationController locationController;
    [SerializeField] private LunarDEMReader demReader;
    [SerializeField] private SimulationTime simulationTime;
    [SerializeField] private EphemerisDataProvider ephemerisProvider;

    [Header("Horizon model")]
    [Min(0.1f)] [SerializeField] private float azimuthStepDegrees = 1f;
    [Min(10f)] [SerializeField] private float radialStepMeters = 1000f;
    [Min(1000f)] [SerializeField] private float maximumRangeKm = 200f;
    [Min(10f)] [SerializeField] private float surfaceNormalSampleStepMeters = 500f;
    [Min(1)] [SerializeField] private int azimuthBinsPerFrame = 2;

    [Header("Solar panel model")]
    [SerializeField] private LunarSitePanelSettings[] sitePanelSettings =
    {
        new LunarSitePanelSettings
        {
            panelAreaSquareMeters = 1f,
            panelEfficiency = 0.20f,
            panelParallelToSurface = true,
            fixedPanelTiltDegrees = 45f,
            fixedPanelAzimuthDegrees = 180f,
            panelHeightMeters = 2f
        },
        new LunarSitePanelSettings
        {
            panelAreaSquareMeters = 1f,
            panelEfficiency = 0.20f,
            panelParallelToSurface = true,
            fixedPanelTiltDegrees = 45f,
            fixedPanelAzimuthDegrees = 180f,
            panelHeightMeters = 2f
        }
    };
    [Min(1f)] [SerializeField] private float nominalSolarIrradianceWPerM2AtAu = 1361f;

    [Header("Energy integration")]
    [Min(60f)] [SerializeField] private float maximumEnergyStepSeconds = 900f;
    [Min(1)] [SerializeField] private int maximumEnergySamplesPerUpdate = 2000;

    private sealed class SiteRuntimeState
    {
        public LunarSurfaceSite Site;
        public LunarHorizonProfile Horizon;
        public LunarVisibilityResult LatestVisibility;
        public SolarPowerResult LatestPower;
        public bool HasSite;
        public bool HasLatestResult;
        public bool HorizonReady;
        public Coroutine HorizonRoutine;
        public string Status = "Site not selected.";
        public string CalculationError = "";
        public double HorizonProgress;
        public int HorizonSamples;
        public double AccumulatedEnergyWh;
        public double PreviousEnergyEt;
        public SolarEnergySample PreviousEnergySample;
        public bool HasPreviousEnergySample;
        public string EnergyMessage = "Energy accumulates as simulation time advances.";
        public int EnergySamplesUsed;
    }

    private readonly SiteRuntimeState[] _siteStates =
    {
        new SiteRuntimeState(),
        new SiteRuntimeState()
    };

    private static readonly Color[] HorizonColors =
    {
        new Color(1f, 0.55f, 0.2f, 1f),
        new Color(1f, 0.55f, 0.2f, 1f)
    };
    private static readonly Color[] SunColors =
    {
        new Color(1f, 0.88f, 0.18f, 1f),
        new Color(1f, 0.88f, 0.18f, 1f)
    };
    private static readonly Color[] EarthColors =
    {
        new Color(0.25f, 0.78f, 1f, 1f),
        new Color(0.25f, 0.78f, 1f, 1f)
    };

    private void Awake()
    {
        EnsureSitePanelSettings();
    }

    private void Start()
    {
        ResolveReferences();
        if (locationController == null || demReader == null || simulationTime == null)
        {
            string error = "Missing project input. Assign the location, DEM, and simulation components.";
            for (int siteIndex = 0; siteIndex < _siteStates.Length; siteIndex++)
            {
                _siteStates[siteIndex].CalculationError = error;
                _siteStates[siteIndex].Status = "Project inputs need attention.";
            }
            Debug.LogError("[LunarVisibilityPresenter] " + error, this);
            return;
        }

        locationController.LocationSlotSelected += OnLocationSlotSelected;
        locationController.LocationSlotCleared += OnLocationSlotCleared;
        simulationTime.StateUpdated += OnStateUpdated;
        simulationTime.UtcTimeSet += OnUtcTimeSet;

        bool anySelected = false;
        for (int siteIndex = 0; siteIndex < _siteStates.Length; siteIndex++)
        {
            if (locationController.TryGetSelectedLocation(siteIndex, out LunarLocationResult selected))
            {
                OnLocationSlotSelected(siteIndex, selected);
                anySelected = true;
            }
        }

        if (!anySelected && simulationTime.TryGetCurrentState(out EphemerisState state))
            OnStateUpdated(state);
    }

    private void OnDestroy()
    {
        if (locationController != null)
        {
            locationController.LocationSlotSelected -= OnLocationSlotSelected;
            locationController.LocationSlotCleared -= OnLocationSlotCleared;
        }
        if (simulationTime != null)
        {
            simulationTime.StateUpdated -= OnStateUpdated;
            simulationTime.UtcTimeSet -= OnUtcTimeSet;
        }
        for (int siteIndex = 0; siteIndex < _siteStates.Length; siteIndex++)
            CancelHorizonBuild(siteIndex);
    }

    public bool TryGetSitePanelSettings(int siteIndex, out LunarSitePanelSettings settings)
    {
        settings = default;
        EnsureSitePanelSettings();
        if (siteIndex < 0 || siteIndex >= sitePanelSettings.Length)
            return false;
        settings = sitePanelSettings[siteIndex];
        return true;
    }

    public bool TryGetSitePanelReadout(int siteIndex, out LunarSitePanelReadout readout)
    {
        readout = default;
        if (siteIndex < 0 || siteIndex >= _siteStates.Length)
            return false;

        SiteRuntimeState site = _siteStates[siteIndex];
        readout = new LunarSitePanelReadout
        {
            hasSite = site.HasSite,
            horizonReady = site.HorizonReady,
            hasLatestResult = site.HasLatestResult,
            surfaceSite = site.Site,
            visibility = site.LatestVisibility,
            power = site.LatestPower,
            accumulatedEnergyWh = site.AccumulatedEnergyWh,
            horizonProgress = site.HorizonProgress,
            horizonSamples = site.HorizonSamples,
            status = site.Status,
            calculationError = site.CalculationError,
            energyMessage = site.EnergyMessage
        };
        return true;
    }

    public bool TryApplySitePanelSettings(
        int siteIndex,
        LunarSitePanelSettings settings,
        out bool horizonRebuilt,
        out string error)
    {
        horizonRebuilt = false;
        error = null;
        EnsureSitePanelSettings();
        if (siteIndex < 0 || siteIndex >= _siteStates.Length)
        {
            error = "Landing-site index must be 0 (A) or 1 (B).";
            return false;
        }
        if (!ValidateSitePanelSettings(settings, out error))
            return false;

        LunarSitePanelSettings previous = sitePanelSettings[siteIndex];
        settings.fixedPanelAzimuthDegrees = (float)LunarVectorMath.WrapDegrees(settings.fixedPanelAzimuthDegrees);
        sitePanelSettings[siteIndex] = settings;

        SiteRuntimeState site = _siteStates[siteIndex];
        bool panelHeightChanged = !Mathf.Approximately(previous.panelHeightMeters, settings.panelHeightMeters);
        site.AccumulatedEnergyWh = 0.0;
        site.HasPreviousEnergySample = false;
        site.EnergySamplesUsed = 0;
        site.EnergyMessage = "Energy window reset by applying site panel settings.";
        site.CalculationError = "";

        if (panelHeightChanged && site.HasSite)
        {
            horizonRebuilt = true;
            RebuildHorizon(siteIndex);
        }
        else if (site.HorizonReady && simulationTime != null &&
                 simulationTime.TryGetCurrentState(out EphemerisState state))
        {
            UpdateSiteForState(siteIndex, state);
        }
        return true;
    }

    private static bool ValidateSitePanelSettings(LunarSitePanelSettings settings, out string error)
    {
        if (!LunarCoordinates.IsFinite(settings.panelAreaSquareMeters) ||
            settings.panelAreaSquareMeters < 0f || settings.panelAreaSquareMeters > 100000f)
        {
            error = "Panel area must be between 0 and 100,000 m2.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(settings.panelEfficiency) ||
            settings.panelEfficiency < 0f || settings.panelEfficiency > 1f)
        {
            error = "Panel efficiency must be between 0 and 100 percent.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(settings.panelHeightMeters) ||
            settings.panelHeightMeters < 0f || settings.panelHeightMeters > 100000f)
        {
            error = "Panel height must be between 0 and 100,000 metres.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(settings.fixedPanelAzimuthDegrees))
        {
            error = "Panel azimuth must be a finite number.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(settings.fixedPanelTiltDegrees) ||
            settings.fixedPanelTiltDegrees < 0f || settings.fixedPanelTiltDegrees > 90f)
        {
            error = "Panel tilt must be between 0 and 90 degrees.";
            return false;
        }
        error = null;
        return true;
    }

    private void EnsureSitePanelSettings()
    {
        if (sitePanelSettings != null && sitePanelSettings.Length == _siteStates.Length)
            return;

        LunarSitePanelSettings[] existing = sitePanelSettings;
        sitePanelSettings = new LunarSitePanelSettings[_siteStates.Length];
        for (int siteIndex = 0; siteIndex < sitePanelSettings.Length; siteIndex++)
            sitePanelSettings[siteIndex] = existing != null && siteIndex < existing.Length
                ? existing[siteIndex]
                : CreateDefaultSitePanelSettings();
    }

    private static LunarSitePanelSettings CreateDefaultSitePanelSettings()
    {
        return new LunarSitePanelSettings
        {
            panelAreaSquareMeters = 1f,
            panelEfficiency = 0.20f,
            panelParallelToSurface = true,
            fixedPanelTiltDegrees = 45f,
            fixedPanelAzimuthDegrees = 180f,
            panelHeightMeters = 2f
        };
    }

    public void RebuildHorizon()
    {
        for (int siteIndex = 0; siteIndex < _siteStates.Length; siteIndex++)
            RebuildHorizon(siteIndex);
    }

    private void RebuildHorizon(int siteIndex)
    {
        SiteRuntimeState site = _siteStates[siteIndex];
        if (!site.HasSite || demReader == null)
            return;

        CancelHorizonBuild(siteIndex);
        site.HorizonReady = false;
        site.Horizon = null;
        site.HasLatestResult = false;
        site.HasPreviousEnergySample = false;
        site.AccumulatedEnergyWh = 0.0;
        site.HorizonSamples = 0;
        site.HorizonProgress = 0.0;

        if (!TryGetSettings(out LunarHorizonSettings settings, out string error))
        {
            site.CalculationError = error;
            site.Status = "Horizon settings need attention.";
            return;
        }

        site.CalculationError = "";
        site.Status = "Building a LOLA terrain horizon...";
        site.EnergyMessage = "Energy integration starts when the horizon is ready.";
        site.HorizonRoutine = StartCoroutine(BuildHorizon(siteIndex, settings));
    }

    private IEnumerator BuildHorizon(int siteIndex, LunarHorizonSettings settings)
    {
        SiteRuntimeState site = _siteStates[siteIndex];
        int binCount = (int)Math.Round(360.0 / settings.azimuthStepDegrees);
        double[] elevations = new double[binCount];
        int sampleCount = 0;
        double maxElevation = 0.0;
        double panelHeightMeters = sitePanelSettings[siteIndex].panelHeightMeters;

        for (int i = 0; i < binCount; i++)
        {
            double azimuth = i * settings.azimuthStepDegrees;
            if (!LunarHorizonCalculator.TryComputeAzimuth(
                    demReader, site.Site, panelHeightMeters, settings, azimuth,
                    out double elevation, out int usedSamples, out string error))
            {
                site.CalculationError = error;
                site.Status = "Could not calculate the terrain horizon.";
                site.HorizonRoutine = null;
                yield break;
            }

            elevations[i] = elevation;
            sampleCount += usedSamples;
            maxElevation = Math.Max(maxElevation, elevation);
            site.HorizonProgress = (i + 1.0) / binCount;
            site.HorizonSamples = sampleCount;

            if ((i + 1) % Mathf.Max(1, azimuthBinsPerFrame) == 0)
                yield return null;
        }

        site.Horizon = new LunarHorizonProfile(
            site.Site, panelHeightMeters, settings, elevations, sampleCount, maxElevation);
        site.HorizonReady = true;
        site.HorizonRoutine = null;
        site.CalculationError = "";
        site.Status = "LOLA terrain horizon ready.";

        if (simulationTime != null && simulationTime.TryGetCurrentState(out EphemerisState state))
            UpdateSiteForState(siteIndex, state);
    }

    private void OnLocationSlotSelected(int siteIndex, LunarLocationResult location)
    {
        if (siteIndex < 0 || siteIndex >= _siteStates.Length)
            return;

        ResetSiteRuntimeState(siteIndex, "Sampling LOLA around the selected site...");
        SiteRuntimeState site = _siteStates[siteIndex];
        site.EnergyMessage = "Energy integration starts when the horizon is ready.";

        if (!LunarSurfaceGeometry.TryCreateSite(
                demReader, location.latitudeDeg, location.longitudeEastDeg,
                surfaceNormalSampleStepMeters, out LunarSurfaceSite selectedSite, out string error))
        {
            site.Site = default;
            site.CalculationError = error;
            site.Status = "Could not create the scientific site geometry.";
            return;
        }

        site.Site = selectedSite;
        site.HasSite = true;
        RebuildHorizon(siteIndex);
    }

    private void OnLocationSlotCleared(int siteIndex)
    {
        if (siteIndex < 0 || siteIndex >= _siteStates.Length)
            return;
        ResetSiteRuntimeState(siteIndex, "Site not selected.");
    }

    private void ResetSiteRuntimeState(int siteIndex, string status)
    {
        CancelHorizonBuild(siteIndex);
        _siteStates[siteIndex] = new SiteRuntimeState
        {
            Status = status,
            EnergyMessage = "Energy integration starts when the horizon is ready."
        };
    }

    private void OnStateUpdated(EphemerisState state)
    {
        for (int siteIndex = 0; siteIndex < _siteStates.Length; siteIndex++)
            UpdateSiteForState(siteIndex, state);
    }

    private void OnUtcTimeSet()
    {
        for (int siteIndex = 0; siteIndex < _siteStates.Length; siteIndex++)
        {
            SiteRuntimeState site = _siteStates[siteIndex];
            site.AccumulatedEnergyWh = 0.0;
            site.HasPreviousEnergySample = false;
            site.EnergySamplesUsed = 0;
            site.EnergyMessage = "Energy window reset at the selected UTC time.";

            if (site.HorizonReady && simulationTime != null &&
                simulationTime.TryGetCurrentState(out EphemerisState state))
            {
                UpdateSiteForState(siteIndex, state);
            }
        }
    }

    private void UpdateSiteForState(int siteIndex, EphemerisState state)
    {
        SiteRuntimeState site = _siteStates[siteIndex];
        if (!site.HorizonReady || site.Horizon == null)
            return;

        if (!TryEvaluate(site, siteIndex, state, out LunarVisibilityResult visibility, out SolarPowerResult power, out string error))
        {
            site.CalculationError = error;
            site.Status = "Could not evaluate Sun/Earth visibility.";
            return;
        }

        site.LatestVisibility = visibility;
        site.LatestPower = power;
        site.HasLatestResult = true;
        site.CalculationError = "";

        if (!site.HasPreviousEnergySample)
        {
            site.PreviousEnergyEt = state.etSeconds;
            site.PreviousEnergySample = ToEnergySample(visibility, power);
            site.HasPreviousEnergySample = true;
            site.EnergyMessage = "Integration window starts at the current simulation time.";
            return;
        }

        double duration = state.etSeconds - site.PreviousEnergyEt;
        if (duration < -1e-6)
        {
            site.AccumulatedEnergyWh = 0.0;
            site.PreviousEnergyEt = state.etSeconds;
            site.PreviousEnergySample = ToEnergySample(visibility, power);
            site.EnergyMessage = "Time moved backward; the energy window restarted.";
            return;
        }
        if (duration <= 1e-6)
            return;

        SolarEnergySample currentSample = ToEnergySample(visibility, power);
        SolarEnergySampleProvider sampleProvider =
            delegate(double sampleEt, out SolarEnergySample sample, out string sampleError)
            {
                return TryGetEnergySampleAtEt(siteIndex, sampleEt, out sample, out sampleError);
            };
        if (!SolarPowerCalculator.TryIntegrateInterval(
                site.PreviousEnergyEt, site.PreviousEnergySample,
                state.etSeconds, currentSample,
                Math.Max(60.0, maximumEnergyStepSeconds),
                1.0,
                maximumEnergySamplesPerUpdate,
                sampleProvider,
                out double intervalEnergyWh,
                out int samplesUsed,
                out bool sampleLimitExceeded,
                out string integrationError))
        {
            if (sampleLimitExceeded)
            {
                site.AccumulatedEnergyWh = 0.0;
                site.PreviousEnergyEt = state.etSeconds;
                site.PreviousEnergySample = currentSample;
                site.EnergyMessage = "Integration workload limit reached; energy window restarted at the new time.";
            }
            else
            {
                site.EnergyMessage = "Energy integration paused: " + integrationError;
            }
            return;
        }

        site.AccumulatedEnergyWh += intervalEnergyWh;
        site.PreviousEnergyEt = state.etSeconds;
        site.PreviousEnergySample = currentSample;
        site.EnergySamplesUsed = samplesUsed;
        site.EnergyMessage = string.Format(
            CultureInfo.InvariantCulture,
            "Adaptive trapezoidal integration · {0:F0} s maximum step, {1:N0} ephemeris samples.",
            maximumEnergyStepSeconds, samplesUsed);
        site.Status = "Sun/Earth geometry and solar power updated.";
    }

    private bool TryGetEnergySampleAtEt(
        int siteIndex,
        double etSeconds,
        out SolarEnergySample sample,
        out string error)
    {
        sample = default;
        error = null;
        if (ephemerisProvider == null)
        {
            error = "EphemerisDataProvider is not available.";
            return false;
        }
        if (!ephemerisProvider.TryGetAtEt(etSeconds, out EphemerisState state, out error))
            return false;

        SiteRuntimeState site = _siteStates[siteIndex];
        if (!TryEvaluate(site, siteIndex, state, out LunarVisibilityResult visibility, out SolarPowerResult power, out error))
            return false;

        sample = ToEnergySample(visibility, power);
        return true;
    }

    private static SolarEnergySample ToEnergySample(LunarVisibilityResult visibility, SolarPowerResult power)
        => new SolarEnergySample(power.powerWatts, visibility.sunClearanceDeg, visibility.sunVisible);

    private bool TryEvaluate(
        SiteRuntimeState site,
        int siteIndex,
        EphemerisState state,
        out LunarVisibilityResult visibility,
        out SolarPowerResult power,
        out string error)
    {
        visibility = default;
        power = default;
        if (!LunarVisibilityCalculator.TryEvaluate(state, site.Site, site.Horizon, out visibility, out error))
            return false;
        LunarSitePanelSettings panel = sitePanelSettings[siteIndex];
        return SolarPowerCalculator.TryEvaluateInstantaneousPower(
            site.Site, visibility, panel.panelParallelToSurface,
            panel.fixedPanelAzimuthDegrees, panel.fixedPanelTiltDegrees,
            panel.panelAreaSquareMeters, panel.panelEfficiency,
            nominalSolarIrradianceWPerM2AtAu,
            out power, out error);
    }

    private bool TryGetSettings(out LunarHorizonSettings settings, out string error)
    {
        settings = new LunarHorizonSettings
        {
            azimuthStepDegrees = azimuthStepDegrees,
            radialStepMeters = radialStepMeters,
            maximumRangeMeters = maximumRangeKm * 1000.0
        };
        return LunarHorizonCalculator.ValidateSettings(settings, out error);
    }

    private void ResolveReferences()
    {
        if (locationController == null)
            locationController = FindAnyObjectByType<LunarLocationController>();
        if (demReader == null)
            demReader = FindAnyObjectByType<LunarDEMReader>();
        if (simulationTime == null)
            simulationTime = FindAnyObjectByType<SimulationTime>();
        if (ephemerisProvider == null)
            ephemerisProvider = simulationTime != null
                ? simulationTime.DataProvider
                : FindAnyObjectByType<EphemerisDataProvider>();
    }

    public void DrawSiteHorizonPlot(int siteIndex, Rect rect)
    {
        if (siteIndex < 0 || siteIndex >= _siteStates.Length)
            return;
        DrawHorizonPlot(siteIndex, rect);
    }

    private void CancelHorizonBuild(int siteIndex)
    {
        SiteRuntimeState site = _siteStates[siteIndex];
        if (site.HorizonRoutine == null)
            return;
        StopCoroutine(site.HorizonRoutine);
        site.HorizonRoutine = null;
    }

    private void DrawHorizonPlot(int siteIndex, Rect rect)
    {
        GUI.color = new Color(0.035f, 0.055f, 0.08f, 0.94f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        float left = rect.x + 34f;
        float right = rect.xMax - 12f;
        float top = rect.y + 10f;
        float bottom = rect.yMax - 23f;
        float graphHeight = bottom - top;
        if (right <= left || graphHeight <= 0f)
            return;

        SiteRuntimeState site = _siteStates[siteIndex];
        if (!site.HorizonReady || site.Horizon == null)
        {
            string message = !string.IsNullOrEmpty(site.CalculationError)
                ? site.CalculationError
                : site.HasSite
                    ? site.Status + " " + site.HorizonProgress.ToString("P0", CultureInfo.InvariantCulture)
                    : "Select a site to build its terrain horizon.";
            GUI.Label(new Rect(left + 8f, top + 8f, right - left - 16f, graphHeight - 16f), message);
            return;
        }

        double maxY = 15.0;
        maxY = Math.Max(maxY, site.Horizon.MaximumElevationDegrees + 5.0);
        if (site.HasLatestResult)
            maxY = Math.Max(maxY, Math.Max(site.LatestVisibility.elevationSunDeg,
                site.LatestVisibility.elevationEarthDeg) + 4.0);
        maxY = Math.Min(90.0, maxY);
        const double minY = -15.0;

        float MapY(double degrees)
        {
            double t = (degrees - minY) / (maxY - minY);
            return bottom - (float)Math.Max(0.0, Math.Min(1.0, t)) * graphHeight;
        }

        float zeroY = MapY(0.0);
        DrawLine(new Vector2(left, top), new Vector2(left, bottom), new Color(0.55f, 0.62f, 0.69f, 0.9f), 1.2f);
        DrawLine(new Vector2(left, zeroY), new Vector2(right, zeroY), new Color(0.55f, 0.62f, 0.69f, 0.65f), 1.0f);
        for (int tick = 0; tick <= 4; tick++)
        {
            double azimuth = tick * 90.0;
            float x = Mathf.Lerp(left, right, (float)(azimuth / 360.0));
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), new Color(0.33f, 0.4f, 0.48f, 0.28f), 1.0f);
            GUI.Label(new Rect(x - 17f, bottom + 2f, 40f, 18f), azimuth.ToString("F0", CultureInfo.InvariantCulture) + "°");
        }
        GUI.Label(new Rect(rect.x + 2f, top - 5f, 32f, 18f), maxY.ToString("F0", CultureInfo.InvariantCulture) + "°");
        GUI.Label(new Rect(rect.x + 2f, bottom - 8f, 32f, 18f), minY.ToString("F0", CultureInfo.InvariantCulture) + "°");

        double[] horizon = site.Horizon.ElevationDegreesByAzimuth;
        for (int i = 0; i < horizon.Length; i++)
        {
            int next = (i + 1) % horizon.Length;
            float x0 = Mathf.Lerp(left, right, (float)i / horizon.Length);
            float x1 = Mathf.Lerp(left, right, (float)(i + 1) / horizon.Length);
            LunarTimeSeriesPlot.DrawConnectedLine(
                new Vector2(x0, MapY(horizon[i])), new Vector2(x1, MapY(horizon[next])),
                HorizonColors[siteIndex], 2.0f);
        }

        if (!site.HasLatestResult)
            return;
        LunarVisibilityResult result = site.LatestVisibility;
        DrawTarget(result.azimuthSunDeg, result.elevationSunDeg, left, right, zeroY, MapY,
            SunColors[siteIndex], result.sunVisible);
        DrawTarget(result.azimuthEarthDeg, result.elevationEarthDeg, left, right, zeroY, MapY,
            EarthColors[siteIndex], result.earthVisible);
    }

    private static void DrawTarget(
        double azimuthDegrees,
        double elevationDegrees,
        float left,
        float right,
        float zeroY,
        Func<double, float> mapY,
        Color color,
        bool visible)
    {
        float x = Mathf.Lerp(left, right,
            (float)(LunarVectorMath.WrapDegrees(azimuthDegrees) / 360.0));
        float y = mapY(elevationDegrees);
        DrawLine(new Vector2(x, zeroY), new Vector2(x, y), color, 2.0f);
        GUI.color = color;
        GUI.DrawTexture(new Rect(x - 4f, y - 4f, 8f, 8f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        if (!visible)
        {
            Color blockedColor = new Color(0.98f, 0.28f, 0.25f, 1f);
            DrawLine(new Vector2(x - 4f, y - 4f), new Vector2(x + 4f, y + 4f), blockedColor, 1.5f);
            DrawLine(new Vector2(x - 4f, y + 4f), new Vector2(x + 4f, y - 4f), blockedColor, 1.5f);
        }
    }

    private static void DrawLine(Vector2 start, Vector2 end, Color color, float thickness)
    {
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length <= 0.1f)
            return;

        Matrix4x4 oldMatrix = GUI.matrix;
        Vector2 middle = (start + end) * 0.5f;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, middle);
        GUI.color = color;
        GUI.DrawTexture(new Rect(middle.x - length * 0.5f, middle.y - thickness * 0.5f,
            length, thickness), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;
        GUI.color = Color.white;
    }

}
