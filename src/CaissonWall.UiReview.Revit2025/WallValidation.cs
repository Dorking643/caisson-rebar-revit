using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace CaissonWallUi;

public sealed class WallCheckSettings
{
    public double InnerCoverMm { get; set; } = 40;
    public double OuterCoverMm { get; set; } = 40;
    public double TopCoverMm { get; set; } = 40;
    public double BottomCoverMm { get; set; } = 40;
    public bool CoverConfirmed { get; set; }
    // Clear-cover shortfalls are advisory in the wall editor. Keep the serialized
    // field compatible with older drafts without allowing a saved false to block creation.
    public bool AllowCoverViolation { get => true; set { } }
    public bool AllowCollisionViolation { get; set; }
}

public sealed class Point3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public Point3() { }
    public Point3(double x, double y, double z) { X=x; Y=y; Z=z; }
}

public sealed class Curve3
{
    public SegmentKind Kind { get; set; }
    public Point3 Start { get; set; } = new();
    public Point3 Middle { get; set; } = new();
    public Point3 End { get; set; } = new();
}

public sealed class BarPath3
{
    public string DraftId { get; set; } = "";
    public int MemberIndex { get; set; }
    public string TypeId { get; set; } = "";
    public double DiameterMm { get; set; }
    public List<Curve3> Curves { get; set; } = new();
}

public sealed class ExistingBarPath
{
    public bool AllowTopExtension { get; set; }
    public long ElementId { get; set; }
    public int PositionIndex { get; set; }
    public double DiameterMm { get; set; }
    public List<Curve3> Curves { get; set; } = new();
}

public enum ValidationKind { Cover, Collision, InvalidPath, Context, Incomplete, HostOpening }

public sealed class ValidationIssue
{
    public string DraftId { get; set; } = "";
    public int MemberIndex { get; set; } = -1;
    public string OtherDraftId { get; set; } = "";
    public int OtherMemberIndex { get; set; } = -1;
    public long? ExistingElementId { get; set; }
    public ValidationKind Kind { get; set; }
    public string Message { get; set; } = "";
    public double? ClearanceMm { get; set; }
}

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; set; } = new();
    public List<ValidationIssue> AcceptedCollisions { get; set; } = new();
    public List<ValidationIssue> AcceptedCoverViolations { get; set; } = new();
    public List<ValidationIssue> AcceptedHostOpenings { get; set; } = new();
    public List<BarPath3> Bars { get; set; } = new();
    public bool IsComplete { get; set; } = true;
    public bool CanBuild => IsComplete && Issues.Count == 0 && Bars.Count > 0;
    [JsonIgnore] public IEnumerable<ValidationIssue> VisibleIssues => Issues.Concat(AcceptedCollisions).Concat(AcceptedCoverViolations).Concat(AcceptedHostOpenings);
    public IEnumerable<ValidationIssue> ForDraft(string id) => VisibleIssues.Where(i => i.DraftId == id || i.OtherDraftId == id);
    public bool HasIssue(string id, int member = -1) => VisibleIssues.Any(i =>
        (i.DraftId == id && (member < 0 || i.MemberIndex < 0 || i.MemberIndex == member)) ||
        (i.OtherDraftId == id && (member < 0 || i.OtherMemberIndex < 0 || i.OtherMemberIndex == member)));
}

/// <summary>Host-local mm geometry. Reports geometric cover/penetration, not structural-code approval.</summary>
public static class WallValidation
{
    public const double ContactToleranceMm = .01;
    public const double DistanceErrorMm = .001;
    const int MaxBars = 5000, MaxPairWork = 2000000;

