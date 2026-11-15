using System;

[Serializable]
public struct SolarPowerResult
{
    public double irradianceWPerSquareMeter;
    public double incidenceFactor;
    public double powerWatts;
}

[Serializable]
public struct SolarEnergySample
{
    public double powerWatts;
    public double sunClearanceDegrees;
    public bool sunVisible;

    public SolarEnergySample(double power, double clearance, bool visible)
    {
        powerWatts = power;
        sunClearanceDegrees = clearance;
        sunVisible = visible;
    }
}

public delegate bool SolarEnergySampleProvider(
    double etSeconds,
    out SolarEnergySample sample,
    out string error);

/// <summary>Integrates solar output in simulated ephemeris seconds.</summary>
public static class SolarPowerCalculator
{
    private const int MaximumSubdivisionDepth = 24;
    private const double AstronomicalUnitMeters = 149597870700.0;

    /// <summary>Calculates instantaneous electrical output for the configured panel.</summary>
    public static bool TryEvaluateInstantaneousPower(
        LunarSurfaceSite site,
        LunarVisibilityResult visibility,
        bool useSurfaceNormal,
        double panelAzimuthDegrees,
        double panelTiltDegrees,
        double panelAreaSquareMeters,
        double panelEfficiencyFraction,
        double nominalIrradianceAtAu,
        out SolarPowerResult result,
        out string error)
    {
        result = default;
        error = null;
        if (!LunarCoordinates.IsFinite(panelAreaSquareMeters) || panelAreaSquareMeters < 0.0 ||
            !LunarCoordinates.IsFinite(panelEfficiencyFraction) ||
            panelEfficiencyFraction < 0.0 || panelEfficiencyFraction > 1.0 ||
            !LunarCoordinates.IsFinite(nominalIrradianceAtAu) || nominalIrradianceAtAu <= 0.0 ||
            !LunarCoordinates.IsFinite(visibility.sunVisibleFraction) ||
            visibility.sunVisibleFraction < 0.0 || visibility.sunVisibleFraction > 1.0)
        {
            error = "Solar panel inputs or visible-Sun fraction are outside their valid ranges.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(visibility.sunDistanceMeters) || visibility.sunDistanceMeters <= 0.0 ||
            !LunarVectorMath.TryNormalize(visibility.sunDirectionME, out DoubleVector3 sunDirection))
        {
            error = "The visibility result does not contain a valid site-to-Sun vector.";
            return false;
        }

        DoubleVector3 panelNormal;
        if (useSurfaceNormal)
        {
            if (!LunarVectorMath.TryNormalize(site.surfaceNormal, out panelNormal))
            {
                error = "The DEM-derived panel normal is invalid.";
                return false;
            }
        }
        else
        {
            if (!LunarCoordinates.IsFinite(panelAzimuthDegrees) ||
                !LunarCoordinates.IsFinite(panelTiltDegrees) || panelTiltDegrees < 0.0 || panelTiltDegrees > 90.0)
            {
                error = "Panel azimuth must be finite and tilt must be between 0 and 90 degrees.";
                return false;
            }

            double azimuth = LunarVectorMath.WrapDegrees(panelAzimuthDegrees) * Math.PI / 180.0;
            double tilt = panelTiltDegrees * Math.PI / 180.0;
            DoubleVector3 tangent = site.north * Math.Cos(azimuth) + site.east * Math.Sin(azimuth);
            panelNormal = site.radialUp * Math.Cos(tilt) + tangent * Math.Sin(tilt);
            if (!LunarVectorMath.TryNormalize(panelNormal, out panelNormal))
            {
                error = "The configured solar panel normal is invalid.";
                return false;
            }
        }

        double incidence = Math.Max(0.0, LunarVectorMath.Dot(panelNormal, sunDirection));
        double ratio = AstronomicalUnitMeters / visibility.sunDistanceMeters;
        double irradiance = nominalIrradianceAtAu * ratio * ratio;
        double power = panelAreaSquareMeters * panelEfficiencyFraction * irradiance * incidence *
                       visibility.sunVisibleFraction;
        if (!LunarCoordinates.IsFinite(irradiance) || !LunarCoordinates.IsFinite(power))
        {
            error = "Solar power calculation produced a non-finite result.";
            return false;
        }

        result = new SolarPowerResult
        {
            irradianceWPerSquareMeter = irradiance,
            incidenceFactor = incidence,
            powerWatts = power
        };
        return true;
    }

