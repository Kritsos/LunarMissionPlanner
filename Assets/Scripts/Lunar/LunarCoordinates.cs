using System;
using UnityEngine;

/// <summary>Double-precision Cartesian vector in the scientific lunar frame, in metres.</summary>
[Serializable]
public struct LunarCartesianPosition
{
    public double x;
    public double y;
    public double z;

    public LunarCartesianPosition(double xValue, double yValue, double zValue)
    {
        x = xValue;
        y = yValue;
        z = zValue;
    }

    public double Magnitude => Math.Sqrt(x * x + y * y + z * z);
}

[Serializable]
public struct LunarGeographicCoordinate
{
    public double latitudeDeg;
    public double longitudeEastDeg;

    public LunarGeographicCoordinate(double latitude, double longitudeEast)
    {
        latitudeDeg = latitude;
        longitudeEastDeg = longitudeEast;
    }
}

/// <summary>
/// Scientific coordinate calculations for the LOLA Mean Earth/Polar Axis frame.
/// This class deliberately knows nothing about Unity scene transforms or the GLB reflection.
/// </summary>
public static class LunarCoordinates
{
    public static LunarCartesianPosition GeographicToScientificCartesian(
        double latitudeDeg,
        double longitudeEastDeg,
        double radiusMeters)
    {
        ValidateLatitude(latitudeDeg);
        if (!IsFinite(longitudeEastDeg))
            throw new ArgumentOutOfRangeException(nameof(longitudeEastDeg), "Longitude must be finite.");
        if (!IsFinite(radiusMeters) || radiusMeters <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), "Radius must be finite and positive.");

        double latitudeRad = latitudeDeg * Math.PI / 180.0;
        double longitudeRad = NormalizeLongitudeEast(longitudeEastDeg) * Math.PI / 180.0;
        double cosLatitude = Math.Cos(latitudeRad);

        // Right-handed LOLA ME frame:
        // +X = prime meridian, +Y = 90 degrees east, +Z = north pole.
        return new LunarCartesianPosition(
            radiusMeters * cosLatitude * Math.Cos(longitudeRad),
            radiusMeters * cosLatitude * Math.Sin(longitudeRad),
            radiusMeters * Math.Sin(latitudeRad));
    }

    public static LunarGeographicCoordinate ScientificCartesianToGeographic(
        LunarCartesianPosition position)
    {
        if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
            throw new ArgumentException("Cartesian coordinates must be finite.", nameof(position));

        double horizontal = Math.Sqrt(position.x * position.x + position.y * position.y);
        double radius = Math.Sqrt(horizontal * horizontal + position.z * position.z);
        if (radius <= 0.0)
            throw new ArgumentException("The lunar centre has no geographic latitude or longitude.", nameof(position));

        double latitudeRad = Math.Atan2(position.z, horizontal);
        // Longitude is undefined at the exact poles. Allow for a tiny horizontal
        // component introduced when Unity's float Vector3 is used for a pole click.
        double longitudeDeg = horizontal < radius * 1e-7
            ? 0.0
            : Math.Atan2(position.y, position.x) * 180.0 / Math.PI;

        return new LunarGeographicCoordinate(
            latitudeRad * 180.0 / Math.PI,
            NormalizeLongitudeEast(longitudeDeg));
    }

    /// <summary>Normalize east-positive longitude to [0, 360).</summary>
    public static double NormalizeLongitudeEast(double longitudeDeg)
    {
        if (!IsFinite(longitudeDeg))
            throw new ArgumentOutOfRangeException(nameof(longitudeDeg), "Longitude must be finite.");
        double normalized = longitudeDeg % 360.0;
        if (normalized < 0.0)
            normalized += 360.0;
        // Avoid returning 360 because of floating-point edge cases.
        return normalized >= 360.0 ? 0.0 : normalized;
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void ValidateLatitude(double latitudeDeg)
    {
        if (!IsFinite(latitudeDeg) || latitudeDeg < -90.0 || latitudeDeg > 90.0)
            throw new ArgumentOutOfRangeException(nameof(latitudeDeg), "Latitude must be between -90 and +90 degrees.");
    }
}
