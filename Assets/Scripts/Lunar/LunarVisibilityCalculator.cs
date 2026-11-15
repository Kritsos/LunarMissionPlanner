using System;

[Serializable]
public struct LunarVisibilityResult
{
    public double azimuthSunDeg;
    public double elevationSunDeg;
    public double horizonSunDeg;
    public double sunClearanceDeg;
    public double sunVisibleFraction;
    public bool sunVisible;
    public DoubleVector3 sunDirectionME;
    public double azimuthEarthDeg;
    public double elevationEarthDeg;
    public double horizonEarthDeg;
    public double earthClearanceDeg;
    public bool earthVisible;
    public DoubleVector3 earthDirectionME;
    public double sunDistanceMeters;
}

public static class LunarVisibilityCalculator
{
    private const double KilometersToMeters = 1000.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double SunRadiusMeters = 695700000.0;
    private const double EarthMeanRadiusMeters = 6371008.8;
    private const int DiscVisibilitySampleCount = 1024;
    private const double GoldenAngleRadians = 2.39996322972865332;

    public static bool TryEvaluate(
        EphemerisState state,
        LunarSurfaceSite site,
        LunarHorizonProfile horizon,
        out LunarVisibilityResult result,
        out string error)
    {
        result = default;
        error = null;
        if (horizon == null || horizon.ElevationDegreesByAzimuth == null)
        {
            error = "Terrain horizon has not been calculated for the selected site.";
            return false;
        }
        if (horizon.ElevationDegreesByAzimuth.Length == 0 ||
            !LunarCoordinates.IsFinite(horizon.ObserverHeightMeters) || horizon.ObserverHeightMeters < 0.0)
        {
            error = "The terrain horizon has no samples or has invalid observer height.";
            return false;
        }
        double qNorm = state.moonBodyToJ2000.Magnitude;
        if (!state.moonBodyToJ2000.IsFinite || !LunarCoordinates.IsFinite(qNorm) || qNorm <= 1e-12 ||
            !state.earthHeliocentricPositionKm.IsFinite || !state.moonHeliocentricPositionKm.IsFinite)
        {
            error = "The ephemeris state contains invalid coordinates or lunar attitude.";
            return false;
        }
        // The cached quaternion maps ME body-fixed vectors to J2000. Its inverse is
        // the conjugate, used here before any Unity visualization transforms.
        DoubleQuaternion bodyToJ2000 = state.moonBodyToJ2000;
        bodyToJ2000 = new DoubleQuaternion(
            bodyToJ2000.x / qNorm, bodyToJ2000.y / qNorm,
            bodyToJ2000.z / qNorm, bodyToJ2000.w / qNorm);
        DoubleQuaternion j2000ToBody = new DoubleQuaternion(
            -bodyToJ2000.x, -bodyToJ2000.y, -bodyToJ2000.z, bodyToJ2000.w);

        DoubleVector3 sunFromMoonJ2000Km = state.moonHeliocentricPositionKm * -1.0;
        DoubleVector3 earthFromMoonJ2000Km =
            state.earthHeliocentricPositionKm - state.moonHeliocentricPositionKm;
        DoubleVector3 sunFromMoonMeters = j2000ToBody.Rotate(sunFromMoonJ2000Km) * KilometersToMeters;
        DoubleVector3 earthFromMoonMeters = j2000ToBody.Rotate(earthFromMoonJ2000Km) * KilometersToMeters;
        // The visibility origin must match the origin used to build the horizon.
        // The single-site panel model is placed at this same configured height.
        DoubleVector3 observerPosition = site.positionMeters +
                                         site.radialUp * horizon.ObserverHeightMeters;
        DoubleVector3 sunFromObserver = sunFromMoonMeters - observerPosition;
        DoubleVector3 earthFromObserver = earthFromMoonMeters - observerPosition;
        if (!LunarVectorMath.TryNormalize(sunFromObserver, out DoubleVector3 sunDirection) ||
            !LunarVectorMath.TryNormalize(earthFromObserver, out DoubleVector3 earthDirection))
        {
            error = "The Sun or Earth line-of-sight vector could not be normalized.";
            return false;
        }

        LocalAngles(sunDirection, site, out double sunAzimuth, out double sunElevation);
        LocalAngles(earthDirection, site, out double earthAzimuth, out double earthElevation);
        double sunHorizon = horizon.GetElevationDegrees(sunAzimuth);
        double earthHorizon = horizon.GetElevationDegrees(earthAzimuth);
        double sunClearance = sunElevation - sunHorizon;
        double earthClearance = earthElevation - earthHorizon;

        double sunDistanceMeters = LunarVectorMath.Magnitude(sunFromObserver);
        double earthDistanceMeters = LunarVectorMath.Magnitude(earthFromObserver);
        if (!LunarCoordinates.IsFinite(sunDistanceMeters) || sunDistanceMeters <= SunRadiusMeters)
        {
            error = "The site-to-Sun distance is invalid.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(earthDistanceMeters) || earthDistanceMeters <= EarthMeanRadiusMeters)
        {
            error = "The site-to-Earth distance is invalid.";
            return false;
        }

        double sunAngularRadius = Math.Asin(SunRadiusMeters / sunDistanceMeters);
        double earthAngularRadius = Math.Asin(EarthMeanRadiusMeters / earthDistanceMeters);
        if (!TryComputeVisibleDiscFraction(
                sunDirection, sunAngularRadius, site, horizon,
                out double sunVisibleFraction, out error))
            return false;

        bool sunVisible = HasAnyDiscVisibility(
            sunDirection, sunAzimuth, sunElevation, sunAngularRadius, site, horizon);
        // Preserve a nonzero estimate for a geometrically visible cap that falls
        // between all area samples; its reported area is below one sample quantum.
        if (sunVisible && sunVisibleFraction == 0.0)
            sunVisibleFraction = 1.0 / DiscVisibilitySampleCount;
        // Earth remains a line-of-sight flag. Its upper apparent limb is checked
        // against each piecewise-linear terrain-horizon segment, so even a narrow
        // visible sliver counts without requiring a disc-area sample to hit it.
        bool earthVisible = HasAnyDiscVisibility(
            earthDirection, earthAzimuth, earthElevation, earthAngularRadius, site, horizon);
        result = new LunarVisibilityResult
        {
            azimuthSunDeg = sunAzimuth,
            elevationSunDeg = sunElevation,
            horizonSunDeg = sunHorizon,
            sunClearanceDeg = sunClearance,
            sunVisibleFraction = sunVisibleFraction,
            sunVisible = sunVisible,
            sunDirectionME = sunDirection,
            azimuthEarthDeg = earthAzimuth,
            elevationEarthDeg = earthElevation,
            horizonEarthDeg = earthHorizon,
            earthClearanceDeg = earthClearance,
            earthVisible = earthVisible,
            earthDirectionME = earthDirection,
            sunDistanceMeters = sunDistanceMeters
        };
        return true;
    }

    /// <summary>
    /// Estimates the terrain-unobstructed fraction of an apparent spherical disc.
    /// Equal-solid-angle samples make this a direct estimate of visible sky area.
    /// </summary>
    private static bool TryComputeVisibleDiscFraction(
        DoubleVector3 centerDirection,
        double angularRadiusRadians,
        LunarSurfaceSite site,
        LunarHorizonProfile horizon,
        out double visibleFraction,
        out string error)
    {
        visibleFraction = 0.0;
        error = null;
        if (!LunarCoordinates.IsFinite(angularRadiusRadians) || angularRadiusRadians <= 0.0 ||
            angularRadiusRadians >= Math.PI * 0.5 ||
            !LunarVectorMath.TryNormalize(centerDirection, out centerDirection))
        {
            error = "The apparent body disc has invalid angular geometry.";
            return false;
        }

        DoubleVector3 referenceAxis = Math.Abs(centerDirection.z) < 0.9
            ? new DoubleVector3(0.0, 0.0, 1.0)
            : new DoubleVector3(1.0, 0.0, 0.0);
        if (!LunarVectorMath.TryNormalize(
                LunarVectorMath.Cross(referenceAxis, centerDirection), out DoubleVector3 tangentX))
        {
            error = "Could not construct a stable basis for the apparent body disc.";
            return false;
        }
        DoubleVector3 tangentY = LunarVectorMath.Cross(centerDirection, tangentX);

        double cosAngularRadius = Math.Cos(angularRadiusRadians);
        int visibleSamples = 0;
        for (int i = 0; i < DiscVisibilitySampleCount; i++)
        {
            // Uniformly spaced solid angle from the disc center to its limb, with
            // a golden-angle turn to distribute samples around the disc.
            double areaFraction = (i + 0.5) / DiscVisibilitySampleCount;
            double cosOffset = 1.0 - areaFraction * (1.0 - cosAngularRadius);
            double sinOffset = Math.Sqrt(Math.Max(0.0, 1.0 - cosOffset * cosOffset));
            double azimuthAroundDisc = i * GoldenAngleRadians;
            DoubleVector3 radial = tangentX * Math.Cos(azimuthAroundDisc) +
                                   tangentY * Math.Sin(azimuthAroundDisc);
            DoubleVector3 sampleDirection = centerDirection * cosOffset + radial * sinOffset;
            if (!LunarVectorMath.TryNormalize(sampleDirection, out sampleDirection))
            {
                error = "An apparent-disc sample direction could not be normalized.";
                return false;
            }

            LocalAngles(sampleDirection, site, out double sampleAzimuth, out double sampleElevation);
            double sampleHorizon = horizon.GetElevationDegrees(sampleAzimuth);
            if (sampleElevation > sampleHorizon)
                visibleSamples++;
        }

        visibleFraction = (double)visibleSamples / DiscVisibilitySampleCount;
        return true;
    }

    /// <summary>
    /// Checks whether any part of an apparent disc rises above the terrain skyline.
    /// The disc's upper limb is maximized against each horizon interpolation segment.
    /// </summary>
    private static bool HasAnyDiscVisibility(
        DoubleVector3 centerDirection,
        double centerAzimuthDegrees,
        double centerElevationDegrees,
        double angularRadiusRadians,
        LunarSurfaceSite site,
        LunarHorizonProfile horizon)
    {
        if (centerElevationDegrees > horizon.GetElevationDegrees(centerAzimuthDegrees))
            return true;

        double angularRadiusDegrees = angularRadiusRadians * RadiansToDegrees;
        if (centerElevationDegrees - angularRadiusDegrees > horizon.MaximumElevationDegrees)
            return true;

        // The sampled horizon is never below the zero-degree reference plane.
        if (centerElevationDegrees + angularRadiusDegrees <= 0.0)
            return false;

        double centerElevationRadians = centerElevationDegrees / RadiansToDegrees;
        if (Math.PI * 0.5 - centerElevationRadians <= angularRadiusRadians)
        {
            if (horizon.MaximumElevationDegrees < 90.0)
                return true;
            return HasAnySampledDiscVisibility(centerDirection, angularRadiusRadians, site, horizon);
        }

        double cosCenterElevation = Math.Cos(centerElevationRadians);
        if (cosCenterElevation <= 1e-12)
            return false;

        double azimuthRatio = Math.Sin(angularRadiusRadians) / cosCenterElevation;
        if (azimuthRatio >= 1.0)
            return HasAnySampledDiscVisibility(centerDirection, angularRadiusRadians, site, horizon);

        double azimuthHalfSpanDegrees =
            Math.Asin(azimuthRatio) * RadiansToDegrees;
        double startAzimuth = centerAzimuthDegrees - azimuthHalfSpanDegrees;
        double endAzimuth = centerAzimuthDegrees + azimuthHalfSpanDegrees;
        double binWidth = horizon.Settings.azimuthStepDegrees;
        double left = startAzimuth;
        double nextBoundary = (Math.Floor(left / binWidth) + 1.0) * binWidth;

        while (left < endAzimuth)
        {
            if (nextBoundary <= left + 1e-12)
                nextBoundary += binWidth;
            double right = Math.Min(endAzimuth, nextBoundary);
            if (HasVisibleUpperLimbOnSegment(
                    left, right, centerAzimuthDegrees, centerElevationRadians,
                    angularRadiusRadians, horizon))
                return true;

            left = right;
            nextBoundary += binWidth;
        }
        return false;
    }

    private static bool HasAnySampledDiscVisibility(
        DoubleVector3 centerDirection,
        double angularRadiusRadians,
        LunarSurfaceSite site,
        LunarHorizonProfile horizon)
        => TryComputeVisibleDiscFraction(
               centerDirection, angularRadiusRadians, site, horizon,
               out double fraction, out _) && fraction > 0.0;

    private static bool HasVisibleUpperLimbOnSegment(
        double startAzimuthDegrees,
        double endAzimuthDegrees,
        double centerAzimuthDegrees,
        double centerElevationRadians,
        double angularRadiusRadians,
        LunarHorizonProfile horizon)
    {
        double startClearance = UpperLimbClearance(
            startAzimuthDegrees, centerAzimuthDegrees, centerElevationRadians,
            angularRadiusRadians, horizon);
        double endClearance = UpperLimbClearance(
            endAzimuthDegrees, centerAzimuthDegrees, centerElevationRadians,
            angularRadiusRadians, horizon);
        if (startClearance > 0.0 || endClearance > 0.0)
            return true;

        // Within one horizon bin, the skyline is linear and the disc upper limb
        // is a smooth concave arc. Ternary search finds its maximum clearance.
        double low = startAzimuthDegrees;
        double high = endAzimuthDegrees;
        for (int i = 0; i < 48; i++)
        {
            double first = low + (high - low) / 3.0;
            double second = high - (high - low) / 3.0;
            double firstClearance = UpperLimbClearance(
                first, centerAzimuthDegrees, centerElevationRadians,
                angularRadiusRadians, horizon);
            double secondClearance = UpperLimbClearance(
                second, centerAzimuthDegrees, centerElevationRadians,
                angularRadiusRadians, horizon);
            if (firstClearance < secondClearance)
                low = first;
            else
                high = second;
        }

        double maximumClearance = UpperLimbClearance(
            (low + high) * 0.5, centerAzimuthDegrees, centerElevationRadians,
            angularRadiusRadians, horizon);
        return maximumClearance > 0.0;
    }

    private static double UpperLimbClearance(
        double azimuthDegrees,
        double centerAzimuthDegrees,
        double centerElevationRadians,
        double angularRadiusRadians,
        LunarHorizonProfile horizon)
    {
        double azimuthOffsetRadians =
            (azimuthDegrees - centerAzimuthDegrees) / RadiansToDegrees;
        double vertical = Math.Sin(centerElevationRadians);
        double horizontal = Math.Cos(centerElevationRadians) * Math.Cos(azimuthOffsetRadians);
        double projectionMagnitude = Math.Sqrt(vertical * vertical + horizontal * horizontal);
        double cosAngularRadius = Math.Cos(angularRadiusRadians);
        if (projectionMagnitude < cosAngularRadius)
            return double.NegativeInfinity;

        double limbCenterElevation = Math.Atan2(vertical, horizontal);
        double limbHalfHeight = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
            cosAngularRadius / projectionMagnitude)));
        double upperElevationDegrees = Math.Min(
            90.0, (limbCenterElevation + limbHalfHeight) * RadiansToDegrees);
        return upperElevationDegrees - horizon.GetElevationDegrees(azimuthDegrees);
    }

    private static void LocalAngles(
        DoubleVector3 direction,
        LunarSurfaceSite site,
        out double azimuthDegrees,
        out double elevationDegrees)
    {
        double east = LunarVectorMath.Dot(direction, site.east);
        double north = LunarVectorMath.Dot(direction, site.north);
        double up = LunarVectorMath.Dot(direction, site.radialUp);
        azimuthDegrees = LunarVectorMath.WrapDegrees(Math.Atan2(east, north) * RadiansToDegrees);
        elevationDegrees = Math.Asin(Math.Max(-1.0, Math.Min(1.0, up))) * RadiansToDegrees;
    }
}
