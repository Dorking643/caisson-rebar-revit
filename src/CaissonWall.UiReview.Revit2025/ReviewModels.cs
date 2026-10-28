using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaissonWallUi;

public sealed class HostContext
{
    public string DocumentTitle { get; set; } = "";
    public string DocumentKey { get; set; } = "";
    public string HostName { get; set; } = "";
    public long HostId { get; set; }
    public double InnerRadiusMm { get; set; }
    public double OuterRadiusMm { get; set; }
    public double HeightMm { get; set; }
    public bool IsVerified { get; set; }
    public string HostUniqueId { get; set; } = "";
    public double OriginXmm { get; set; }
    public double OriginYmm { get; set; }
    public double TopZmm { get; set; }
    public List<SketchPoint> SectionBoundary { get; set; } = new();
    // Main axisymmetric profile and separately verified finite cylindrical voids, all in host-local mm.
    public List<HostOpening> HostOpenings { get; set; } = new();
    public bool OpeningGeometryVerified { get; set; }
    public string OpeningGeometryNote { get; set; } = "";
    public bool ExistingScanComplete { get; set; }
    public string ExistingScanNote { get; set; } = "";
    public string ExistingSnapshotUtc { get; set; } = "";
}

public sealed class HostOpening
{
    public string Id { get; set; } = "";
    public Point3 AxisStart { get; set; } = new();
    public Point3 AxisEnd { get; set; } = new();
    public double RadiusMm { get; set; }
    public int SourceFaceCount { get; set; }
}

public sealed class ReviewBarType
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Type 1";
    public string Grade { get; set; } = "";
    public double DiameterMm { get; set; } = 16;
    public override string ToString() => $"{(string.IsNullOrWhiteSpace(Grade) ? "Unspecified grade" : Grade)}  Φ{DiameterMm:0.###}";
}

public sealed class SketchPoint
{
    public double Rho { get; set; }
    public double Z { get; set; }
    public SketchPoint() { }
    public SketchPoint(double rho, double z) { Rho = rho; Z = z; }
    public SketchPoint Copy() => new(Rho, Z);
}

public enum DraftKind { Ring, Profile }
public enum SegmentKind { Line, Arc }
public enum ReviewTool { Select, Ring, Polyline, Arc, InsertShape }
public enum PointRole { None, Ring, Start, Middle, End }
public enum RingArrayAxis { X, Y }
public enum ProfileArrayMode { Count, AngleStep }

public sealed class GroupParameters
{
    public bool Enabled { get; set; }
    public RingArrayAxis RingAxis { get; set; } = RingArrayAxis.Y;
    public double RingStepMm { get; set; } = 150;
    public int Count { get; set; } = 1;
    public ProfileArrayMode ProfileMode { get; set; } = ProfileArrayMode.Count;
    public double StartAngleDeg { get; set; }
    public double AngleStepDeg { get; set; } = 30;
    public GroupParameters Copy() => new()
    {
        Enabled = Enabled, RingAxis = RingAxis, RingStepMm = RingStepMm, Count = Count,
        ProfileMode = ProfileMode, StartAngleDeg = StartAngleDeg, AngleStepDeg = AngleStepDeg
    };
}

public sealed class SketchSegment
{
    public SegmentKind Kind { get; set; }
    public SketchPoint Start { get; set; } = new();
    public SketchPoint Middle { get; set; } = new();
    public SketchPoint End { get; set; } = new();
}

public sealed class ShapeTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Shape 1";
    // Local u-v coordinates: SketchPoint.Rho is u; SketchPoint.Z is v. Base B=(0,0).
    public List<SketchSegment> Segments { get; set; } = new();
    public ShapeTemplate Copy() => new() { Id = Id, Name = Name, Segments = ShapePlacement.CopySegments(Segments) };
    public override string ToString() => Name;
}

