using System;

/// <summary>Small vector helpers for the physical, double-precision lunar frame.</summary>
public static class LunarVectorMath
{
    public static double Dot(DoubleVector3 a, DoubleVector3 b)
        => a.x * b.x + a.y * b.y + a.z * b.z;

    public static DoubleVector3 Cross(DoubleVector3 a, DoubleVector3 b)
        => new DoubleVector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);

    public static double Magnitude(DoubleVector3 value)
        => Math.Sqrt(Dot(value, value));

    public static bool TryNormalize(DoubleVector3 value, out DoubleVector3 normalized)
    {
        double magnitude = Magnitude(value);
        if (!IsFinite(magnitude) || magnitude <= 1e-12)
        {
            normalized = default;
            return false;
        }

        normalized = value / magnitude;
        return true;
    }

    public static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
