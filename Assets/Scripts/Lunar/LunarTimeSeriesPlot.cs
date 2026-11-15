using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

public enum LunarPlotVariable
{
    SunAzimuth,
    SunElevation,
    SunHorizon,
    SunDiscVisibilityPercent,
    EarthAzimuth,
    EarthElevation,
    EarthHorizon,
    EarthDiscVisibility,
    PowerWatts,
    Irradiance,
    Incidence,
    CumulativeEnergyKWh
}

public static class LunarPlotVariableInfo
{
    private static readonly GUIContent[] Options =
    {
        new GUIContent("Sun azimuth", "Local compass bearing of the Sun, in degrees."),
        new GUIContent("Sun elevation", "Sun centre angle above the local lunar horizon, in degrees."),
        new GUIContent("Sun horizon", "Terrain horizon elevation in the Sun's azimuth direction, in degrees."),
        new GUIContent("Sun disc visibility (%)", "Estimated percentage of the apparent solar disc unobstructed by terrain."),
        new GUIContent("Earth azimuth", "Local compass bearing of Earth, in degrees."),
        new GUIContent("Earth elevation", "Earth centre angle above the local lunar horizon, in degrees."),
        new GUIContent("Earth horizon", "Terrain horizon elevation in Earth's azimuth direction, in degrees."),
        new GUIContent("Earth disc visibility (0/1)", "1 when any part of Earth's apparent disc clears terrain; otherwise 0."),
        new GUIContent("Power", "Instantaneous electrical output for this site's solar panel, in watts."),
        new GUIContent("Irradiance", "Direct solar irradiance at the panel before area and efficiency, in W/m²."),
        new GUIContent("Incidence", "Panel orientation factor from 0 (edge-on) to 1 (directly facing the Sun)."),
        new GUIContent("Cumulative energy", "Integrated solar energy since the latest UTC reset, in kWh.")
    };

    public static int Count => Options.Length;

    public static GUIContent GetOption(int index)
        => Options[Mathf.Clamp(index, 0, Options.Length - 1)];

    public static string GetLabel(LunarPlotVariable variable)
        => GetOption((int)variable).text;

    public static string GetTooltip(LunarPlotVariable variable)
        => GetOption((int)variable).tooltip;

    public static string GetUnits(LunarPlotVariable variable)
    {
        switch (variable)
        {
            case LunarPlotVariable.SunAzimuth:
            case LunarPlotVariable.SunElevation:
            case LunarPlotVariable.SunHorizon:
            case LunarPlotVariable.EarthAzimuth:
            case LunarPlotVariable.EarthElevation:
            case LunarPlotVariable.EarthHorizon:
                return "deg";
            case LunarPlotVariable.SunDiscVisibilityPercent:
                return "%";
            case LunarPlotVariable.EarthDiscVisibility:
            case LunarPlotVariable.Incidence:
                return "0–1";
            case LunarPlotVariable.PowerWatts:
                return "W";
            case LunarPlotVariable.Irradiance:
                return "W/m²";
            case LunarPlotVariable.CumulativeEnergyKWh:
                return "kWh";
            default:
                return "";
        }
    }

    public static string GetFileNameToken(LunarPlotVariable variable)
    {
        switch (variable)
        {
            case LunarPlotVariable.SunAzimuth: return "sun_azimuth";
            case LunarPlotVariable.SunElevation: return "sun_elevation";
            case LunarPlotVariable.SunHorizon: return "sun_horizon";
            case LunarPlotVariable.SunDiscVisibilityPercent: return "sun_disc_visibility_percent";
            case LunarPlotVariable.EarthAzimuth: return "earth_azimuth";
            case LunarPlotVariable.EarthElevation: return "earth_elevation";
            case LunarPlotVariable.EarthHorizon: return "earth_horizon";
            case LunarPlotVariable.EarthDiscVisibility: return "earth_disc_visibility";
            case LunarPlotVariable.PowerWatts: return "power_w";
            case LunarPlotVariable.Irradiance: return "irradiance";
            case LunarPlotVariable.Incidence: return "incidence";
            case LunarPlotVariable.CumulativeEnergyKWh: return "cumulative_kwh";
            default: return "plot_data";
        }
    }
}

public struct LunarPlotPoint
{
    public DateTime utc;
    public double[] values;

    public LunarPlotPoint(DateTime utc, double[] values)
    {
        this.utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        this.values = values;
    }
}

