using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Shared expandable UI stack for the landing sites, orbital clock, and time-series plot.
/// Location, calculation, and clock state remain owned by their domain components.
/// </summary>
public sealed class LunarSitePanelsUI : MonoBehaviour
{
    [Header("Project components")]
    [SerializeField] private LunarLocationController locationController;
    [SerializeField] private LunarVisibilityPresenter visibilityPresenter;
    [SerializeField] private SimulationTime simulationTime;

    [Header("Panel layout")]
    [SerializeField] private bool showPanels = true;
    [SerializeField] private Vector2 panelOrigin = new Vector2(12f, 12f);
    [SerializeField] private float panelWidth = 640f;
    [SerializeField] private float plotHeight = 150f;
    [SerializeField] private float timeSeriesPlotHeight = 260f;

    private sealed class SitePanelViewState
    {
        public bool Expanded;
        public bool UseSurfaceNormal = true;
        public string AreaInput = "1.00";
        public string EfficiencyInput = "20.0";
        public string PanelHeightInput = "2.0";
        public string TiltInput = "45.0";
        public string AzimuthInput = "180.0";
        public string PanelMessage = "";
    }

    private readonly SitePanelViewState[] _siteViews =
    {
        new SitePanelViewState { Expanded = true },
        new SitePanelViewState { Expanded = false }
    };

    private readonly LunarSitePlotHistory[] _plotHistories =
    {
        new LunarSitePlotHistory(),
        new LunarSitePlotHistory()
    };
    private readonly bool[] _plotSelectedSites = new bool[2];
    private readonly LunarLocationResult[] _plotLocations = new LunarLocationResult[2];
    private static readonly Color[] SiteBeaconColors =
    {
        new Color(1f, 0.55f, 0.2f, 1f),
        new Color(0.28f, 0.82f, 1f, 1f)
    };

    private Vector2 _scrollPosition;
    private float _currentPanelContentWidth = 420f;
    private bool _panelCollapsed;
    private bool _simulationPanelExpanded = true;
    private bool _plotPanelExpanded = true;
    private bool _plotVariableMenuOpen;
    private int _selectedPlotVariable;
    private string _plotMessage = "";
    private GUIStyle _headerStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _rowLabelStyle;
    private GUIStyle _rowValueStyle;
    private GUIStyle _secondaryStyle;
    private GUIStyle _beaconLabelStyle;

    private void Start()
    {
        ResolveReferences();
        if (locationController == null || visibilityPresenter == null || simulationTime == null)
        {
            Debug.LogError("LunarSitePanelsUI requires LunarLocationController, LunarVisibilityPresenter, and SimulationTime.", this);
            return;
        }

        locationController.LocationSlotSelected += OnLocationSlotSelected;
        locationController.LocationSlotCleared += OnLocationSlotCleared;
        simulationTime.UtcTimeSet += OnUtcTimeSet;
        for (int siteIndex = 0; siteIndex < _siteViews.Length; siteIndex++)
            LoadPanelSettings(siteIndex);
    }

    private void OnDestroy()
    {
        if (locationController != null)
        {
            locationController.LocationSlotSelected -= OnLocationSlotSelected;
            locationController.LocationSlotCleared -= OnLocationSlotCleared;
        }
        if (simulationTime != null)
            simulationTime.UtcTimeSet -= OnUtcTimeSet;
    }

    public bool IsPointerOverPanel()
    {
        if (!showPanels)
            return false;

        Vector3 mouse = Input.mousePosition;
        Vector2 guiPoint = new Vector2(mouse.x, Screen.height - mouse.y);
        Rect interactiveRect = _panelCollapsed ? GetCollapsedPanelRect() : GetPanelRect();
        return interactiveRect.Contains(guiPoint);
    }

    private void ResolveReferences()
    {
        if (locationController == null)
            locationController = FindAnyObjectByType<LunarLocationController>();
        if (visibilityPresenter == null)
            visibilityPresenter = FindAnyObjectByType<LunarVisibilityPresenter>();
        if (simulationTime == null)
            simulationTime = FindAnyObjectByType<SimulationTime>();
    }

