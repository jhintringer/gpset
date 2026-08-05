namespace GPSet;

public readonly record struct GeoPosition(double Latitude, double Longitude)
{
    public bool IsValid =>
        double.IsFinite(Latitude) && Latitude is >= -90 and <= 90 &&
        double.IsFinite(Longitude) && Longitude is >= -180 and <= 180;
}
