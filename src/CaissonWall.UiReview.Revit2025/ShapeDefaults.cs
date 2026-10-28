namespace CaissonWallUi;

/// <summary>Prepares editable templates only; imported model centreline snapshots stay unchanged.</summary>
public static class ShapeDefaults
{
    public static bool TryPrepare(ShapeTemplate source,out ShapeTemplate prepared,out string error)
    {
        prepared=source.Copy();
        if(!FilletGeometry.TryApplyDefaults(source.Segments,FilletGeometry.DefaultDiameterMm,out var segments,out _,out error))return false;
        prepared.Segments=segments;return true;
    }
}
