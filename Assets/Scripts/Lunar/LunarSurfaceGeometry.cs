using System;

[Serializable]
public struct LunarSurfaceSite
{
    public double latitudeDeg;
    public double longitudeEastDeg;
    public double elevationMeters;
    public double radiusMeters;
    public DoubleVector3 positionMeters;
    public DoubleVector3 radialUp;
    public DoubleVector3 east;
    public DoubleVector3 north;
    public DoubleVector3 surfaceNormal;

    public double SlopeDegrees
    {
        get
        {
            double dot = LunarVectorMath.Dot(surfaceNormal, radialUp);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            return Math.Acos(dot) * 180.0 / Math.PI;
        }
    }
}

/// <summary>
/// Creates a physical LOLA site and a DEM-derived surface normal. Nothing in this
/// class reads Unity mesh, collider, or transform geometry.
/// </summary>
public static class LunarSurfaceGeometry
{
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    public static bool TryCreateSite(
        LunarDEMReader demReader,
        double latitudeDeg,
        double longitudeEastDeg,
        double finiteDifferenceStepMeters,
        out LunarSurfaceSite site,
        out string error)
    {
        site = default;
        error = null;
        if (demReader == null)
        {
            error = "LunarDEMReader is not available.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(latitudeDeg) || latitudeDeg < -90.0 || latitudeDeg > 90.0)
        {
            error = "Latitude must be finite and between -90 and +90 degrees.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(longitudeEastDeg))
        {
            error = "Longitude must be finite.";
            return false;
        }
        if (!LunarCoordinates.IsFinite(finiteDifferenceStepMeters) || finiteDifferenceStepMeters < 1.0)
        {
            error = "The surface-normal sample spacing must be at least 1 metre.";
            return false;
        }
        if (!demReader.TryGetElevation(latitudeDeg, longitudeEastDeg,
                out LolaElevationSample center, out error))
            return false;

        DoubleVector3 up = RadialUp(latitudeDeg, center.longitudeEastDeg);
        LocalBasis(latitudeDeg, center.longitudeEastDeg, out DoubleVector3 east, out DoubleVector3 north);
        DoubleVector3 p0 = up * center.radiusMeters;

        if (!TryOffsetPoint(demReader, latitudeDeg, center.longitudeEastDeg,
                east, finiteDifferenceStepMeters, out DoubleVector3 eastPlus, out error) ||
            !TryOffsetPoint(demReader, latitudeDeg, center.longitudeEastDeg,
                east * -1.0, finiteDifferenceStepMeters, out DoubleVector3 eastMinus, out error) ||
            !TryOffsetPoint(demReader, latitudeDeg, center.longitudeEastDeg,
                north, finiteDifferenceStepMeters, out DoubleVector3 northPlus, out error) ||
            !TryOffsetPoint(demReader, latitudeDeg, center.longitudeEastDeg,
                north * -1.0, finiteDifferenceStepMeters, out DoubleVector3 northMinus, out error))
            return false;

        // Derivatives are per physical metre. east x north points outward in ME/PA.
        DoubleVector3 eastTangent = (eastPlus - eastMinus) / (2.0 * finiteDifferenceStepMeters);
        DoubleVector3 northTangent = (northPlus - northMinus) / (2.0 * finiteDifferenceStepMeters);
        DoubleVector3 normal = LunarVectorMath.Cross(eastTangent, northTangent);
        if (!LunarVectorMath.TryNormalize(normal, out normal))
        {
            error = "The DEM neighborhood did not define a stable surface normal.";
            return false;
        }
        if (LunarVectorMath.Dot(normal, up) < 0.0)
            normal = normal * -1.0;

        site = new LunarSurfaceSite
        {
            latitudeDeg = latitudeDeg,
            longitudeEastDeg = center.longitudeEastDeg,
            elevationMeters = center.elevationMeters,
            radiusMeters = center.radiusMeters,
            positionMeters = p0,
            radialUp = up,
            east = east,
            north = north,
            surfaceNormal = normal
        };
        return true;
    }

    public static DoubleVector3 RadialUp(double latitudeDeg, double longitudeEastDeg)
    {
        double latitude = latitudeDeg * DegreesToRadians;
        double longitude = LunarCoordinates.NormalizeLongitudeEast(longitudeEastDeg) * DegreesToRadians;
        double cosLatitude = Math.Cos(latitude);
        return new DoubleVector3(
            cosLatitude * Math.Cos(longitude),
            cosLatitude * Math.Sin(longitude),
            Math.Sin(latitude));
    }

    public static void LocalBasis(
        double latitudeDeg,
        double longitudeEastDeg,
        out DoubleVector3 east,
        out DoubleVector3 north)
    {
        double latitude = latitudeDeg * DegreesToRadians;
        double longitude = LunarCoordinates.NormalizeLongitudeEast(longitudeEastDeg) * DegreesToRadians;
        east = new DoubleVector3(-Math.Sin(longitude), Math.Cos(longitude), 0.0);
        north = new DoubleVector3(
            -Math.Sin(latitude) * Math.Cos(longitude),
            -Math.Sin(latitude) * Math.Sin(longitude),
            Math.Cos(latitude));
    }

    public static void GreatCircleDestination(
        double latitudeDeg,
        double longitudeEastDeg,
        DoubleVector3 tangentDirection,
        double distanceMeters,
        double referenceRadiusMeters,
        out double destinationLatitudeDeg,
        out double destinationLongitudeEastDeg)
    {
        DoubleVector3 start = RadialUp(latitudeDeg, longitudeEastDeg);
        DoubleVector3 tangent = tangentDirection - start * LunarVectorMath.Dot(tangentDirection, start);
        if (!LunarVectorMath.TryNormalize(tangent, out tangent))
        {
            destinationLatitudeDeg = latitudeDeg;
            destinationLongitudeEastDeg = LunarCoordinates.NormalizeLongitudeEast(longitudeEastDeg);
            return;
        }

        double angularDistance = distanceMeters / referenceRadiusMeters;
        DoubleVector3 destination = start * Math.Cos(angularDistance) +
                                    tangent * Math.Sin(angularDistance);
        if (!LunarVectorMath.TryNormalize(destination, out destination))
        {
            destinationLatitudeDeg = latitudeDeg;
            destinationLongitudeEastDeg = LunarCoordinates.NormalizeLongitudeEast(longitudeEastDeg);
            return;
        }

        double z = Math.Max(-1.0, Math.Min(1.0, destination.z));
        destinationLatitudeDeg = Math.Asin(z) * RadiansToDegrees;
        destinationLongitudeEastDeg = LunarCoordinates.NormalizeLongitudeEast(
            Math.Atan2(destination.y, destination.x) * RadiansToDegrees);
    }

    public static DoubleVector3 PointOnReferenceFrame(double latitudeDeg, double longitudeEastDeg, double radiusMeters)
        => RadialUp(latitudeDeg, longitudeEastDeg) * radiusMeters;

    private static bool TryOffsetPoint(
        LunarDEMReader demReader,
        double latitudeDeg,
        double longitudeEastDeg,
        DoubleVector3 tangent,
        double distanceMeters,
        out DoubleVector3 point,
        out string error)
    {
        point = default;
        LunarSurfaceGeometry.GreatCircleDestination(
            latitudeDeg, longitudeEastDeg, tangent, distanceMeters,
            demReader.ReferenceRadiusMeters,
            out double sampleLatitude, out double sampleLongitude);
        if (!demReader.TryGetElevation(sampleLatitude, sampleLongitude,
                out LolaElevationSample sample, out error))
            return false;
        point = PointOnReferenceFrame(sampleLatitude, sample.longitudeEastDeg, sample.radiusMeters);
        return true;
    }
}
