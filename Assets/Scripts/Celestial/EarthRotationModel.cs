using System;
using UnityEngine;

/// <summary>
/// Evaluates the long-range IAU Earth orientation model used by NAIF's
/// pck00011.tpc. The result maps IAU_EARTH body-fixed axes into J2000/ICRF.
/// NAIF cautions that this model is for visualization and general geometry,
/// not high-accuracy terrestrial orientation.
/// </summary>
public static class EarthRotationModel
{
    private const double SecondsPerDay = 86400.0;
    private const double DaysPerJulianCentury = 36525.0;
    private const double DegreesToRadians = Math.PI / 180.0;

    // BODY399_POLE_RA, BODY399_POLE_DEC, BODY399_PM in NAIF pck00011.tpc.
    private const double PoleRaAtJ2000Degrees = 0.0;
    private const double PoleRaDegreesPerCentury = -0.641;
    private const double PoleDecAtJ2000Degrees = 90.0;
    private const double PoleDecDegreesPerCentury = -0.557;
    private const double PrimeMeridianAtJ2000Degrees = 190.147;
    private const double PrimeMeridianDegreesPerTdbDay = 360.9856235;

    /// <summary>
    /// Returns the IAU_EARTH body-to-J2000 rotation at ET seconds past J2000 TDB.
    /// </summary>
    public static Quaternion BodyToJ2000(double etSeconds)
    {
        double tdbDays = etSeconds / SecondsPerDay;
        double tdbCenturies = tdbDays / DaysPerJulianCentury;

        double rightAscension = (PoleRaAtJ2000Degrees + PoleRaDegreesPerCentury * tdbCenturies) * DegreesToRadians;
        double declination = (PoleDecAtJ2000Degrees + PoleDecDegreesPerCentury * tdbCenturies) * DegreesToRadians;
        double primeMeridian = WrapDegrees(
            PrimeMeridianAtJ2000Degrees + PrimeMeridianDegreesPerTdbDay * tdbDays) * DegreesToRadians;

        double cosRa = Math.Cos(rightAscension);
        double sinRa = Math.Sin(rightAscension);
        double cosDec = Math.Cos(declination);
        double sinDec = Math.Sin(declination);
        double cosW = Math.Cos(primeMeridian);
        double sinW = Math.Sin(primeMeridian);

        // Unit vector toward the north pole and two tangent directions at it.
        Vector3d north = new Vector3d(cosDec * cosRa, cosDec * sinRa, sinDec);
        Vector3d increasingRa = new Vector3d(-sinRa, cosRa, 0.0);
        Vector3d increasingDec = new Vector3d(-sinDec * cosRa, -sinDec * sinRa, cosDec);

        // IAU_EARTH +X points through the prime meridian; +Y is 90 degrees
        // east; +Z points north. These columns form the body-to-J2000 matrix.
        Vector3d primeMeridianAxis = increasingRa * cosW + increasingDec * sinW;
        Vector3d eastAxis = Cross(north, primeMeridianAxis);
        return RotationFromColumns(primeMeridianAxis, eastAxis, north);
    }

    private static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    private static Vector3d Cross(Vector3d a, Vector3d b)
    {
        return new Vector3d(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);
    }

    private static Quaternion RotationFromColumns(Vector3d xAxis, Vector3d yAxis, Vector3d zAxis)
    {
        double m00 = xAxis.x;
        double m01 = yAxis.x;
        double m02 = zAxis.x;
        double m10 = xAxis.y;
        double m11 = yAxis.y;
        double m12 = zAxis.y;
        double m20 = xAxis.z;
        double m21 = yAxis.z;
        double m22 = zAxis.z;
        double trace = m00 + m11 + m22;

        double x;
        double y;
        double z;
        double w;
        if (trace > 0.0)
        {
            double scale = Math.Sqrt(trace + 1.0) * 2.0;
            w = 0.25 * scale;
            x = (m21 - m12) / scale;
            y = (m02 - m20) / scale;
            z = (m10 - m01) / scale;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double scale = Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
            w = (m21 - m12) / scale;
            x = 0.25 * scale;
            y = (m01 + m10) / scale;
            z = (m02 + m20) / scale;
        }
        else if (m11 > m22)
        {
            double scale = Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
            w = (m02 - m20) / scale;
            x = (m01 + m10) / scale;
            y = 0.25 * scale;
            z = (m12 + m21) / scale;
        }
        else
        {
            double scale = Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
            w = (m10 - m01) / scale;
            x = (m02 + m20) / scale;
            y = (m12 + m21) / scale;
            z = 0.25 * scale;
        }

        double norm = Math.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion((float)(x / norm), (float)(y / norm), (float)(z / norm), (float)(w / norm));
    }

    private struct Vector3d
    {
        public readonly double x;
        public readonly double y;
        public readonly double z;

        public Vector3d(double xValue, double yValue, double zValue)
        {
            x = xValue;
            y = yValue;
            z = zValue;
        }

        public static Vector3d operator +(Vector3d a, Vector3d b)
            => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3d operator *(Vector3d value, double scale)
            => new Vector3d(value.x * scale, value.y * scale, value.z * scale);
    }
}