    /// <summary>
    /// Integrates a forward ET interval with composite trapezoids. Each base interval
    /// is midpoint sampled; intervals containing a Sun-visibility transition are
    /// subdivided until they are within transitionToleranceSeconds.
    /// </summary>
    public static bool TryIntegrateInterval(
        double startEtSeconds,
        SolarEnergySample startSample,
        double endEtSeconds,
        SolarEnergySample endSample,
        double maximumStepSeconds,
        double transitionToleranceSeconds,
        int maximumSamples,
        SolarEnergySampleProvider sampleProvider,
        out double energyWattHours,
        out int samplesUsed,
        out bool sampleLimitExceeded,
        out string error)
    {
        energyWattHours = 0.0;
        samplesUsed = 0;
        sampleLimitExceeded = false;
        error = null;

        if (!LunarCoordinates.IsFinite(startEtSeconds) || !LunarCoordinates.IsFinite(endEtSeconds) ||
            endEtSeconds < startEtSeconds || !IsValid(startSample) || !IsValid(endSample))
        {
            error = "Energy integration requires finite samples over a forward ET interval.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(maximumStepSeconds) || maximumStepSeconds <= 0.0 ||
            !LunarCoordinates.IsFinite(transitionToleranceSeconds) || transitionToleranceSeconds <= 0.0 ||
            maximumSamples < 1 || sampleProvider == null)
        {
            error = "Energy integration settings or sample provider are invalid.";
            return false;
        }

        double duration = endEtSeconds - startEtSeconds;
        if (duration <= 0.0)
            return true;

        double requiredBaseIntervals = Math.Ceiling(duration / maximumStepSeconds);
        double minimumRequiredSamples = Math.Max(0.0, requiredBaseIntervals * 2.0 - 1.0);
        if (!LunarCoordinates.IsFinite(requiredBaseIntervals) || minimumRequiredSamples > maximumSamples)
        {
            sampleLimitExceeded = true;
            error = "The ET interval exceeds the configured energy sample limit.";
            return false;
        }

        int baseIntervals = (int)requiredBaseIntervals;
        double intervalSeconds = duration / baseIntervals;
        double energyJoules = 0.0;
        SolarEnergySample left = startSample;
        double leftEt = startEtSeconds;

        for (int i = 1; i <= baseIntervals; i++)
        {
            double rightEt = i == baseIntervals
                ? endEtSeconds
                : startEtSeconds + intervalSeconds * i;
            SolarEnergySample right;
            if (i == baseIntervals)
            {
                right = endSample;
            }
            else if (!TryGetSample(rightEt, sampleProvider, maximumSamples,
                         ref samplesUsed, out right, out sampleLimitExceeded, out error))
            {
                return false;
            }

            if (!TryIntegrateSubinterval(
                    leftEt, left, rightEt, right,
                    transitionToleranceSeconds, 0,
                    sampleProvider, maximumSamples, ref samplesUsed,
                    ref sampleLimitExceeded, out double intervalJoules, out error))
                return false;

            energyJoules += intervalJoules;
            leftEt = rightEt;
            left = right;
        }

        energyWattHours = energyJoules / 3600.0;
        if (!LunarCoordinates.IsFinite(energyWattHours) || energyWattHours < 0.0)
        {
            energyWattHours = 0.0;
            error = "Energy integration produced an invalid result.";
            return false;
        }
        return true;
    }

    private static bool TryIntegrateSubinterval(
        double leftEt,
        SolarEnergySample left,
        double rightEt,
        SolarEnergySample right,
        double transitionToleranceSeconds,
        int depth,
        SolarEnergySampleProvider sampleProvider,
        int maximumSamples,
        ref int samplesUsed,
        ref bool sampleLimitExceeded,
        out double energyJoules,
        out string error)
    {
        energyJoules = 0.0;
        error = null;
        double width = rightEt - leftEt;
        if (width <= 0.0)
            return true;

        double middleEt = leftEt + width * 0.5;
        if (middleEt <= leftEt || middleEt >= rightEt || depth >= MaximumSubdivisionDepth)
        {
            energyJoules = TrapezoidJoules(width, left.powerWatts, right.powerWatts);
            return true;
        }

        if (!TryGetSample(middleEt, sampleProvider, maximumSamples,
                ref samplesUsed, out SolarEnergySample middle,
                out sampleLimitExceeded, out error))
            return false;

        bool transition = left.sunVisible != middle.sunVisible ||
                          middle.sunVisible != right.sunVisible;
        if (!transition || width <= transitionToleranceSeconds)
        {
            energyJoules = TrapezoidJoules(width * 0.5, left.powerWatts, middle.powerWatts) +
                           TrapezoidJoules(width * 0.5, middle.powerWatts, right.powerWatts);
            return true;
        }

        if (!TryIntegrateSubinterval(
                leftEt, left, middleEt, middle, transitionToleranceSeconds, depth + 1,
                sampleProvider, maximumSamples, ref samplesUsed, ref sampleLimitExceeded,
                out double leftJoules, out error))
            return false;
        if (!TryIntegrateSubinterval(
                middleEt, middle, rightEt, right, transitionToleranceSeconds, depth + 1,
                sampleProvider, maximumSamples, ref samplesUsed, ref sampleLimitExceeded,
                out double rightJoules, out error))
            return false;

        energyJoules = leftJoules + rightJoules;
        return true;
    }

    private static bool TryGetSample(
        double etSeconds,
        SolarEnergySampleProvider sampleProvider,
        int maximumSamples,
        ref int samplesUsed,
        out SolarEnergySample sample,
        out bool sampleLimitExceeded,
        out string error)
    {
        sample = default;
        sampleLimitExceeded = false;
        error = null;
        if (samplesUsed >= maximumSamples)
        {
            sampleLimitExceeded = true;
            error = "The ET interval exceeds the configured energy sample limit.";
            return false;
        }
        if (!sampleProvider(etSeconds, out sample, out error))
            return false;
        samplesUsed++;
        if (!IsValid(sample))
        {
            error = "An energy sample contains invalid power or solar geometry.";
            return false;
        }
        return true;
    }

    private static double TrapezoidJoules(double seconds, double startWatts, double endWatts)
        => (startWatts + endWatts) * 0.5 * seconds;

    private static bool IsValid(SolarEnergySample sample)
        => LunarCoordinates.IsFinite(sample.powerWatts) && sample.powerWatts >= 0.0 &&
           LunarCoordinates.IsFinite(sample.sunClearanceDegrees);
}
