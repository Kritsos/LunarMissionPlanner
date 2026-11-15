using System;

[Serializable]
public struct LunarHorizonSettings
{
    public double azimuthStepDegrees;
    public double radialStepMeters;
    public double maximumRangeMeters;
}

public sealed class LunarHorizonProfile
{
    public LunarSurfaceSite Site { get; }
    public double ObserverHeightMeters { get; }
    public LunarHorizonSettings Settings { get; }
    public double[] ElevationDegreesByAzimuth { get; }
    public int TerrainSampleCount { get; }
    public double MaximumElevationDegrees { get; }

    public LunarHorizonProfile(
        LunarSurfaceSite site,
        double observerHeightMeters,
        LunarHorizonSettings settings,
        double[] elevations,
        int terrainSampleCount,
        double maximumElevationDegrees)
    {
        Site = site;
        ObserverHeightMeters = observerHeightMeters;
        Settings = settings;
        ElevationDegreesByAzimuth = elevations;
        TerrainSampleCount = terrainSampleCount;
        MaximumElevationDegrees = maximumElevationDegrees;
    }

    public double GetElevationDegrees(double azimuthDegrees)
    {
        if (ElevationDegreesByAzimuth == null || ElevationDegreesByAzimuth.Length == 0)
            return 0.0;

        double azimuth = LunarVectorMath.WrapDegrees(azimuthDegrees);
        double index = azimuth / Settings.azimuthStepDegrees;
        int low = (int)Math.Floor(index) % ElevationDegreesByAzimuth.Length;
        int high = (low + 1) % ElevationDegreesByAzimuth.Length;
        double fraction = index - Math.Floor(index);
        return ElevationDegreesByAzimuth[low] +
               (ElevationDegreesByAzimuth[high] - ElevationDegreesByAzimuth[low]) * fraction;
    }
}

public static class LunarHorizonCalculator
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double NearFieldRangeMeters = 5000.0;
    private const double MaximumTotalTerrainSamples = 2000000.0;

    public static bool ValidateSettings(LunarHorizonSettings settings, out string error)
    {
        error = null;
        if (!LunarCoordinates.IsFinite(settings.azimuthStepDegrees) ||
            settings.azimuthStepDegrees <= 0.0 || settings.azimuthStepDegrees > 10.0)
        {
            error = "Azimuth spacing must be between 0 and 10 degrees.";
            return false;
        }
        double bins = 360.0 / settings.azimuthStepDegrees;
        if (Math.Abs(bins - Math.Round(bins)) > 1e-8)
        {
            error = "Azimuth spacing must divide 360 degrees evenly.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(settings.radialStepMeters) || settings.radialStepMeters < 10.0)
        {
            error = "Terrain sampling step must be at least 10 metres.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(settings.maximumRangeMeters) ||
            settings.maximumRangeMeters < settings.radialStepMeters || settings.maximumRangeMeters > 1000000.0)
        {
            error = "Maximum horizon range must be at least one radial step and no more than 1,000 km.";
            return false;
        }

        double azimuthBinCount = Math.Round(bins);
        double nearRange = Math.Min(settings.maximumRangeMeters, NearFieldRangeMeters);
        double nearStep = Math.Min(settings.radialStepMeters, 500.0);
        double samplesPerAzimuth = Math.Ceiling(nearRange / nearStep) +
                                   Math.Ceiling((settings.maximumRangeMeters - nearRange) /
                                                settings.radialStepMeters);
        if (azimuthBinCount * samplesPerAzimuth > MaximumTotalTerrainSamples)
        {
            error = "These horizon settings exceed the 2,000,000-sample workload limit. " +
                    "Increase the azimuth or radial spacing, or reduce the maximum range.";
            return false;
        }
        return true;
    }

    public static bool TryComputeAzimuth(
        LunarDEMReader demReader,
        LunarSurfaceSite site,
        double observerHeightMeters,
        LunarHorizonSettings settings,
        double azimuthDegrees,
        out double maximumElevationDegrees,
        out int sampleCount,
        out string error)
    {
        maximumElevationDegrees = 0.0;
        sampleCount = 0;
        error = null;
        if (demReader == null)
        {
            error = "LunarDEMReader is not available.";
            return false;
        }
        if (!ValidateSettings(settings, out error))
            return false;
        if (!LunarCoordinates.IsFinite(observerHeightMeters) || observerHeightMeters < 0.0)
        {
            error = "Observer height must be a finite non-negative distance.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(azimuthDegrees))
        {
            error = "Azimuth must be finite.";
            return false;
        }

        double azimuth = LunarVectorMath.WrapDegrees(azimuthDegrees) * DegreesToRadians;
        DoubleVector3 tangent = site.north * Math.Cos(azimuth) + site.east * Math.Sin(azimuth);
        DoubleVector3 observer = site.positionMeters + site.radialUp * observerHeightMeters;
        double maxElevation = 0.0; // Tangent plane of the reference sphere.
        int samples = 0;
        double nearRange = Math.Min(settings.maximumRangeMeters, NearFieldRangeMeters);
        double nearStep = Math.Min(settings.radialStepMeters, 500.0);
        int nearSamples = (int)Math.Ceiling(nearRange / nearStep);
        string sampleErrorMessage = null;

        bool TrySampleAtDistance(double distance)
        {
            LunarSurfaceGeometry.GreatCircleDestination(
                site.latitudeDeg, site.longitudeEastDeg, tangent, distance,
                demReader.ReferenceRadiusMeters,
                out double sampleLatitude, out double sampleLongitude);
            if (!demReader.TryGetElevation(sampleLatitude, sampleLongitude,
                    out LolaElevationSample terrain, out string sampleError))
            {
                sampleErrorMessage = sampleError;
                return false;
            }

            DoubleVector3 terrainPoint = LunarSurfaceGeometry.PointOnReferenceFrame(
                sampleLatitude, terrain.longitudeEastDeg, terrain.radiusMeters);
            DoubleVector3 delta = terrainPoint - observer;
            double vertical = LunarVectorMath.Dot(delta, site.radialUp);
            double east = LunarVectorMath.Dot(delta, site.east);
            double north = LunarVectorMath.Dot(delta, site.north);
            double horizontal = Math.Sqrt(east * east + north * north);
            double apparentElevation = Math.Atan2(vertical, horizontal) * RadiansToDegrees;
            if (apparentElevation > maxElevation)
                maxElevation = apparentElevation;
            samples++;
            return true;
        }

        // The first five kilometres use at most 500 m spacing so nearby crater rims
        // are not skipped by the coarser long-range interval.
        for (int i = 1; i <= nearSamples; i++)
        {
            if (!TrySampleAtDistance(Math.Min(nearRange, i * nearStep)))
            {
                error = sampleErrorMessage;
                return false;
            }
        }

        int farIntervals = (int)Math.Floor(
            (settings.maximumRangeMeters - nearRange) / settings.radialStepMeters);
        for (int i = 1; i <= farIntervals; i++)
        {
            if (!TrySampleAtDistance(nearRange + i * settings.radialStepMeters))
            {
                error = sampleErrorMessage;
                return false;
            }
        }

        double lastDistance = nearRange + farIntervals * settings.radialStepMeters;
        if (lastDistance < settings.maximumRangeMeters - 1e-8 &&
            !TrySampleAtDistance(settings.maximumRangeMeters))
        {
            error = sampleErrorMessage;
            return false;
        }

        maximumElevationDegrees = maxElevation;
        sampleCount = samples;
        return true;
    }
}