    public static ValidationReport Check(ReviewDocument doc)
    {
        var report = new ValidationReport();
        if (doc == null) { Context(report, "Draft does not exist."); return report; }
        var host = doc.Host; var settings = doc.CheckSettings;
        if (!host.IsVerified) Context(report, "Host geometry has not been verified.");
        if (!host.ExistingScanComplete) Context(report, string.IsNullOrWhiteSpace(host.ExistingScanNote) ? "Existing rebar has not been loaded." : host.ExistingScanNote);
        if (!settings.CoverConfirmed) Context(report, "Confirm the clear cover.");
        if (!new[] {settings.InnerCoverMm, settings.OuterCoverMm, settings.TopCoverMm, settings.BottomCoverMm}.All(v => double.IsFinite(v) && v >= 0))
        { Context(report, "Cover must be a finite, nonnegative value."); return report; }
        if (!double.IsFinite(host.InnerRadiusMm) || !double.IsFinite(host.OuterRadiusMm) || !double.IsFinite(host.HeightMm) ||
            host.OuterRadiusMm>1e9 || host.HeightMm>1e9 ||
            host.InnerRadiusMm < 0 || host.OuterRadiusMm <= host.InnerRadiusMm || host.HeightMm <= 0)
        { Context(report, "Host dimensions are invalid."); return report; }
        var boundary = Boundary(host);
        if (boundary.Count < 3 || boundary.Any(p => !double.IsFinite(p.Rho) || !double.IsFinite(p.Z)))
        { Context(report, "Host section boundary is invalid."); return report; }
        try { ValidateOpenings(host); }
        catch(Exception ex) { Context(report,ex.Message); return report; }
        var candidates = new List<ModelBar>();
        var ids = new HashSet<string>();
        foreach (var draft in doc.Drafts)
        {
            if (!ids.Add(draft.Id)) { Invalid(report, draft, "Duplicate draft ID."); continue; }
            var type = doc.Types.FirstOrDefault(t => t.Id == draft.TypeId);
            if (type == null || !double.IsFinite(type.DiameterMm) || type.DiameterMm <= 0 || type.DiameterMm>1e6)
            { Invalid(report, draft, "Select a valid rebar type."); continue; }
            if (!GroupGeometry.TryMembers(draft, out var members, out var error)) { Invalid(report,draft,error); continue; }
            if (candidates.Count + members.Count > MaxBars) { Incomplete(report, $"Check no more than {MaxBars} bars at a time."); break; }
            List<CurveEval> profile;
            try
            {
                if (draft.Kind == DraftKind.Ring)
                {
                    if (!double.IsFinite(draft.RingPoint.Rho) || draft.RingPoint.Rho <= type.DiameterMm / 2 || !double.IsFinite(draft.RingPoint.Z))
                        throw new ArgumentException("Ring radius or elevation is invalid.");
                    profile = new();
                }
                else
                {
                    if (draft.Segments.Count == 0) throw new ArgumentException("Shape path is empty.");
                    profile = draft.Segments.Select(s => new CurveEval(ToCurve(s,0))).ToList();
                    ValidatePath(profile,type.DiameterMm,settings.AllowCollisionViolation,report,draft);
                }
            }
            catch (Exception ex) { Invalid(report,draft,ex.Message); continue; }
            foreach (var member in members)
            {
                try
                {
                    var path = new BarPath3 { DraftId=draft.Id, MemberIndex=member.Index, TypeId=draft.TypeId, DiameterMm=type.DiameterMm };
                    ModelBar bar;
                    if (draft.Kind == DraftKind.Ring)
                    {
                        double rho=draft.RingPoint.Rho+member.RhoOffsetMm, z=draft.RingPoint.Z+member.ZOffsetMm;
                        if (rho <= type.DiameterMm/2) throw new ArgumentException("Ring radius is smaller than the bar radius.");
                        path.Curves = RingCurves(rho,z);
                        bar = new ModelBar(path, path.Curves.Select(c=>new CurveEval(c)).ToList(), new(rho,z), null);
                        CheckCover(report, draft, member.Index, new[] {new CurveEval(new Curve3 { Start=new(rho,0,z),End=new(rho,0,z) },true)}, boundary,host,settings,type.DiameterMm);
                    }
                    else
                    {
                        path.Curves = draft.Segments.Select(s=>ToCurve(s,member.AngleDeg)).ToList();
                        bar = new ModelBar(path,path.Curves.Select(c=>new CurveEval(c)).ToList(),null,profile);
                        // Every rotated member has the same radial/vertical cover; one source issue highlights all copies.
                        if (member.Index == 0) CheckCover(report,draft,-1,profile,boundary,host,settings,type.DiameterMm);
                    }
                    CheckOpenings(report,path,host);
                    candidates.Add(bar); report.Bars.Add(path);
                }
                catch (Exception ex) { Invalid(report,draft,$"Bar {member.Index+1}: {ex.Message}",member.Index); }
            }
        }
        var existing = new List<ModelBar>();
        foreach (var old in doc.ExistingBars)
        {
            try
            {
                if (!double.IsFinite(old.DiameterMm) || old.DiameterMm <= 0 || old.DiameterMm>1e6 || old.Curves.Count == 0) throw new ArgumentException();
                var curves = old.Curves.Select(c=>new CurveEval(c)).ToList();
                existing.Add(new ModelBar(new BarPath3 { DiameterMm=old.DiameterMm,Curves=old.Curves },curves,DetectRing(curves),DetectProfile(old.Curves)) { ExistingId=old.ElementId });
            }
            catch { Incomplete(report,$"Geometry of existing rebar {old.ElementId} could not be checked."); }
        }
        int work=0;
        var all = candidates.Concat(existing).OrderBy(b=>b.Box.MinX).ToArray();
        for (int i=0;i<all.Length;i++)
        for (int j=i+1;j<all.Length && all[j].Box.MinX <= all[i].Box.MaxX + ContactToleranceMm;j++)
        {
            var a=all[i]; var b=all[j];
            if (a.ExistingId.HasValue && b.ExistingId.HasValue || !a.Box.Overlaps(b.Box)) continue;
            if (++work > MaxPairWork || report.Issues.Count >= 2000)
            {
                if(settings.AllowCollisionViolation)report.AcceptedCollisions.Add(new(){Kind=ValidationKind.Incomplete,Message="Collision warning only: check limit exceeded; some intersections remain unquantified."});
                else Incomplete(report,"Check limit exceeded. Place bars in smaller batches.");
                return report;
            }
            if (a.ExistingId.HasValue) (a,b)=(b,a);
            try
            {
                double threshold=(a.Path.DiameterMm+b.Path.DiameterMm)/2;
                double distance=BarDistance(a,b,threshold);
                double clearance=distance-threshold;
                if (clearance < -ContactToleranceMm)
                {
                    var other=b.ExistingId.HasValue ? $"existing rebar {b.ExistingId.Value}" : $"{doc.Drafts.First(d=>d.Id==b.Path.DraftId).Name}, bar {b.Path.MemberIndex+1}";
                    var issue=new ValidationIssue { DraftId=a.Path.DraftId,MemberIndex=a.Path.MemberIndex,
                        OtherDraftId=b.Path.DraftId,OtherMemberIndex=b.Path.MemberIndex,ExistingElementId=b.ExistingId,
                        Kind=ValidationKind.Collision,ClearanceMm=clearance,Message=$"Collision with {other}; overlap approx. {-clearance:0.###} mm (distance accuracy {DistanceErrorMm} mm)." };
                    if(settings.AllowCollisionViolation){issue.Message="Collision warning only · "+issue.Message;report.AcceptedCollisions.Add(issue);}
                    else report.Issues.Add(issue);
                }
            }
            catch (Exception ex)
            {
                if(settings.AllowCollisionViolation)report.AcceptedCollisions.Add(new(){DraftId=a.Path.DraftId,MemberIndex=a.Path.MemberIndex,OtherDraftId=b.Path.DraftId,
                    OtherMemberIndex=b.Path.MemberIndex,ExistingElementId=b.ExistingId,Kind=ValidationKind.Incomplete,Message=$"Collision warning only · Distance not quantified: {ex.Message}"});
                else {Incomplete(report,$"Distance check incomplete: {ex.Message}");return report;}
            }
        }
        return report;
    }