/// <summary>Records hourly UTC samples interpolated between simulation state updates.</summary>
public sealed class LunarSitePlotHistory
{
    private const int MaximumPointCount = 24000;
    private const int TrimPointCount = 1000;
    private static readonly long TicksPerHour = TimeSpan.TicksPerHour;

    private readonly List<LunarPlotPoint> _points = new List<LunarPlotPoint>();
    private LunarPlotPoint _previousObservation;
    private DateTime _nextSampleUtc;
    private bool _hasPreviousObservation;

    public IReadOnlyList<LunarPlotPoint> Points => _points;

    public void Clear()
    {
        _points.Clear();
        _previousObservation = default;
        _nextSampleUtc = default;
        _hasPreviousObservation = false;
    }

    public void Observe(DateTime utc, LunarSitePanelReadout readout, bool selectedAndReady)
    {
        if (!selectedAndReady || !readout.hasLatestResult)
        {
            _hasPreviousObservation = false;
            return;
        }

        LunarPlotPoint current = new LunarPlotPoint(utc, ExtractValues(readout));
        if (!_hasPreviousObservation)
        {
            AddPoint(current);
            _previousObservation = current;
            _nextSampleUtc = NextUtcHour(current.utc);
            _hasPreviousObservation = true;
            return;
        }

        if (current.utc < _previousObservation.utc)
        {
            Clear();
            AddPoint(current);
            _previousObservation = current;
            _nextSampleUtc = NextUtcHour(current.utc);
            _hasPreviousObservation = true;
            return;
        }

        if (current.utc == _previousObservation.utc)
        {
            _previousObservation = current;
            return;
        }

        long intervalTicks = current.utc.Ticks - _previousObservation.utc.Ticks;
        while (_nextSampleUtc <= current.utc)
        {
            if (_nextSampleUtc > _previousObservation.utc)
            {
                double t = (double)(_nextSampleUtc.Ticks - _previousObservation.utc.Ticks) / intervalTicks;
                AddPoint(Interpolate(_previousObservation, current, _nextSampleUtc, t));
            }
            _nextSampleUtc = _nextSampleUtc.AddTicks(TicksPerHour);
        }

        _previousObservation = current;
    }

    private void AddPoint(LunarPlotPoint point)
    {
        _points.Add(point);
        if (_points.Count > MaximumPointCount)
            _points.RemoveRange(0, TrimPointCount);
    }

    private static DateTime NextUtcHour(DateTime utc)
    {
        long nextHour = (utc.Ticks / TicksPerHour + 1) * TicksPerHour;
        return new DateTime(nextHour, DateTimeKind.Utc);
    }

    private static double[] ExtractValues(LunarSitePanelReadout readout)
    {
        LunarVisibilityResult visibility = readout.visibility;
        SolarPowerResult power = readout.power;
        return new[]
        {
            visibility.azimuthSunDeg,
            visibility.elevationSunDeg,
            visibility.horizonSunDeg,
            visibility.sunVisibleFraction * 100.0,
            visibility.azimuthEarthDeg,
            visibility.elevationEarthDeg,
            visibility.horizonEarthDeg,
            visibility.earthVisible ? 1.0 : 0.0,
            power.powerWatts,
            power.irradianceWPerSquareMeter,
            power.incidenceFactor,
            readout.accumulatedEnergyWh / 1000.0
        };
    }

    private static LunarPlotPoint Interpolate(
        LunarPlotPoint from,
        LunarPlotPoint to,
        DateTime utc,
        double t)
    {
        double[] values = new double[LunarPlotVariableInfo.Count];
        for (int i = 0; i < values.Length; i++)
        {
            LunarPlotVariable variable = (LunarPlotVariable)i;
            double a = from.values[i];
            double b = to.values[i];
            if (variable == LunarPlotVariable.SunAzimuth || variable == LunarPlotVariable.EarthAzimuth)
            {
                double delta = LunarVectorMath.WrapDegrees(b - a + 180.0) - 180.0;
                values[i] = LunarVectorMath.WrapDegrees(a + delta * t);
            }
            else if (variable == LunarPlotVariable.EarthDiscVisibility)
            {
                values[i] = t < 0.5 ? a : b;
            }
            else
            {
                values[i] = a + (b - a) * t;
            }
        }
        return new LunarPlotPoint(utc, values);
    }
}