    private void LateUpdate()
    {
        if (simulationTime == null || locationController == null || visibilityPresenter == null ||
            !simulationTime.TryGetCurrentState(out EphemerisState state))
            return;

        for (int siteIndex = 0; siteIndex < _plotHistories.Length; siteIndex++)
        {
            bool selected = locationController.TryGetSelectedLocation(siteIndex, out _plotLocations[siteIndex]);
            visibilityPresenter.TryGetSitePanelReadout(siteIndex, out LunarSitePanelReadout readout);
            _plotHistories[siteIndex].Observe(state.UtcDateTime, readout, selected);
        }
    }

    private void OnGUI()
    {
        if (!showPanels || locationController == null || visibilityPresenter == null || simulationTime == null)
            return;

        EnsureStyles();
        Rect panelRect = GetPanelRect();
        DrawSiteBeaconMarkers();
        if (_panelCollapsed)
        {
            if (GUI.Button(GetCollapsedPanelRect(), new GUIContent(">", "Show lunar site panels")))
                _panelCollapsed = false;
            return;
        }

        _currentPanelContentWidth = Mathf.Max(200f, panelRect.width - 30f);
        GUILayout.BeginArea(panelRect, GUI.skin.box);
        GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Height(30f));
        GUILayout.Label("LUNAR SITE ANALYSIS", _sectionStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(new GUIContent("<", "Collapse panels to the left"),
                GUILayout.Width(30f), GUILayout.Height(26f)))
            _panelCollapsed = true;
        GUILayout.EndHorizontal();

        _scrollPosition = GUILayout.BeginScrollView(
            _scrollPosition,
            false,
            true,
            GUILayout.Width(panelRect.width - 12f),
            GUILayout.Height(panelRect.height - 48f));

        for (int siteIndex = 0; siteIndex < _siteViews.Length; siteIndex++)
        {
            DrawSitePanel(siteIndex);
            GUILayout.Space(8f);
        }

        DrawSimulationPanel();
        GUILayout.Space(8f);
        DrawPlotPanel();

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawSiteBeaconMarkers()
    {
        if (Event.current.type != EventType.Repaint || locationController == null)
            return;

        Camera targetCamera = Camera.main;
        LunarReferenceFrame referenceFrame = locationController.ReferenceFrame;
        if (targetCamera == null || referenceFrame == null)
            return;

        Transform moonRoot = referenceFrame.MoonRoot;
        for (int siteIndex = 0; siteIndex < _plotLocations.Length; siteIndex++)
        {
            if (!locationController.TryGetSelectedLocation(siteIndex, out LunarLocationResult location))
                continue;

            Vector3 worldPosition = referenceFrame.MoonLocalToWorld(location.moonLocalPosition);
            Vector3 outward = worldPosition - moonRoot.position;
            Vector3 towardCamera = targetCamera.transform.position - worldPosition;
            if (outward.sqrMagnitude <= 1e-10f || towardCamera.sqrMagnitude <= 1e-10f ||
                Vector3.Dot(outward.normalized, towardCamera.normalized) <= 0f)
                continue;

            Vector3 screenPosition = targetCamera.WorldToScreenPoint(worldPosition);
            if (screenPosition.z <= 0f || screenPosition.x < 0f || screenPosition.x > Screen.width ||
                screenPosition.y < 0f || screenPosition.y > Screen.height)
                continue;

            Vector2 guiPosition = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            DrawSiteBeacon(siteIndex, guiPosition);
        }
    }

    private void DrawSiteBeacon(int siteIndex, Vector2 position)
    {
        Color previousColor = GUI.color;
        Color color = SiteBeaconColors[siteIndex];
        GUI.color = new Color(color.r, color.g, color.b, 0.22f);
        GUI.DrawTexture(new Rect(position.x - 7f, position.y - 17f, 14f, 14f), Texture2D.whiteTexture);

        GUI.color = color;
        GUI.DrawTexture(new Rect(position.x - 2.5f, position.y - 12.5f, 5f, 5f), Texture2D.whiteTexture);
        DrawScreenLine(new Vector2(position.x, position.y - 7f),
            new Vector2(position.x, position.y + 7f), color, 2f);
        DrawScreenLine(new Vector2(position.x - 5f, position.y + 7f),
            new Vector2(position.x + 5f, position.y + 7f), color, 2f);
        GUI.Label(new Rect(position.x + 7f, position.y - 17f, 20f, 18f),
            siteIndex == 0 ? "A" : "B", _beaconLabelStyle);
        GUI.color = previousColor;
    }