    public static double CurveDistanceMm(Curve3 first,Curve3 second) => Distance(new CurveEval(first),new CurveEval(second));
    public static double CertifiedSeparationMm(ExistingBarPath first,ExistingBarPath second)
    {
        ModelBar Model(ExistingBarPath p){var c=p.Curves.Select(x=>new CurveEval(x)).ToList();return new(new BarPath3{DiameterMm=p.DiameterMm,Curves=p.Curves},c,DetectRing(c),DetectProfile(p.Curves));}
        double threshold=(first.DiameterMm+second.DiameterMm)/2;
        return BarDistance(Model(first),Model(second),threshold+DistanceErrorMm)-threshold-DistanceErrorMm;
    }

    /// <summary>Independent re-check of read-back curves. No planned coordinates are substituted for native geometry.</summary>
    public static ValidationReport CheckActual(ReviewDocument design,List<ExistingBarPath> own,List<ExistingBarPath> others)
    {
        var actual=ReviewDocument.FromJson(design.ToJson());actual.Drafts.Clear();actual.Types.Clear();actual.ExistingBars=others;
        var rejected=new List<ValidationIssue>();
        foreach(var bar in own)
        {
            string id=$"element/{bar.ElementId}/{bar.PositionIndex}";
            try
            {
                var evals=bar.Curves.Select(c=>new CurveEval(c)).ToList();
                if(evals.Count==0)throw new ArgumentException("Actual path is empty.");
                var ring=DetectRing(evals);
                var type=new ReviewBarType{Id=id,Name=id,DiameterMm=bar.DiameterMm};actual.Types.Add(type);
                var draft=new ReviewDraft{Id=id,Name=$"Actual rebar {bar.ElementId}",TypeId=id,AllowTopExtension=bar.AllowTopExtension};
                if(ring!=null){draft.Kind=DraftKind.Ring;draft.RingPoint=ring;}
                else
                {
                    double angle=Math.Atan2(bar.Curves[0].Start.Y,bar.Curves[0].Start.X);
                    double ca=Math.Cos(angle),sa=Math.Sin(angle);
                    SketchPoint Radial(Point3 p)
                    {
                        double rho=p.X*ca+p.Y*sa,off=-p.X*sa+p.Y*ca;
                        if(Math.Abs(off)>.001 || rho<=0)throw new ArgumentException("Actual rebar is outside the expected radial plane.");
                        return new(rho,p.Z);
                    }
                    draft.Kind=DraftKind.Profile;
                    draft.Segments=bar.Curves.Select(c=>new SketchSegment{Kind=c.Kind,Start=Radial(c.Start),Middle=c.Kind==SegmentKind.Arc?Radial(c.Middle):new(),End=Radial(c.End)}).ToList();
                    draft.Group=new(){Enabled=true,Count=1,StartAngleDeg=angle*180/Math.PI};
                }
                actual.Drafts.Add(draft);
            }
            catch(Exception ex){rejected.Add(new(){DraftId=id,ExistingElementId=bar.ElementId,Kind=ValidationKind.InvalidPath,Message=$"Actual rebar {bar.ElementId}: {ex.Message}"});}
        }
        var report=Check(actual);report.Issues.AddRange(rejected);
        return report;
    }

