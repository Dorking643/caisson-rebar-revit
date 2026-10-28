using System.Text.Json;

namespace CaissonWallUi;

public enum SlabGroupKind { Orthogonal, Ring, Radial }

public sealed class SlabCheckSettings
{
    public double TopCoverMm { get; set; }=40;
    public double BottomCoverMm { get; set; }=40;
    public double SideCoverMm { get; set; }=40;
    // User-authorized linear penetration allowance only; unrelated to concrete cover or path accuracy.
    public double CollisionToleranceMm { get; set; }
    // Nominal covers remain placement inputs; legacy drafts cannot reinstate cover blockers.
    public bool AllowCoverViolation { get => true; set { } }
    // User-disabled slab cover/solid-boundary diagnostics apply to both preview and native readback.
    public bool AllowConcreteBoundaryViolation { get => true; set { } }
    // Explicit collision override; CollisionToleranceMm remains the reference value for diagnostics.
    public bool AllowCollisionViolation { get; set; }
    public bool CoverConfirmed { get; set; }
}

public sealed class SlabGroup
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    string _name="Rebar group";
    public string Name { get => _name; set => _name=SlabGroupNames.ToEnglish(value); }
    public string TypeId { get; set; }="";
    public string ShapeTemplateId { get; set; }="";
    public string SourceNote { get; set; }="";
    public SlabGroupKind Kind { get; set; }
    public bool Enabled { get; set; }=true;
    public bool PositionConfirmed { get; set; }=true;
    public List<SketchSegment> ShapeSnapshot { get; set; }=new();
    public int StretchSegmentIndex { get; set; }=-1;
    public double ReferenceSpan { get; set; }
    public double Z { get; set; }=-60;
    public double DirectionDeg { get; set; }
    public double StartOffset { get; set; }=-1500;
    public double EndOffset { get; set; }=1500;
    public double Spacing { get; set; }=150;
    public bool UseEndOffset { get; set; }=true;
    public int Count { get; set; }=1;
    public double Radius { get; set; }=500;
    public double RadiusStep { get; set; }=150;
    public double InnerRadius { get; set; }
    public double OuterRadius { get; set; }=1500;
    public double StartAngle { get; set; }
    public double AngleStep { get; set; }=30;
    public bool UseAngleStep { get; set; }
    public bool FitToBoundary { get; set; }=true;
    public double AlongOffset { get; set; }
    public double GroupBoundaryRadiusMm { get; set; }
    // Explicit source-authorized penetration of the upper surface; never exempts side/bottom surfaces.
    public bool AllowTopExtension { get; set; }
    public override string ToString()=>Name;
}

/// <summary>Translate generated legacy labels without changing group identity, source notes, or geometry.</summary>
public static class SlabGroupNames
{
    public static string ToEnglish(string? name)
    {
        if(string.IsNullOrEmpty(name))return name??"";
        // Only generated labels are migrated; unrelated user-defined names remain intact.
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^(?:N\d+(?:-[XY])?\s*·|圆环(?:\s|\d|$)|正交(?:\s|\d|$)|径向(?:\s|\d|$))"))return name;
        name=System.Text.RegularExpressions.Regex.Replace(name,@"原第\s*(\d+)\s*根","Source Bar $1");
        name=System.Text.RegularExpressions.Regex.Replace(name,@"余\s*(\d+)\s*根","Remaining $1 bars");
        foreach(var (chinese,english) in new[]{
            ("定位待核未启用","Position Pending - Disabled"),("待核未启用","Review Pending - Disabled"),
            ("上层第二向","Top Direction 2"),("下层直筋","Bottom Straight"),("周边插筋","Perimeter Starter "),
            ("上层","Top"),("下层","Bottom"),("马凳","Chair "),("插筋","Starter "),("待核","Review Pending"),
            ("圆环","Ring"),("正交","Orthogonal"),("径向","Radial")})name=name.Replace(chinese,english,StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(name,@"(\d+)\s*根","$1 bars");
    }
}

/// <summary>All coordinates are mm relative to the slab centre's top surface, Z upward.
/// Host.TopZmm is that real centre-top world elevation, not the bounding-box maximum at a raised rim.</summary>
public sealed class SlabDocument
{
    public string Schema { get; set; }="caisson-slab-ui/1";
    public string SessionId { get; set; }=Guid.NewGuid().ToString("N");
    public string ModelOwnerId { get; set; }=Guid.NewGuid().ToString("N");
    public HostContext Host { get; set; }=new();
    public List<ReviewBarType> Types { get; set; }=new();
    public List<ShapeTemplate> Shapes { get; set; }=new();
    public List<SlabGroup> Groups { get; set; }=new();
    public SlabCheckSettings CheckSettings { get; set; }=new();
    public List<ExistingBarPath> ExistingBars { get; set; }=new();
    public static readonly JsonSerializerOptions JsonOptions=ReviewDocument.JsonOptions;
    public string ToJson()=>JsonSerializer.Serialize(this,JsonOptions);
    public static SlabDocument FromJson(string text)=>JsonSerializer.Deserialize<SlabDocument>(text,JsonOptions)??throw new ArgumentException("Slab draft is empty.");
}