public sealed class ReviewDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";
    public DraftKind Kind { get; set; }
    public SketchPoint RingPoint { get; set; } = new();
    public List<SketchSegment> Segments { get; set; } = new();
    public string? ShapeTemplateId { get; set; }
    public string? ShapeName { get; set; }
    public SketchPoint? InsertionPoint { get; set; }
    public List<SketchSegment> ShapeSnapshot { get; set; } = new();
    public PlacementDraft Placement { get; set; } = new();
    // Old JSON without this property remains a single draft; legacy Placement.Count is not migrated.
    public GroupParameters Group { get; set; } = new();
    public bool AllowTopExtension { get; set; }
    public string SourceMark { get; set; } = "";
    public List<long> SourceElementIds { get; set; } = new();
    public List<string> SourceUniqueIds { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class PlacementDraft
{
    public string Mode { get; set; } = "周向";
    public double StartAngleDeg { get; set; }
    public int Count { get; set; } = 1;
    public double SpacingMm { get; set; } = 150;
    public double CoverMm { get; set; } = 40;
}

public sealed class ReviewDocument
{
    public string Stage { get; set; } = "ui_review_only";
    public string Schema { get; set; } = "caisson-wall-ui/2";
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string ModelOwnerId { get; set; } = Guid.NewGuid().ToString("N");
    public string ImportSourceDocumentKey { get; set; } = "";
    public string SavedUtc { get; set; } = "";
    public bool CreatesRevitObjects { get; set; } = false;
    public HostContext Host { get; set; } = new();
    public List<ReviewBarType> Types { get; set; } = new();
    public List<ShapeTemplate> Shapes { get; set; } = new();
    public List<ReviewDraft> Drafts { get; set; } = new();
    public WallCheckSettings CheckSettings { get; set; } = new();
    public List<ExistingBarPath> ExistingBars { get; set; } = new();
    public ReviewTool ActiveTool { get; set; }
    public string? ActiveProfileId { get; set; }
    public List<SketchPoint> PendingPoints { get; set; } = new();
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static ReviewDocument FromJson(string text) => JsonSerializer.Deserialize<ReviewDocument>(text, JsonOptions) ?? new();
}

// Placement of two-dimensional UI sketches only; no reinforcement layout or model creation.
public static class ShapePlacement
{
    public static List<SketchSegment> CopySegments(IEnumerable<SketchSegment> segments, double du = 0, double dv = 0)
        => segments.Select(s => new SketchSegment
        {
            Kind = s.Kind,
            Start = new(s.Start.Rho + du, s.Start.Z + dv),
            Middle = new(s.Middle.Rho + du, s.Middle.Z + dv),
            End = new(s.End.Rho + du, s.End.Z + dv)
        }).ToList();

    public static ReviewDraft Insert(ShapeTemplate template, SketchPoint at, string typeId, string name)
    {
        var draft = new ReviewDraft
        {
            Kind = DraftKind.Profile, Name = name, TypeId = typeId,
            ShapeTemplateId = template.Id, ShapeName = template.Name,
            ShapeSnapshot = CopySegments(template.Segments), InsertionPoint = at.Copy()
        };
        Move(draft, at);
        return draft;
    }

    public static void Move(ReviewDraft draft, SketchPoint at)
    {
        if (draft.Kind == DraftKind.Ring) { draft.RingPoint = at.Copy(); return; }
        if (draft.ShapeSnapshot.Count == 0) throw new InvalidOperationException("Shape snapshot is missing.");
        draft.InsertionPoint = at.Copy();
        draft.Segments = CopySegments(draft.ShapeSnapshot, at.Rho, at.Z);
    }

    public static void Upgrade(ReviewDocument document)
    {
        foreach (var d in document.Drafts.Where(d => d.Kind == DraftKind.Profile && d.Segments.Count > 0))
        {
            if (d.ShapeSnapshot.Count > 0 && d.InsertionPoint is not null) continue;
            var anchor = d.Segments[0].Start.Copy();
            var shape = new ShapeTemplate { Name = "Existing · " + d.Name, Segments = CopySegments(d.Segments, -anchor.Rho, -anchor.Z) };
            document.Shapes.Add(shape);
            d.ShapeTemplateId = shape.Id; d.ShapeName = shape.Name;
            d.ShapeSnapshot = CopySegments(shape.Segments); d.InsertionPoint = anchor;
        }
        document.Schema = "caisson-wall-ui/2";
        document.ActiveTool = ReviewTool.Select;
        document.PendingPoints.Clear(); document.ActiveProfileId = null;
    }
}

public sealed class ReviewSelection
{
    public string? DraftId { get; set; }
    public int SegmentIndex { get; set; } = -1;
    public int MemberIndex { get; set; } = -1;
    public PointRole PointRole { get; set; }
}

// These helpers only edit/render a two-dimensional sketch. They are not engineering checks.
public static class SketchDrawing
{
    public static double Distance(SketchPoint a, SketchPoint b) => Math.Sqrt(Math.Pow(a.Rho - b.Rho, 2) + Math.Pow(a.Z - b.Z, 2));
    public static bool Same(SketchPoint a, SketchPoint b) => Distance(a, b) < 0.000001;
    public static IEnumerable<SketchPoint> Points(ReviewDraft draft)
    {
        if (draft.Kind == DraftKind.Ring) { yield return draft.RingPoint; yield break; }
        foreach (var segment in draft.Segments)
        {
            yield return segment.Start;
            if (segment.Kind == SegmentKind.Arc) yield return segment.Middle;
            yield return segment.End;
        }
    }
    public static SketchPoint? SelectedPoint(ReviewDraft? draft, ReviewSelection selection)
    {
        if (draft == null) return null;
        if (draft.Kind == DraftKind.Ring) return draft.RingPoint;
        if (selection.SegmentIndex < 0 || selection.SegmentIndex >= draft.Segments.Count) return null;
        var segment = draft.Segments[selection.SegmentIndex];
        return selection.PointRole switch { PointRole.Start => segment.Start, PointRole.Middle => segment.Middle, PointRole.End => segment.End, _ => null };
    }
    public static bool Arc(SketchSegment s, out SketchPoint center, out double radius, out double start, out double sweep)
    {
        double ax = s.Start.Rho, ay = s.Start.Z, bx = s.Middle.Rho, by = s.Middle.Z, cx = s.End.Rho, cy = s.End.Z;
        double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        center = new(); radius = start = sweep = 0;
        if (Math.Abs(d) < 0.000001) return false;
        double aa = ax * ax + ay * ay, bb = bx * bx + by * by, cc = cx * cx + cy * cy;
        center = new((aa * (by - cy) + bb * (cy - ay) + cc * (ay - by)) / d, (aa * (cx - bx) + bb * (ax - cx) + cc * (bx - ax)) / d);
        radius = Distance(center, s.Start);
        start = Math.Atan2(ay - center.Z, ax - center.Rho);
        double mid = Positive(Math.Atan2(by - center.Z, bx - center.Rho) - start);
        double end = Positive(Math.Atan2(cy - center.Z, cx - center.Rho) - start);
        sweep = mid <= end ? end : end - 2 * Math.PI;
        return double.IsFinite(radius) && radius > 0;
    }
    static double Positive(double value) { value %= 2 * Math.PI; return value < 0 ? value + 2 * Math.PI : value; }
    public static List<SketchPoint> Polyline(SketchSegment segment)
    {
        if (segment.Kind == SegmentKind.Line) return new() { segment.Start, segment.End };
        if (!Arc(segment, out var c, out double r, out double a, out double sweep)) return new() { segment.Start, segment.Middle, segment.End };
        int count = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) * 32), 8, 200);
        return Enumerable.Range(0, count + 1).Select(i => new SketchPoint(c.Rho + r * Math.Cos(a + sweep * i / count), c.Z + r * Math.Sin(a + sweep * i / count))).ToList();
    }
    public static void MovePoint(ReviewDraft draft, SketchPoint original, SketchPoint target)
    {
        // Shared endpoints remain connected when a node is edited numerically.
        var matches = Points(draft).Where(p => Same(p, original)).ToArray();
        foreach (var p in matches) { p.Rho = target.Rho; p.Z = target.Z; }
    }
}