    static void Context(ValidationReport report,string text) => report.Issues.Add(new(){Kind=ValidationKind.Context,Message=text});
    static void Invalid(ValidationReport report,ReviewDraft draft,string text,int member=-1) => report.Issues.Add(new(){DraftId=draft.Id,MemberIndex=member,Kind=ValidationKind.InvalidPath,Message=text});
    static void Incomplete(ValidationReport report,string text) { report.IsComplete=false; report.Issues.Add(new(){Kind=ValidationKind.Incomplete,Message=text}); }
    static List<SketchPoint> Boundary(HostContext host) => host.SectionBoundary.Count > 0 ? host.SectionBoundary : new()
    { new(host.InnerRadiusMm,0),new(host.OuterRadiusMm,0),new(host.OuterRadiusMm,-host.HeightMm),new(host.InnerRadiusMm,-host.HeightMm) };
    static Curve3 ToCurve(SketchSegment s,double angle)
    {
        double a=angle*Math.PI/180;
        Point3 Convert(SketchPoint p)=>new(p.Rho*Math.Cos(a),p.Rho*Math.Sin(a),p.Z);
        return new(){Kind=s.Kind,Start=Convert(s.Start),Middle=Convert(s.Middle),End=Convert(s.End)};
    }
    static List<Curve3> RingCurves(double r,double z)=>new()
    {
        new(){Kind=SegmentKind.Arc,Start=new(r,0,z),Middle=new(0,r,z),End=new(-r,0,z)},
        new(){Kind=SegmentKind.Arc,Start=new(-r,0,z),Middle=new(0,-r,z),End=new(r,0,z)}
    };
    static void ValidatePath(List<CurveEval> path,double diameter,bool allowCollision,ValidationReport report,ReviewDraft draft)
    {
        for(int i=0;i<path.Count;i++)
        {
            var c=path[i];
            if (c.Bounds().MinX <= 0) throw new ArgumentException("Radial position must be greater than zero.");
            if (c.IsArc && c.Radius <= diameter/2) throw new ArgumentException("Bend centerline radius must exceed the bar radius.");
            if (i>0)
            {
                if (Len(Sub(path[i-1].At(1),c.At(0))) > .001) throw new ArgumentException("Path endpoints are disconnected.");
                if (Dot(path[i-1].Tangent(1),c.Tangent(0)) < .99999) throw new ArgumentException("Corner is not tangent. Add an arc in the shape editor.");
            }
        }
        bool closed=Len(Sub(path[0].At(0),path[^1].At(1)))<.001;
        if(closed && Dot(path[^1].Tangent(1),path[0].Tangent(0))<.99999) throw new ArgumentException("Closed joint is not tangent.");
        for(int i=0;i<path.Count;i++) for(int j=i+2;j<path.Count;j++)
        {
            if(closed && i==0 && j==path.Count-1)continue;
            // Two straight arms of one tangent quarter-turn are the same local bend neighbourhood.
            // Their finite-radius swept sections overlap locally by construction; do not treat that as a remote collision.
            bool localQuarter=j==i+2&&!path[i].IsArc&&!path[j].IsArc&&path[i+1].IsArc&&Math.Abs(path[i+1].Sweep)<=Math.PI/2+1e-8;
            if(localQuarter)continue;
            if(path[i].Bounds().Expand(diameter/2).Overlaps(path[j].Bounds().Expand(diameter/2)))
            {
                try
                {
                    double clearance=Distance(path[i],path[j])-diameter;
                    if(clearance < -ContactToleranceMm)
                    {
                        if(!allowCollision)throw new ArgumentException("Nonadjacent shape segments collide.");
                        report.AcceptedCollisions.Add(new(){DraftId=draft.Id,Kind=ValidationKind.Collision,ClearanceMm=clearance,
                            Message=$"Collision warning only · Nonadjacent segments {i+1}/{j+1} collide; overlap approx. {-clearance:0.###} mm."});
                    }
                }
                catch(Exception ex) when(allowCollision)
                {report.AcceptedCollisions.Add(new(){DraftId=draft.Id,Kind=ValidationKind.Incomplete,Message="Collision warning only · Self-collision not quantified: "+ex.Message});}
            }
        }
    }