public static class LunarTimeSeriesPlot
{
    private const int MaximumRenderedPointsPerSite = 1200;
    private static readonly Color[] SiteColors =
    {
        new Color(1f, 0.58f, 0.22f, 1f),
        new Color(0.28f, 0.82f, 1f, 1f)
    };

    public static void Draw(
        Rect rect,
        LunarPlotVariable variable,
        LunarSitePlotHistory[] histories,
        bool[] selectedSites)
    {
        GUI.color = new Color(0.035f, 0.055f, 0.08f, 0.97f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        float left = rect.x + 62f;
        float right = rect.xMax - 12f;
        float top = rect.y + 16f;
        float bottom = rect.yMax - 42f;
        if (right <= left || bottom <= top)
            return;

        bool hasData = TryGetBounds(histories, selectedSites, variable, out long minTicks, out long maxTicks,
            out double minY, out double maxY);
        if (!hasData)
        {
            GUI.Label(new Rect(left + 8f, top + 8f, right - left - 16f, bottom - top - 16f),
                "No plot data yet. Select a site and allow its terrain horizon to finish building.");
            return;
        }

        if (maxTicks <= minTicks)
        {
            minTicks -= TimeSpan.TicksPerHour / 2;
            maxTicks += TimeSpan.TicksPerHour / 2;
        }
        SetDisplayRange(variable, ref minY, ref maxY);

        DrawLine(new Vector2(left, top), new Vector2(left, bottom), new Color(0.62f, 0.68f, 0.74f, 0.9f), 1.2f);
        DrawLine(new Vector2(left, bottom), new Vector2(right, bottom), new Color(0.62f, 0.68f, 0.74f, 0.9f), 1.2f);
        for (int i = 0; i <= 4; i++)
        {
            double t = i / 4.0;
            double value = maxY - (maxY - minY) * t;
            float y = Mathf.Lerp(top, bottom, (float)t);
            DrawLine(new Vector2(left, y), new Vector2(right, y), new Color(0.38f, 0.45f, 0.53f, 0.3f), 1f);
            GUI.Label(new Rect(rect.x + 2f, y - 9f, 54f, 18f), FormatAxisValue(value));
        }

        long tickStep = GetUtcTickStep(maxTicks - minTicks);
        long firstTick = (minTicks / tickStep + 1) * tickStep;
        for (long tick = firstTick; tick < maxTicks; tick += tickStep)
        {
            float x = MapX(tick, minTicks, maxTicks, left, right);
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), new Color(0.38f, 0.45f, 0.53f, 0.25f), 1f);
            DateTime utc = new DateTime(tick, DateTimeKind.Utc);
            string label = FormatUtcTick(utc, maxTicks - minTicks);
            GUI.Label(new Rect(x - 43f, bottom + 4f, 86f, 18f), label, GUI.skin.label);
        }
        GUI.Label(new Rect(left - 25f, bottom + 4f, 88f, 18f),
            FormatUtcTick(new DateTime(minTicks, DateTimeKind.Utc), maxTicks - minTicks), GUI.skin.label);
        GUI.Label(new Rect(right - 63f, bottom + 4f, 75f, 18f),
            FormatUtcTick(new DateTime(maxTicks, DateTimeKind.Utc), maxTicks - minTicks), GUI.skin.label);

        GUI.Label(new Rect(left, rect.y, right - left, 18f),
            LunarPlotVariableInfo.GetLabel(variable) + " (" + LunarPlotVariableInfo.GetUnits(variable) + ")");
        GUI.Label(new Rect(left, rect.yMax - 20f, right - left, 18f), "UTC");

