namespace CaissonWallUi;

/// <summary>Numerical guard for Revit's minimum bend constraint; never changes the drawn path.</summary>
public static class BendConstraint
{
    public const double GuardMm=.001;
    public const double TypeMatchToleranceMm=.0000001;

    public static double Resolve(double diameterMm,double minimumPathInnerDiameterMm)
    {
        if(!double.IsFinite(diameterMm)||diameterMm<=0||double.IsNaN(minimumPathInnerDiameterMm)
            ||minimumPathInnerDiameterMm<=0)
            throw new InvalidOperationException("Bend radius is too small for the current bar diameter.");
        var fallback=4*diameterMm;
        // Straight bars and generous arcs retain the existing default type constraint.
        if(minimumPathInnerDiameterMm>fallback+GuardMm)return fallback;
        var limit=Math.Min(fallback,minimumPathInnerDiameterMm);
        // A tiny rounding-noise allowance makes equivalent rotations share a type name.
        // The 0.001 mm guard remains at least 0.000999999 mm after this normalization.
        var constraint=Math.Floor(limit*1000+0.000001)/1000-GuardMm;
        constraint=Math.Round(constraint,3,MidpointRounding.AwayFromZero);
        if(constraint<=0)throw new InvalidOperationException("Bend diameter is below the modeling tolerance. Adjust the shape.");
        return constraint;
    }

    public static bool Matches(double actualMm,double expectedMm)=>
        double.IsFinite(actualMm)&&Math.Abs(actualMm-expectedMm)<=TypeMatchToleranceMm;
}
