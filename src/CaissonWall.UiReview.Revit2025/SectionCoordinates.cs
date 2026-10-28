using System;

namespace CaissonWallUi;

/// <summary>
/// Display coordinates for the radial section, in millimetres: the inner wall's
/// top corner is (0,0), x is rightward and y is downward. Stored sketch data stays rho,z.
/// </summary>
public static class SectionCoordinates
{
    public static (double X, double Y) ToDisplay(SketchPoint point, double innerRadiusMm)
    {
        ArgumentNullException.ThrowIfNull(point);
        return ToDisplay(point.Rho, point.Z, innerRadiusMm);
    }

    public static (double X, double Y) ToDisplay(double rho, double z, double innerRadiusMm)
    {
        double x = rho - innerRadiusMm;
        return (x == 0 ? 0 : x, z == 0 ? 0 : -z);
    }

    public static SketchPoint ToInternal(double x, double y, double innerRadiusMm)
        => new(x + innerRadiusMm, y == 0 ? 0 : -y);
}