        for (int siteIndex = 0; siteIndex < histories.Length; siteIndex++)
        {
            if (siteIndex >= selectedSites.Length || !selectedSites[siteIndex])
                continue;
            DrawSeries(histories[siteIndex].Points, variable, minTicks, maxTicks, minY, maxY,
                left, right, top, bottom, SiteColors[siteIndex]);
        }
    }

    public static string BuildCsv(
        LunarPlotVariable variable,
        LunarSitePlotHistory[] histories,
        bool[] selectedSites,
        LunarLocationResult[] locations)
    {
        StringBuilder csv = new StringBuilder("Site,Latitude_deg,LongitudeEast_deg,UTC,Variable,Value,Units\r\n");
        for (int siteIndex = 0; siteIndex < histories.Length; siteIndex++)
        {
            if (siteIndex >= selectedSites.Length || !selectedSites[siteIndex])
                continue;
            char siteLabel = (char)('A' + siteIndex);
            LunarLocationResult location = locations[siteIndex];
            foreach (LunarPlotPoint point in histories[siteIndex].Points)
            {
                double value = point.values[(int)variable];
                csv.Append(siteLabel).Append(',')
                    .Append(location.latitudeDeg.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                    .Append(location.longitudeEastDeg.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',')
                    .Append(LunarPlotVariableInfo.GetFileNameToken(variable)).Append(',')
                    .Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(LunarPlotVariableInfo.GetUnits(variable)).Append("\r\n");
            }
        }
        return csv.ToString();
    }

    private static bool TryGetBounds(
        LunarSitePlotHistory[] histories,
        bool[] selectedSites,
        LunarPlotVariable variable,
        out long minTicks,
        out long maxTicks,
        out double minY,
        out double maxY)
    {
        minTicks = long.MaxValue;
        maxTicks = long.MinValue;
        minY = double.PositiveInfinity;
        maxY = double.NegativeInfinity;
        for (int siteIndex = 0; siteIndex < histories.Length; siteIndex++)
        {
            if (siteIndex >= selectedSites.Length || !selectedSites[siteIndex])
                continue;
            foreach (LunarPlotPoint point in histories[siteIndex].Points)
            {
                minTicks = Math.Min(minTicks, point.utc.Ticks);
                maxTicks = Math.Max(maxTicks, point.utc.Ticks);
                double value = point.values[(int)variable];
                minY = Math.Min(minY, value);
                maxY = Math.Max(maxY, value);
            }
        }

        if (minTicks == long.MaxValue)
            return false;
        if (variable == LunarPlotVariable.SunAzimuth || variable == LunarPlotVariable.EarthAzimuth)
        {
            minY = 0.0;
            maxY = 360.0;
        }
        else if (variable == LunarPlotVariable.SunDiscVisibilityPercent)
        {
            minY = 0.0;
            maxY = 100.0;
        }
        else if (variable == LunarPlotVariable.EarthDiscVisibility || variable == LunarPlotVariable.Incidence)
        {
            minY = 0.0;
            maxY = 1.0;
        }
        return true;
    }

    private static void SetDisplayRange(LunarPlotVariable variable, ref double minY, ref double maxY)
    {
        if (variable == LunarPlotVariable.SunAzimuth || variable == LunarPlotVariable.EarthAzimuth ||
            variable == LunarPlotVariable.SunDiscVisibilityPercent ||
            variable == LunarPlotVariable.EarthDiscVisibility || variable == LunarPlotVariable.Incidence)
            return;

        double span = maxY - minY;
        double padding = span > 0.0 ? span * 0.08 : Math.Max(1.0, Math.Abs(maxY) * 0.05);
        minY -= padding;
        maxY += padding;
    }

    private static long GetUtcTickStep(long spanTicks)
    {
        double spanHours = (double)spanTicks / TimeSpan.TicksPerHour;
        if (spanHours <= 3.0) return TimeSpan.TicksPerHour;
        if (spanHours <= 18.0) return 6 * TimeSpan.TicksPerHour;
        if (spanHours <= 24.0 * 5.0) return TimeSpan.TicksPerDay;
        if (spanHours <= 24.0 * 30.0) return 7 * TimeSpan.TicksPerDay;
        if (spanHours <= 24.0 * 365.0) return 30 * TimeSpan.TicksPerDay;
        return 90 * TimeSpan.TicksPerDay;
    }

    private static float MapX(long ticks, long minTicks, long maxTicks, float left, float right)
    {
        double t = (double)(ticks - minTicks) / (maxTicks - minTicks);
        return Mathf.Lerp(left, right, (float)Math.Max(0.0, Math.Min(1.0, t)));
    }

    private static string FormatUtcTick(DateTime utc, long spanTicks)
    {
        if (spanTicks <= TimeSpan.TicksPerDay)
            return utc.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (spanTicks > 365L * TimeSpan.TicksPerDay)
            return utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return utc.ToString("MM-dd", CultureInfo.InvariantCulture);
    }

    private static float MapY(double value, double minY, double maxY, float top, float bottom)
    {
        double t = (value - minY) / (maxY - minY);
        return Mathf.Lerp(bottom, top, (float)Math.Max(0.0, Math.Min(1.0, t)));
    }

    private static void DrawSeries(
        IReadOnlyList<LunarPlotPoint> points,
        LunarPlotVariable variable,
        long minTicks,
        long maxTicks,
        double minY,
        double maxY,
        float left,
        float right,
        float top,
        float bottom,
        Color color)
    {
        if (points.Count == 1)
        {
            float x = MapX(points[0].utc.Ticks, minTicks, maxTicks, left, right);
            float y = MapY(points[0].values[(int)variable], minY, maxY, top, bottom);
            GUI.color = color;
            GUI.DrawTexture(new Rect(x - 4f, y - 4f, 8f, 8f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            return;
        }

        int stride = Math.Max(1, (points.Count + MaximumRenderedPointsPerSite - 1) /
                                 MaximumRenderedPointsPerSite);
        int previousIndex = 0;
        for (int currentIndex = stride; currentIndex < points.Count; currentIndex += stride)
        {
            DrawSegment(points[previousIndex], points[currentIndex], variable, minTicks, maxTicks,
                minY, maxY, left, right, top, bottom, color);
            previousIndex = currentIndex;
        }

        if (previousIndex < points.Count - 1)
            DrawSegment(points[previousIndex], points[points.Count - 1], variable, minTicks, maxTicks,
                minY, maxY, left, right, top, bottom, color);
    }

    private static void DrawSegment(
        LunarPlotPoint previous,
        LunarPlotPoint current,
        LunarPlotVariable variable,
        long minTicks,
        long maxTicks,
        double minY,
        double maxY,
        float left,
        float right,
        float top,
        float bottom,
        Color color)
    {
        double previousValue = previous.values[(int)variable];
        double currentValue = current.values[(int)variable];
        Vector2 start = new Vector2(
            MapX(previous.utc.Ticks, minTicks, maxTicks, left, right),
            MapY(previousValue, minY, maxY, top, bottom));
        Vector2 end = new Vector2(
            MapX(current.utc.Ticks, minTicks, maxTicks, left, right),
            MapY(currentValue, minY, maxY, top, bottom));

        bool isAzimuth = variable == LunarPlotVariable.SunAzimuth ||
                         variable == LunarPlotVariable.EarthAzimuth;
        if (isAzimuth && Math.Abs(currentValue - previousValue) > 180.0)
        {
            // Split the wrap at the 0/360 plot boundary instead of dropping
            // the whole sample interval and leaving a visible gap in the trace.
            bool crosses360 = previousValue > currentValue;
            double unwrappedCurrent = currentValue + (crosses360 ? 360.0 : -360.0);
            double boundary = crosses360 ? 360.0 : 0.0;
            float crossingFraction = (float)((boundary - previousValue) /
                                             (unwrappedCurrent - previousValue));
            float crossingX = Mathf.Lerp(start.x, end.x, crossingFraction);
            float boundaryY = MapY(boundary, minY, maxY, top, bottom);
            float oppositeBoundaryY = MapY(crosses360 ? 0.0 : 360.0, minY, maxY, top, bottom);
            DrawConnectedLine(start, new Vector2(crossingX, boundaryY), color, 2.2f);
            DrawConnectedLine(new Vector2(crossingX, oppositeBoundaryY), end, color, 2.2f);
            return;
        }

        DrawConnectedLine(start, end, color, 2.2f);
    }

    /// <summary>Draws a plot segment with overlapping caps so adjacent samples have no raster gaps.</summary>
    public static void DrawConnectedLine(Vector2 start, Vector2 end, Color color, float thickness)
    {
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length <= 0.1f)
            return;

        float overlap = Mathf.Min(thickness * 0.65f, length * 0.25f);
        Vector2 extension = delta * (overlap / length);
        DrawLine(start - extension, end + extension, color, thickness);

        float capSize = thickness + 0.5f;
        GUI.color = color;
        GUI.DrawTexture(new Rect(start.x - capSize * 0.5f, start.y - capSize * 0.5f,
            capSize, capSize), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(end.x - capSize * 0.5f, end.y - capSize * 0.5f,
            capSize, capSize), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private static string FormatAxisValue(double value)
        => value.ToString(Math.Abs(value) >= 100.0 ? "F0" : "F1", CultureInfo.InvariantCulture);

    private static void DrawLine(Vector2 start, Vector2 end, Color color, float thickness)
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
        GUI.color = Color.white;
    }
}