    static void CheckCover(ValidationReport report,ReviewDraft draft,int member,IEnumerable<CurveEval> path,
        List<SketchPoint> polygon,HostContext host,WallCheckSettings settings,double diameter)
    {
        var curves=path.ToArray();
        if(draft.AllowTopExtension && draft.Kind==DraftKind.Profile)
        {
            // Extend only the two top corners; all side, ledge and blade checks remain active.
            double cap=Math.Max(0,curves.Max(c=>c.Bounds().MaxZ))+diameter+Math.Max(settings.InnerCoverMm,settings.OuterCoverMm)+settings.TopCoverMm+10;
            polygon=polygon.Select(p=>new SketchPoint(p.Rho,Math.Abs(p.Z)<.01?cap:p.Z)).ToList();
        }
        double worst=double.PositiveInfinity,physical=double.PositiveInfinity; string side="";bool outside=false;
        foreach(var curve in curves)
        {
            if(new[]{0d,.5,1d}.Any(t=>!Inside(polygon,curve.At(t).X,curve.At(t).Z)))outside=true;
            for(int i=0;i<polygon.Count;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Count];
                if(SketchDrawing.Distance(a,b)<1e-9)continue;
                double cover;string label;
                if(Math.Abs(a.Z)<.01&&Math.Abs(b.Z)<.01) {cover=settings.TopCoverMm;label="Top face";}
                else if(Math.Abs(a.Z+host.HeightMm)<.01&&Math.Abs(b.Z+host.HeightMm)<.01){cover=settings.BottomCoverMm;label="Bottom face";}
                else if(Math.Abs(a.Rho-host.OuterRadiusMm)<.01&&Math.Abs(b.Rho-host.OuterRadiusMm)<.01){cover=settings.OuterCoverMm;label="Outer face";}
                else {cover=settings.InnerCoverMm;label="Inner face / step";}
                var edge=new CurveEval(new Curve3{Start=new(a.Rho,0,a.Z),End=new(b.Rho,0,b.Z)});
                const double physicalError=1e-7;
                double distance=Distance(curve,edge,physicalError);
                // Point/line and point/arc distances are analytic; general arc pairs use a certified lower bound.
                double lower=distance-(curve.IsArc&&!curve.IsPoint&&!edge.IsPoint?physicalError:0);
                physical=Math.Min(physical,lower-diameter/2);
                double remaining=lower-diameter/2-cover;
                if(remaining<worst){worst=remaining;side=label;}
            }
        }
        if(outside||physical < -1e-6)
        {report.Issues.Add(new(){DraftId=draft.Id,MemberIndex=member,Kind=ValidationKind.Cover,ClearanceMm=physical,Message="Rebar extends beyond the main concrete boundary, excluding confirmed top extensions."});return;}
        if(worst < -ContactToleranceMm)
        {
            var issue=new ValidationIssue{DraftId=draft.Id,MemberIndex=member,Kind=ValidationKind.Cover,ClearanceMm=worst,Message=$"{side} cover is short by {-worst:0.###} mm."};
            if(settings.AllowCoverViolation){issue.Message="Cover intrusion accepted · "+issue.Message;report.AcceptedCoverViolations.Add(issue);}
            else report.Issues.Add(issue);
        }
    }

    static void ValidateOpenings(HostContext host)
    {
        if(host.HostOpenings.Count==0)return;
        if(!host.OpeningGeometryVerified)throw new ArgumentException("Opening solids have not been verified. Host voids cannot be ignored.");
        var ids=new HashSet<string>();
        foreach(var hole in host.HostOpenings)
        {
            if(string.IsNullOrWhiteSpace(hole.Id)||!ids.Add(hole.Id)||!Finite(hole.AxisStart)||!Finite(hole.AxisEnd)||
                !double.IsFinite(hole.RadiusMm)||hole.RadiusMm<=0||hole.RadiusMm>1e9||hole.SourceFaceCount<1||Len(Sub(hole.AxisEnd,hole.AxisStart))<.001)
                throw new ArgumentException("Opening ID, axis, or radius is invalid.");
        }
    }
    /// <summary>Exact Euclidean distance to a closed, finite cylinder, including its planar end caps; millimetres.</summary>
    public static double PointOpeningDistanceMm(Point3 point,HostOpening hole)
    {
        if(!Finite(point)||!Finite(hole.AxisStart)||!Finite(hole.AxisEnd)||!double.IsFinite(hole.RadiusMm)||hole.RadiusMm<=0)throw new ArgumentException("Opening distance parameters are invalid.");
        var axis=Sub(hole.AxisEnd,hole.AxisStart);double length=Len(axis);if(length<.001)throw new ArgumentException("Opening axis is too short.");
        var direction=Scale(axis,1/length);var relative=Sub(point,hole.AxisStart);double along=Dot(relative,direction);
        double radial=Len(Sub(relative,Scale(direction,along)));
        double axialOutside=Math.Max(0,Math.Max(-along,along-length)),radialOutside=Math.Max(0,radial-hole.RadiusMm);
        return Math.Sqrt(axialOutside*axialOutside+radialOutside*radialOutside);
    }
    /// <summary>Certified distance interval. Distance to a closed solid is 1-Lipschitz along arc length.</summary>
    public static (double Lower,double Upper) OpeningDistanceBoundsMm(Curve3 source,HostOpening hole)
    {
        var curve=new CurveEval(source);
        if(!curve.IsArc)return LineOpeningBounds(curve.Start,curve.End,hole);
        double At(double t)=>PointOpeningDistanceMm(curve.At(t),hole);
        (double Lower,double Upper) Bound(double lo,double hi)
        {
            var chord=LineOpeningBounds(curve.At(lo),curve.At(hi),hole);double sagitta=curve.Sagitta(hi-lo);
            return (Math.Max(0,chord.Lower-sagitta),Math.Min(chord.Upper+sagitta,Math.Min(At(lo),Math.Min(At((lo+hi)/2),At(hi)))));
        }
        var queue=new PriorityQueue<(double Lo,double Hi),double>();
        var first=Bound(0,1);double upper=first.Upper;
        queue.Enqueue((0,1),first.Lower);int work=0;
        while(queue.TryDequeue(out var interval,out double lower))
        {
            if(upper-lower<=DistanceErrorMm)return (lower,upper);
            if(lower>=upper)continue;
            if(++work>100000)throw new InvalidOperationException("Opening-to-curve distance did not converge within 0.001 mm.");
            double mid=(interval.Lo+interval.Hi)/2;
            foreach(var range in new[]{(interval.Lo,mid),(mid,interval.Hi)})
            {
                var child=Bound(range.Item1,range.Item2);upper=Math.Min(upper,child.Upper);
                if(child.Lower<=upper)queue.Enqueue(range,child.Lower);
            }
        }
        return (upper,upper);
    }
    static (double Lower,double Upper) LineOpeningBounds(Point3 start,Point3 end,HostOpening hole)
    {
        var delta=Sub(end,start);double length=Len(delta),lo=0,hi=1;
        if(length<1e-12){double value=PointOpeningDistanceMm(start,hole);return(value,value);}
        double At(double t)=>PointOpeningDistanceMm(Add(start,Scale(delta,t)),hole);
        // Distance to a closed convex finite cylinder is convex along a line, including across end caps.
        // Ternary brackets retain a true minimizer; the final Lipschitz bound makes the tolerance explicit.
        for(int i=0;i<100&&length*(hi-lo)>DistanceErrorMm/2;i++)
        {
            double a=(2*lo+hi)/3,b=(lo+2*hi)/3,fa=At(a),fb=At(b);
            if(fa<fb)hi=b;else if(fb<fa)lo=a;else{lo=a;hi=b;}
        }
        double upper=Math.Min(At(0),Math.Min(At(1),At((lo+hi)/2)));
        return(Math.Max(0,upper-length*(hi-lo)/2),upper);
    }
    static void CheckOpenings(ValidationReport report,BarPath3 bar,HostContext host)
    {
        foreach(var hole in host.HostOpenings)
        {
            double lower=double.PositiveInfinity,upper=double.PositiveInfinity;
            try
            {
                var holeBox=new Box3(Math.Min(hole.AxisStart.X,hole.AxisEnd.X)-hole.RadiusMm,Math.Max(hole.AxisStart.X,hole.AxisEnd.X)+hole.RadiusMm,
                    Math.Min(hole.AxisStart.Y,hole.AxisEnd.Y)-hole.RadiusMm,Math.Max(hole.AxisStart.Y,hole.AxisEnd.Y)+hole.RadiusMm,
                    Math.Min(hole.AxisStart.Z,hole.AxisEnd.Z)-hole.RadiusMm,Math.Max(hole.AxisStart.Z,hole.AxisEnd.Z)+hole.RadiusMm);
                foreach(var curve in bar.Curves)
                {
                    if(!new CurveEval(curve).Bounds().Expand(bar.DiameterMm/2).Overlaps(holeBox))continue;
                    var interval=OpeningDistanceBoundsMm(curve,hole);lower=Math.Min(lower,interval.Lower);upper=Math.Min(upper,interval.Upper);
                }
                double half=bar.DiameterMm/2;
                if(lower>=half-1e-6)continue;
                bool confirmed=upper<half-1e-6;
                report.AcceptedHostOpenings.Add(new(){DraftId=bar.DraftId,MemberIndex=bar.MemberIndex,Kind=ValidationKind.HostOpening,
                    ClearanceMm=lower-half,Message=confirmed?$"Pending · Intersects opening {hole.Id}; centerline-to-opening-solid distance {lower:0.###}–{upper:0.###} mm. Shape retained.":
                    $"Pending · Possible contact with opening {hole.Id} edge (distance accuracy {DistanceErrorMm} mm). Shape retained."});
            }
            catch(Exception ex)
            {report.IsComplete=false;report.Issues.Add(new(){DraftId=bar.DraftId,MemberIndex=bar.MemberIndex,Kind=ValidationKind.Incomplete,Message=$"Opening {hole.Id} check incomplete: {ex.Message}"});}
        }
    }
    static bool Inside(List<SketchPoint> polygon,double x,double z)
    {
        bool inside=false;
        for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
        {
            var a=polygon[i];var b=polygon[j];
            if(PointSegment(new(x,0,z),new(a.Rho,0,a.Z),new(b.Rho,0,b.Z))<1e-9)return true;
            if((a.Z>z)!=(b.Z>z) && x<(b.Rho-a.Rho)*(z-a.Z)/(b.Z-a.Z)+a.Rho)inside=!inside;
        }
        return inside;
    }

    sealed class ModelBar
    {
        public BarPath3 Path;public List<CurveEval> Curves;public SketchPoint? Ring;public List<CurveEval>? Profile;public long? ExistingId;public Box3 Box;
        public ModelBar(BarPath3 path,List<CurveEval> curves,SketchPoint? ring,List<CurveEval>? profile)
        {Path=path;Curves=curves;Ring=ring;Profile=profile;Box=Box3.Union(curves.Select(c=>c.Bounds())).Expand(path.DiameterMm/2);}
    }
    static SketchPoint? DetectRing(List<CurveEval> curves)
    {
        if(curves.Count<2 || curves.Any(c=>!c.IsArc))return null;
        var a=curves[0];
        if(Math.Abs(a.Center.X)>.001||Math.Abs(a.Center.Y)>.001||Math.Abs(curves.Sum(c=>Math.Abs(c.Sweep))-2*Math.PI)>1e-6)return null;
        for(int i=0;i<curves.Count;i++)
        {
            var b=curves[i];
            if(Len(Sub(a.Center,b.Center))>.001||Math.Abs(a.Radius-b.Radius)>.001||Math.Abs(b.AxisX.Z)>.000001||Math.Abs(b.AxisY.Z)>.000001||
                Len(Sub(b.At(1),curves[(i+1)%curves.Count].At(0)))>.001)return null;
        }
        return new(a.Radius,a.Center.Z);
    }
    static List<CurveEval>? DetectProfile(List<Curve3> curves)
    {
        if(curves.Count==0)return null;
        double angle=Math.Atan2(curves[0].Start.Y,curves[0].Start.X),ca=Math.Cos(angle),sa=Math.Sin(angle);
        Point3 Convert(Point3 p)
        {
            double rho=p.X*ca+p.Y*sa,off=-p.X*sa+p.Y*ca;
            if(rho<=0||Math.Abs(off)>.000001)throw new ArgumentException();
            return new(rho,0,p.Z);
        }
        try{return curves.Select(c=>new CurveEval(new Curve3{Kind=c.Kind,Start=Convert(c.Start),End=Convert(c.End),Middle=c.Kind==SegmentKind.Arc?Convert(c.Middle):new()})).ToList();}
        catch{return null;}
    }
    static double BarDistance(ModelBar a,ModelBar b,double threshold)
    {
        if(a.Ring!=null&&b.Ring!=null)return SketchDrawing.Distance(a.Ring,b.Ring);
        if(a.Ring!=null&&b.Profile!=null)return RingProfile(a.Ring,b.Profile);
        if(b.Ring!=null&&a.Profile!=null)return RingProfile(b.Ring,a.Profile);
        double distance=double.PositiveInfinity;
        foreach(var x in a.Curves)foreach(var y in b.Curves)
        {
            if(x.Bounds().Distance(y.Bounds())>threshold)continue;
            distance=Math.Min(distance,Distance(x,y));
        }
        return distance;
    }
    static double RingProfile(SketchPoint ring,List<CurveEval> profile)
    {
        var point=new CurveEval(new Curve3{Start=new(ring.Rho,0,ring.Z),End=new(ring.Rho,0,ring.Z)},true);
        return profile.Min(c=>Distance(point,c));
    }

    // Each arc subcurve lies within its chord sagitta. Segment-distance +/- both sagittae is a proven distance interval.
    // Branch-and-bound refines only potentially closest subcurves. Reported upper bounds differ from the exact minimum by <= .001 mm.
    readonly record struct Interval(CurveEval A,double A0,double A1,CurveEval B,double B0,double B1,double Lower,double Upper,double SagA,double SagB);
    static Interval IntervalOf(CurveEval a,double a0,double a1,CurveEval b,double b0,double b1)
    {
        double sa=a.Sagitta(a1-a0),sb=b.Sagitta(b1-b0);
        double chord=SegmentDistance(a.At(a0),a.At(a1),b.At(b0),b.At(b1));
        return new(a,a0,a1,b,b0,b1,Math.Max(0,chord-sa-sb),chord+sa+sb,sa,sb);
    }
    static double Distance(CurveEval a,CurveEval b,double tolerance=DistanceErrorMm)
    {
        if(a.IsPoint)return b.PointDistance(a.At(0));
        if(b.IsPoint)return a.PointDistance(b.At(0));
        if(!a.IsArc&&!b.IsArc)return SegmentDistance(a.At(0),a.At(1),b.At(0),b.At(1));
        var queue=new PriorityQueue<Interval,double>();
        var first=IntervalOf(a,0,1,b,0,1);double upper=first.Upper;
        queue.Enqueue(first,first.Lower);int steps=0;
        while(queue.TryDequeue(out var current,out double lower))
        {
            if(upper-lower<=tolerance)return upper;
            if(++steps>100000)throw new InvalidOperationException("Curve distance convergence limit exceeded");
            if(lower>=upper)continue;
            Interval left,right;
            if(current.SagA>=current.SagB)
            {
                double mid=(current.A0+current.A1)/2;
                left=IntervalOf(a,current.A0,mid,b,current.B0,current.B1);right=IntervalOf(a,mid,current.A1,b,current.B0,current.B1);
            }
            else
            {
                double mid=(current.B0+current.B1)/2;
                left=IntervalOf(a,current.A0,current.A1,b,current.B0,mid);right=IntervalOf(a,current.A0,current.A1,b,mid,current.B1);
            }
            upper=Math.Min(upper,Math.Min(left.Upper,right.Upper));
            if(left.Lower<=upper)queue.Enqueue(left,left.Lower);
            if(right.Lower<=upper)queue.Enqueue(right,right.Lower);
        }
        return upper;
    }

    sealed class CurveEval
    {
        public bool IsArc,IsPoint;public Point3 Start,End,Center=new(),AxisX=new(),AxisY=new();public double Radius,Sweep;
        public CurveEval(Curve3 c,bool allowPoint=false)
        {
            Start=c.Start;End=c.End;
            if(!Finite(Start)||!Finite(End)||!Finite(c.Middle))throw new ArgumentException("Path coordinates must be finite values.");
            if(c.Kind==SegmentKind.Line)
            {
                IsPoint=Len(Sub(Start,End))<1e-9;
                if(IsPoint&&!allowPoint)throw new ArgumentException("Line length is zero.");
                return;
            }
            if(c.Kind!=SegmentKind.Arc)throw new ArgumentException("Unsupported curve.");
            IsArc=true;var u=Sub(c.Middle,Start);var v=Sub(End,Start);var w=Cross(u,v);double ww=Dot(w,w);
            if(ww<1e-12)throw new ArgumentException("The three arc points are collinear or coincident.");
            Center=Add(Start,Scale(Add(Scale(Cross(v,w),Dot(u,u)),Scale(Cross(w,u),Dot(v,v))),1/(2*ww)));
            Radius=Len(Sub(Start,Center));
            if(!double.IsFinite(Radius)||Radius<=1e-9)throw new ArgumentException("Arc radius is invalid.");
            AxisX=Scale(Sub(Start,Center),1/Radius);AxisY=Cross(Scale(w,1/Math.Sqrt(ww)),AxisX);
            double mid=Positive(Math.Atan2(Dot(Sub(c.Middle,Center),AxisY),Dot(Sub(c.Middle,Center),AxisX)));
            double end=Positive(Math.Atan2(Dot(Sub(End,Center),AxisY),Dot(Sub(End,Center),AxisX)));
            Sweep=mid<=end+1e-12?end:end-2*Math.PI;
            if(Math.Abs(Sweep)<1e-9)throw new ArgumentException("Arc angle is zero.");
        }
        public Point3 At(double t)=>!IsArc?Add(Start,Scale(Sub(End,Start),t)):Add(Center,Scale(Add(Scale(AxisX,Math.Cos(Sweep*t)),Scale(AxisY,Math.Sin(Sweep*t))),Radius));
        public Point3 Tangent(double t)
        {
            if(!IsArc){var v=Sub(End,Start);return Scale(v,1/Len(v));}
            return Scale(Add(Scale(AxisX,-Math.Sin(Sweep*t)),Scale(AxisY,Math.Cos(Sweep*t))),Math.Sign(Sweep));
        }
        public double Sagitta(double fraction)=>!IsArc?0:Radius*(1-Math.Cos(Math.Abs(Sweep)*fraction/2));
        public bool InSweep(double angle)
        {double p=Positive(Sweep>=0?angle:-angle);return p<=Math.Abs(Sweep)+1e-10;}
        public double PointDistance(Point3 p)
        {
            if(!IsArc)return PointSegment(p,Start,End);
            var v=Sub(p,Center);double x=Dot(v,AxisX),y=Dot(v,AxisY);
            double angle=Math.Atan2(y,x);
            if(InSweep(angle))
            {
                var nearest=Add(Center,Scale(Add(Scale(AxisX,Math.Cos(angle)),Scale(AxisY,Math.Sin(angle))),Radius));
                return Len(Sub(p,nearest));
            }
            return Math.Min(Len(Sub(p,Start)),Len(Sub(p,End)));
        }
        public Box3 Bounds()
        {
            var points=new List<Point3>{Start,End};
            if(IsArc)
            {
                foreach(var angle in new[]{Math.Atan2(AxisY.X,AxisX.X),Math.Atan2(AxisY.Y,AxisX.Y),Math.Atan2(AxisY.Z,AxisX.Z)})
                foreach(var a in new[]{angle,angle+Math.PI})if(InSweep(a))points.Add(Add(Center,Scale(Add(Scale(AxisX,Math.Cos(a)),Scale(AxisY,Math.Sin(a))),Radius)));
            }
            return new(points.Min(p=>p.X),points.Max(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.Y),points.Min(p=>p.Z),points.Max(p=>p.Z));
        }
    }
    readonly record struct Box3(double MinX,double MaxX,double MinY,double MaxY,double MinZ,double MaxZ)
    {
        public Box3 Expand(double r)=>new(MinX-r,MaxX+r,MinY-r,MaxY+r,MinZ-r,MaxZ+r);
        public bool Overlaps(Box3 b)=>MinX<=b.MaxX+.000001&&MaxX+.000001>=b.MinX&&MinY<=b.MaxY+.000001&&MaxY+.000001>=b.MinY&&MinZ<=b.MaxZ+.000001&&MaxZ+.000001>=b.MinZ;
        public double Distance(Box3 b)
        {double x=Math.Max(0,Math.Max(MinX-b.MaxX,b.MinX-MaxX)),y=Math.Max(0,Math.Max(MinY-b.MaxY,b.MinY-MaxY)),z=Math.Max(0,Math.Max(MinZ-b.MaxZ,b.MinZ-MaxZ));return Math.Sqrt(x*x+y*y+z*z);}
        public static Box3 Union(IEnumerable<Box3> boxes)
        {var a=boxes.ToArray();return new(a.Min(b=>b.MinX),a.Max(b=>b.MaxX),a.Min(b=>b.MinY),a.Max(b=>b.MaxY),a.Min(b=>b.MinZ),a.Max(b=>b.MaxZ));}
    }
    static double Positive(double a){a%=2*Math.PI;return a<0?a+2*Math.PI:a;}
    static bool Finite(Point3 p)=>p!=null&&double.IsFinite(p.X)&&double.IsFinite(p.Y)&&double.IsFinite(p.Z)&&Math.Max(Math.Abs(p.X),Math.Max(Math.Abs(p.Y),Math.Abs(p.Z)))<=1e9;
    static Point3 Add(Point3 a,Point3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    static Point3 Sub(Point3 a,Point3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    static Point3 Scale(Point3 a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
    static double Dot(Point3 a,Point3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    static Point3 Cross(Point3 a,Point3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    static double Len(Point3 a)=>Math.Sqrt(Dot(a,a));
    static double PointSegment(Point3 p,Point3 a,Point3 b)
    {var d=Sub(b,a);double dd=Dot(d,d),t=dd<1e-18?0:Math.Clamp(Dot(Sub(p,a),d)/dd,0,1);return Len(Sub(p,Add(a,Scale(d,t))));}
    static double SegmentDistance(Point3 p1,Point3 q1,Point3 p2,Point3 q2)
    {
        var d1=Sub(q1,p1);var d2=Sub(q2,p2);var r=Sub(p1,p2);double a=Dot(d1,d1),e=Dot(d2,d2),f=Dot(d2,r),s,t;
        if(a<=1e-18&&e<=1e-18)return Len(r);
        if(a<=1e-18){s=0;t=Math.Clamp(f/e,0,1);}
        else
        {
            double c=Dot(d1,r);
            if(e<=1e-18){t=0;s=Math.Clamp(-c/a,0,1);}
            else
            {
                double b=Dot(d1,d2),denom=a*e-b*b;
                s=Math.Abs(denom)>1e-14*a*e?Math.Clamp((b*f-c*e)/denom,0,1):0;
                t=(b*s+f)/e;
                if(t<0){t=0;s=Math.Clamp(-c/a,0,1);}else if(t>1){t=1;s=Math.Clamp((b-c)/a,0,1);}
            }
        }
        return Len(Sub(Add(p1,Scale(d1,s)),Add(p2,Scale(d2,t))));
    }
}