    private static void DrawScreenLine(Vector2 start, Vector2 end, Color color, float thickness)
    {
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length <= 0.1f)
            return;

        Matrix4x4 previousMatrix = GUI.matrix;
        Vector2 middle = (start + end) * 0.5f;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, middle);
        GUI.color = color;
        GUI.DrawTexture(new Rect(middle.x - length * 0.5f, middle.y - thickness * 0.5f,
            length, thickness), Texture2D.whiteTexture);
        GUI.matrix = previousMatrix;
    }

    private Rect GetCollapsedPanelRect()
    {
        const float width = 34f;
        const float height = 56f;
        float x = Mathf.Clamp(panelOrigin.x, 0f, Mathf.Max(0f, Screen.width - width));
        float y = Mathf.Clamp(panelOrigin.y, 0f, Mathf.Max(0f, Screen.height - height));
        return new Rect(x, y, width, height);
    }

    private void OnLocationSlotSelected(int siteIndex, LunarLocationResult location)
    {
        if (siteIndex >= 0 && siteIndex < _plotHistories.Length)
        {
            _plotHistories[siteIndex].Clear();
            _plotMessage = "Plot history reset because the site location changed.";
        }
    }

    private void OnLocationSlotCleared(int siteIndex)
    {
        if (siteIndex >= 0 && siteIndex < _plotHistories.Length)
        {
            _plotHistories[siteIndex].Clear();
            _plotMessage = "Cleared plot data for site " + (char)('A' + siteIndex) + ".";
        }
    }

    private void OnUtcTimeSet()
    {
        for (int siteIndex = 0; siteIndex < _plotHistories.Length; siteIndex++)
            _plotHistories[siteIndex].Clear();
        _plotMessage = "All plot histories reset at the selected UTC time.";
    }

    private void DrawSitePanel(int siteIndex)
    {
        SitePanelViewState view = _siteViews[siteIndex];
        bool hasLocation = locationController.TryGetSelectedLocation(siteIndex, out _);
        bool isClickTarget = locationController.ActiveClickSiteIndex == siteIndex;
        char siteLabel = (char)('A' + siteIndex);
        string selectionStatus = hasLocation ? "LOCATION SET" : "NOT SELECTED";
        string clickStatus = isClickTarget ? "  |  ACTIVE FOR MOON CLICKS" : "";
        string title = (view.Expanded ? "▼  " : "▶  ") +
                       "LUNAR LANDING SITE " + siteLabel + "   |   " + selectionStatus + clickStatus;

        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(_currentPanelContentWidth));
        if (GUILayout.Button(title, _headerStyle, GUILayout.Height(34f)))
            view.Expanded = !view.Expanded;

        if (view.Expanded)
            DrawExpandedSitePanel(siteIndex, view, hasLocation, isClickTarget);
        GUILayout.EndVertical();
    }

    private void DrawSimulationPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(_currentPanelContentWidth));
        _simulationPanelExpanded = DrawPanelHeader("ORBITAL SIMULATION", _simulationPanelExpanded);
        if (_simulationPanelExpanded)
        {
            GUILayout.Space(5f);
            simulationTime.DrawControls();
        }
        GUILayout.EndVertical();
    }

    private void DrawPlotPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(_currentPanelContentWidth));
        _plotPanelExpanded = DrawPanelHeader("TIME SERIES PLOT", _plotPanelExpanded);
        if (!_plotPanelExpanded)
        {
            GUILayout.EndVertical();
            return;
        }

        GUILayout.Space(5f);
        DrawPlotVariableDropdown();
        RefreshPlotSelectionState();

        GUILayout.BeginHorizontal();
        if (_plotSelectedSites[0])
            GUILayout.Label("Site A: orange", _secondaryStyle, GUILayout.ExpandWidth(false));
        if (_plotSelectedSites[1])
            GUILayout.Label("Site B: cyan", _secondaryStyle, GUILayout.ExpandWidth(false));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        Rect plotRect = GUILayoutUtility.GetRect(1f, timeSeriesPlotHeight, GUILayout.ExpandWidth(true));
        LunarTimeSeriesPlot.Draw(plotRect, (LunarPlotVariable)_selectedPlotVariable,
            _plotHistories, _plotSelectedSites);

        bool hasExportData = false;
        for (int siteIndex = 0; siteIndex < _plotHistories.Length; siteIndex++)
            hasExportData |= _plotSelectedSites[siteIndex] && _plotHistories[siteIndex].Points.Count > 0;

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && hasExportData;
        if (GUILayout.Button("Download displayed data (CSV)", GUILayout.Height(28f)))
            DownloadPlotCsv();
        GUI.enabled = previousEnabled;

        GUILayout.Label("Hourly UTC samples interpolated between simulation updates · selected sites only.", _secondaryStyle);
        if (!string.IsNullOrEmpty(_plotMessage))
            GUILayout.Label(_plotMessage, _secondaryStyle);
        GUILayout.EndVertical();
    }

    private bool DrawPanelHeader(string title, bool expanded)
    {
        string indicator = expanded ? "▼  " : "▶  ";
        return GUILayout.Button(indicator + title, _headerStyle, GUILayout.Height(34f)) ? !expanded : expanded;
    }

    private void DrawPlotVariableDropdown()
    {
        LunarPlotVariable selectedVariable = (LunarPlotVariable)_selectedPlotVariable;
        if (GUILayout.Button(new GUIContent(
                LunarPlotVariableInfo.GetLabel(selectedVariable) + "  ▼",
                LunarPlotVariableInfo.GetTooltip(selectedVariable)), GUILayout.Height(30f)))
            _plotVariableMenuOpen = !_plotVariableMenuOpen;

        if (_plotVariableMenuOpen)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            for (int optionIndex = 0; optionIndex < LunarPlotVariableInfo.Count; optionIndex++)
            {
                GUIContent option = LunarPlotVariableInfo.GetOption(optionIndex);
                if (GUILayout.Button(option, GUILayout.Height(25f)))
                {
                    _selectedPlotVariable = optionIndex;
                    _plotVariableMenuOpen = false;
                }
            }
            GUILayout.EndVertical();
        }

        string tooltip = string.IsNullOrEmpty(GUI.tooltip)
            ? LunarPlotVariableInfo.GetTooltip((LunarPlotVariable)_selectedPlotVariable)
            : GUI.tooltip;
        GUILayout.Label(tooltip, _secondaryStyle);
    }

    private void RefreshPlotSelectionState()
    {
        for (int siteIndex = 0; siteIndex < _plotHistories.Length; siteIndex++)
        {
            _plotSelectedSites[siteIndex] = locationController.TryGetSelectedLocation(
                siteIndex, out _plotLocations[siteIndex]);
        }
    }

    private void DownloadPlotCsv()
    {
        RefreshPlotSelectionState();
        LunarPlotVariable variable = (LunarPlotVariable)_selectedPlotVariable;
        StringBuilder siteTokens = new StringBuilder();
        for (int siteIndex = 0; siteIndex < _plotHistories.Length; siteIndex++)
        {
            if (!_plotSelectedSites[siteIndex] || _plotHistories[siteIndex].Points.Count == 0)
                continue;
            if (siteTokens.Length > 0)
                siteTokens.Append('_');
            LunarLocationResult location = _plotLocations[siteIndex];
            siteTokens.Append("site").Append((char)('A' + siteIndex))
                .Append("_lat").Append(FormatCoordinateToken(location.latitudeDeg))
                .Append("_lon").Append(FormatCoordinateToken(location.longitudeEastDeg));
        }

        if (siteTokens.Length == 0)
        {
            _plotMessage = "There is no selected-site plot data to export yet.";
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff'Z'", CultureInfo.InvariantCulture);
        string fileNameBase = LunarPlotVariableInfo.GetFileNameToken(variable) + "_" +
                              siteTokens.ToString() + "_" + timestamp;
        string fileName = EnsureCsvExtension(fileNameBase);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloadDirectory = string.IsNullOrEmpty(userProfile)
            ? Path.Combine(Application.persistentDataPath, "Downloads")
            : Path.Combine(userProfile, "Downloads");
        string path = Path.Combine(downloadDirectory, fileName);
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            string csv = LunarTimeSeriesPlot.BuildCsv(
                variable, _plotHistories, _plotSelectedSites, _plotLocations);
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            _plotMessage = "CSV saved to " + path;
            Debug.Log("Lunar plot CSV saved: " + path, this);
        }
        catch (Exception exception)
        {
            _plotMessage = "Could not save plot CSV: " + exception.Message;
            Debug.LogError(_plotMessage, this);
        }
    }

    private static string FormatCoordinateToken(double value)
        => value.ToString("F6", CultureInfo.InvariantCulture)
            .Replace("-", "m")
            .Replace(".", "p");

    private static string EnsureCsvExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        while (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.GetFileNameWithoutExtension(fileName);
            extension = Path.GetExtension(fileName);
        }

        return fileName + ".csv";
    }

    private void DrawExpandedSitePanel(
        int siteIndex,
        SitePanelViewState view,
        bool hasLocation,
        bool isClickTarget)
    {
        GUILayout.Space(5f);
        DrawCoordinateInputs(siteIndex);
        DrawSiteActions(siteIndex, hasLocation, isClickTarget);
        DrawPanelSettings(siteIndex, view);
        DrawSiteMeasurements(siteIndex, hasLocation);
    }

    private void DrawCoordinateInputs(int siteIndex)
    {
        locationController.TryGetCoordinateInputs(siteIndex, out string latitude, out string longitude);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Latitude (deg)", GUILayout.Width(88f));
        latitude = GUILayout.TextField(latitude, GUILayout.MinWidth(70f), GUILayout.ExpandWidth(true));
        GUILayout.Label("Longitude E (deg)", GUILayout.Width(108f));
        longitude = GUILayout.TextField(longitude, GUILayout.MinWidth(70f), GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
        locationController.SetCoordinateInputs(siteIndex, latitude, longitude);

        if (GUILayout.Button("Place marker from coordinates", GUILayout.Height(27f)))
        {
            if (locationController.TryPlaceFromCoordinateInputs(
                    siteIndex, out LunarLocationResult ignored, out string error))
            {
                _siteViews[siteIndex].PanelMessage = "Site placed from the entered coordinates.";
            }
            else
            {
                _siteViews[siteIndex].PanelMessage = "";
                locationController.SetSiteInputError(siteIndex, error);
            }
        }

        string locationError = locationController.GetSiteInputError(siteIndex);
        if (!string.IsNullOrEmpty(locationError))
            GUILayout.Label(locationError, GUI.skin.box);
    }

    private void DrawSiteActions(int siteIndex, bool hasLocation, bool isClickTarget)
    {
        GUILayout.BeginHorizontal();
        if (isClickTarget)
        {
            GUILayout.Label("Active for Moon clicks", _secondaryStyle, GUILayout.Width(142f));
        }
        else if (GUILayout.Button("Use for Moon clicks", GUILayout.Height(27f), GUILayout.Width(142f)))
        {
            locationController.SetActiveClickSite(siteIndex);
        }

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && hasLocation;
        string focusLabel = locationController.IsTrackingSite(siteIndex)
            ? "Stop tracking"
            : "Focus and track";
        if (GUILayout.Button(focusLabel, GUILayout.Height(27f), GUILayout.ExpandWidth(true)))
            locationController.TryFocusSelectedLocation(siteIndex);
        GUI.enabled = previousEnabled;

        Color previousColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.92f, 0.77f, 0.77f);
        if (GUILayout.Button("Clear site", GUILayout.Height(27f), GUILayout.Width(86f)))
        {
            locationController.ClearSite(siteIndex);
            locationController.ClearSiteInputError(siteIndex);
            _siteViews[siteIndex].PanelMessage = "Site cleared. Panel settings were kept.";
        }
        GUI.backgroundColor = previousColor;
        GUILayout.EndHorizontal();
    }

    private void DrawPanelSettings(int siteIndex, SitePanelViewState view)
    {
        if (!visibilityPresenter.TryGetSitePanelSettings(siteIndex, out LunarSitePanelSettings settings))
            return;

        DrawSectionHeading("SOLAR PANEL PARAMETERS");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Area (m2)", GUILayout.Width(74f));
        view.AreaInput = GUILayout.TextField(view.AreaInput, GUILayout.MinWidth(60f), GUILayout.ExpandWidth(true));
        GUILayout.Label("Efficiency (%)", GUILayout.Width(92f));
        view.EfficiencyInput = GUILayout.TextField(view.EfficiencyInput, GUILayout.MinWidth(55f), GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();

        view.UseSurfaceNormal = GUILayout.Toggle(view.UseSurfaceNormal, "Panel parallel to LOLA surface normal");
        if (!view.UseSurfaceNormal)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Tilt (deg)", GUILayout.Width(74f));
            view.TiltInput = GUILayout.TextField(view.TiltInput, GUILayout.MinWidth(55f), GUILayout.ExpandWidth(true));
            GUILayout.Label("Azimuth (deg)", GUILayout.Width(104f));
            view.AzimuthInput = GUILayout.TextField(view.AzimuthInput, GUILayout.MinWidth(55f), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Panel height above surface (m)", GUILayout.Width(268f));
        view.PanelHeightInput = GUILayout.TextField(view.PanelHeightInput, GUILayout.MinWidth(55f), GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Apply panel", GUILayout.Height(29f)))
            ApplyPanelSettings(siteIndex, view, settings);

        if (!string.IsNullOrEmpty(view.PanelMessage))
            GUILayout.Label(view.PanelMessage, _secondaryStyle);
    }

    private void ApplyPanelSettings(
        int siteIndex,
        SitePanelViewState view,
        LunarSitePanelSettings previousSettings)
    {
        if (!TryParseInput(view.AreaInput, out double area) ||
            !TryParseInput(view.EfficiencyInput, out double efficiencyPercent) ||
            !TryParseInput(view.PanelHeightInput, out double panelHeight))
        {
            view.PanelMessage = "Enter finite numbers for area, efficiency, and panel height.";
            return;
        }

        double tilt = previousSettings.fixedPanelTiltDegrees;
        double azimuth = previousSettings.fixedPanelAzimuthDegrees;
        if (!view.UseSurfaceNormal &&
            (!TryParseInput(view.TiltInput, out tilt) || !TryParseInput(view.AzimuthInput, out azimuth)))
        {
            view.PanelMessage = "Enter finite numbers for panel tilt and azimuth.";
            return;
        }

        LunarSitePanelSettings nextSettings = new LunarSitePanelSettings
        {
            panelAreaSquareMeters = (float)area,
            panelEfficiency = (float)(efficiencyPercent / 100.0),
            panelParallelToSurface = view.UseSurfaceNormal,
            fixedPanelTiltDegrees = (float)tilt,
            fixedPanelAzimuthDegrees = (float)azimuth,
            panelHeightMeters = (float)panelHeight
        };

        if (!visibilityPresenter.TryApplySitePanelSettings(
                siteIndex, nextSettings, out bool horizonRebuilt, out string error))
        {
            view.PanelMessage = error;
            return;
        }

        visibilityPresenter.TryGetSitePanelSettings(siteIndex, out LunarSitePanelSettings appliedSettings);
        view.AreaInput = appliedSettings.panelAreaSquareMeters.ToString("F2", CultureInfo.InvariantCulture);
        view.EfficiencyInput = (appliedSettings.panelEfficiency * 100f).ToString("F1", CultureInfo.InvariantCulture);
        view.PanelHeightInput = appliedSettings.panelHeightMeters.ToString("F1", CultureInfo.InvariantCulture);
        view.TiltInput = appliedSettings.fixedPanelTiltDegrees.ToString("F1", CultureInfo.InvariantCulture);
        view.AzimuthInput = appliedSettings.fixedPanelAzimuthDegrees.ToString("F1", CultureInfo.InvariantCulture);
        view.UseSurfaceNormal = appliedSettings.panelParallelToSurface;
        view.PanelMessage = horizonRebuilt
            ? "Applied. Rebuilding this site's horizon for the new height; energy reset."
            : "Applied. This site's cumulative energy was reset.";
    }

    private void DrawSiteMeasurements(int siteIndex, bool hasLocation)
    {
        visibilityPresenter.TryGetSitePanelReadout(siteIndex, out LunarSitePanelReadout readout);
        locationController.TryGetSelectedLocation(siteIndex, out LunarLocationResult location);

        DrawSectionHeading("SITE INFORMATION AND MEASUREMENTS");
        DrawMetric("LOLA elevation", hasLocation ? FormatMeters(location.elevationMeters) : "--");
        DrawMetric("Radius", hasLocation ? FormatMeters(location.radiusMeters) : "--");
        DrawMetric("Moon-local XYZ", hasLocation ? FormatMoonPosition(location.moonLocalPosition) : "--");
        DrawMetric("Surface slope", readout.hasSite ? FormatDegrees(readout.surfaceSite.SlopeDegrees) : "--");
        DrawMetric("Horizon build", FormatHorizonStatus(readout, hasLocation));
        if (!string.IsNullOrEmpty(readout.calculationError))
            GUILayout.Label(readout.calculationError, GUI.skin.box);

        DrawSectionHeading("SUN");
        DrawMetric("Azimuth", readout.hasLatestResult ? FormatDegrees(readout.visibility.azimuthSunDeg) : "--");
        DrawMetric("Elevation", readout.hasLatestResult ? FormatDegrees(readout.visibility.elevationSunDeg) : "--");
        DrawMetric("Terrain horizon", readout.hasLatestResult ? FormatDegrees(readout.visibility.horizonSunDeg) : "--");
        DrawMetric("Disc visibility", readout.hasLatestResult ? readout.visibility.sunVisibleFraction.ToString("P1", CultureInfo.InvariantCulture) : "--");

        DrawSectionHeading("EARTH");
        DrawMetric("Azimuth", readout.hasLatestResult ? FormatDegrees(readout.visibility.azimuthEarthDeg) : "--");
        DrawMetric("Elevation", readout.hasLatestResult ? FormatDegrees(readout.visibility.elevationEarthDeg) : "--");
        DrawMetric("Terrain horizon", readout.hasLatestResult ? FormatDegrees(readout.visibility.horizonEarthDeg) : "--");
        DrawMetric("Disc visibility (binary LOS)", readout.hasLatestResult
            ? readout.visibility.earthVisible ? "Visible" : "Blocked"
            : "--");

        DrawSectionHeading("SOLAR OUTPUT");
        DrawMetric("Power", readout.hasLatestResult ? FormatWatts(readout.power.powerWatts) : "--");
        DrawMetric("Irradiance", readout.hasLatestResult ? FormatIrradiance(readout.power.irradianceWPerSquareMeter) : "--");
        DrawMetric("Incidence", readout.hasLatestResult ? readout.power.incidenceFactor.ToString("F3", CultureInfo.InvariantCulture) : "--");
        DrawMetric("Cumulative energy", readout.hasLatestResult
            ? (readout.accumulatedEnergyWh / 1000.0).ToString("F6", CultureInfo.InvariantCulture) + " kWh"
            : "--");

        DrawSectionHeading(siteIndex == 0 ? "SITE A HORIZON PLOT" : "SITE B HORIZON PLOT");
        GUILayout.Label(GetPlotLegend(siteIndex), _secondaryStyle);
        Rect plotRect = GUILayoutUtility.GetRect(1f, plotHeight, GUILayout.ExpandWidth(true));
        visibilityPresenter.DrawSiteHorizonPlot(siteIndex, plotRect);
    }

    private void DrawSectionHeading(string text)
    {
        GUILayout.Space(5f);
        GUILayout.Label(text, _sectionStyle);
    }

    private void DrawMetric(string label, string value)
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label(label, _rowLabelStyle, GUILayout.Width(172f));
        GUILayout.Label(value, _rowValueStyle, GUILayout.ExpandWidth(true));
        GUILayout.EndHorizontal();
    }

    private static string FormatHorizonStatus(LunarSitePanelReadout readout, bool hasLocation)
    {
        if (!hasLocation)
            return "Not selected";
        if (!string.IsNullOrEmpty(readout.calculationError))
            return "Needs attention";
        if (readout.horizonReady)
            return string.Format(CultureInfo.InvariantCulture, "Ready - {0:N0} terrain samples", readout.horizonSamples);
        if (!readout.hasSite)
            return string.IsNullOrEmpty(readout.status) ? "Building site geometry" : readout.status;
        return string.Format(CultureInfo.InvariantCulture,
            "Building {0:P0} - {1:N0} samples", readout.horizonProgress, readout.horizonSamples);
    }

    private static string GetPlotLegend(int siteIndex)
        => siteIndex == 0
            ? "Orange horizon  |  yellow Sun  |  blue Earth  |  red X = terrain blocked"
            : "Orange horizon  |  yellow Sun  |  blue Earth  |  red X = terrain blocked";

    private static string FormatMeters(double meters)
        => meters.ToString("N2", CultureInfo.InvariantCulture) + " m";

    private static string FormatDegrees(double degrees)
        => degrees.ToString("F2", CultureInfo.InvariantCulture) + " deg";

    private static string FormatWatts(double watts)
        => watts.ToString("F3", CultureInfo.InvariantCulture) + " W";

    private static string FormatIrradiance(double irradiance)
        => irradiance.ToString("F2", CultureInfo.InvariantCulture) + " W/m2";

    private static string FormatMoonPosition(Vector3 position)
        => string.Format(CultureInfo.InvariantCulture, "({0:F5}, {1:F5}, {2:F5})", position.x, position.y, position.z);

    private static bool TryParseInput(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               LunarCoordinates.IsFinite(value);
    }

    private void LoadPanelSettings(int siteIndex)
    {
        if (!visibilityPresenter.TryGetSitePanelSettings(siteIndex, out LunarSitePanelSettings settings))
            return;
        SitePanelViewState view = _siteViews[siteIndex];
        view.AreaInput = settings.panelAreaSquareMeters.ToString("F2", CultureInfo.InvariantCulture);
        view.EfficiencyInput = (settings.panelEfficiency * 100f).ToString("F1", CultureInfo.InvariantCulture);
        view.PanelHeightInput = settings.panelHeightMeters.ToString("F1", CultureInfo.InvariantCulture);
        view.TiltInput = settings.fixedPanelTiltDegrees.ToString("F1", CultureInfo.InvariantCulture);
        view.AzimuthInput = settings.fixedPanelAzimuthDegrees.ToString("F1", CultureInfo.InvariantCulture);
        view.UseSurfaceNormal = settings.panelParallelToSurface;
    }

    private Rect GetPanelRect()
    {
        float width = Mathf.Min(panelWidth, Mathf.Max(200f, Screen.width - panelOrigin.x - 12f));
        float height = Mathf.Max(160f, Screen.height - panelOrigin.y - 12f);
        return new Rect(panelOrigin.x, panelOrigin.y, width, height);
    }

    private void EnsureStyles()
    {
        if (_headerStyle != null)
            return;

        _headerStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(10, 8, 5, 5)
        };
        _sectionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(2, 0, 3, 1)
        };
        _sectionStyle.normal.textColor = new Color(0.2f, 0.78f, 0.84f);
        _rowLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            padding = new RectOffset(5, 2, 2, 2)
        };
        _rowValueStyle = new GUIStyle(GUI.skin.label)
        {
            wordWrap = true,
            padding = new RectOffset(3, 5, 2, 2)
        };
        _secondaryStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            wordWrap = true,
            padding = new RectOffset(4, 3, 3, 3)
        };
        _secondaryStyle.normal.textColor = new Color(0.72f, 0.79f, 0.84f);
        _beaconLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
    }
}
