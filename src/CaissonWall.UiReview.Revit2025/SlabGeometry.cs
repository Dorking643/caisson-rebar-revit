namespace CaissonWallUi;

/// <summary>Deterministic mm geometry. No Revit objects, transactions, or inferred structural design.</summary>
public static class SlabGeometry
{
    const double Eps=1e-6,Contact=.01;
    public const double CollisionNumericalEpsilonMm=1e-6;
    const int MaxBars=5000,MaxPairs=2000000;
    sealed record Edge(SketchPoint A,SketchPoint B,double Cover,bool Top);
    sealed record Candidate(BarPath3 Path,ExistingBarPath Actual,bool Existing,Bounds Box);
    public static ValidationReport Check(SlabDocument doc)
    {
        var report=new ValidationReport();
        if(!Context(doc,report,out var edges))return report;
        var candidates=new List<Candidate>();var ids=new HashSet<string>();
        foreach(var group in doc.Groups.Where(g=>g.Enabled))
        {
            if(!ids.Add(group.Id)){Issue(report,group.Id,-1,ValidationKind.InvalidPath,"Duplicate rebar group ID.");continue;}
            try
            {
                Require(group.PositionConfirmed,"Group placement is unconfirmed. Confirm it before enabling.");
                var matches=doc.Types.Where(t=>t.Id==group.TypeId).ToArray();
                Require(matches.Length==1&&Finite(matches[0].DiameterMm)&&matches[0].DiameterMm>0,"Select one valid rebar type.");
                var type=matches[0];var bars=BuildGroup(doc,group,type);
                Require(report.Bars.Count+bars.Count<=MaxBars,$"Limit: {MaxBars} bars at a time.");
                foreach(var bar in bars)
                {
                    report.Bars.Add(bar);
                    AddChecked(doc,report,edges,candidates,bar,group.AllowTopExtension,false,0);
                }
            }
            catch(Exception e){Issue(report,group.Id,-1,ValidationKind.InvalidPath,e.Message);}
        }
        AddExisting(report,candidates,doc.ExistingBars);Collisions(report,candidates,doc.CheckSettings.CollisionToleranceMm,doc.CheckSettings.AllowCollisionViolation);return report;
    }

    /// <summary>Checks the actual supplied 3D curves directly, including offset vertical planes and both signs of X/Y.</summary>
    public static ValidationReport CheckActual(SlabDocument doc,List<ExistingBarPath> own,List<ExistingBarPath> others)
    {
        var report=new ValidationReport();if(!Context(doc,report,out var edges))return report;
        var candidates=new List<Candidate>();
        if(own.Count>MaxBars){report.IsComplete=false;Issue(report,"",-1,ValidationKind.Incomplete,"Actual rebar count exceeds the check limit.");return report;}
        var identities=new HashSet<string>();
        foreach(var bar in own)
        {
            string id=$"element/{bar.ElementId}/{bar.PositionIndex}";
            if(!identities.Add(id)){Issue(report,id,-1,ValidationKind.InvalidPath,"Duplicate actual rebar identity.");continue;}
            var path=new BarPath3{DraftId=id,MemberIndex=bar.PositionIndex,DiameterMm=bar.DiameterMm,Curves=bar.Curves};
            report.Bars.Add(path);AddChecked(doc,report,edges,candidates,path,bar.AllowTopExtension,false,bar.ElementId);
        }
        AddExisting(report,candidates,others);Collisions(report,candidates,doc.CheckSettings.CollisionToleranceMm,doc.CheckSettings.AllowCollisionViolation);return report;
    }

    public static bool TryNormalizeShape(IReadOnlyList<SketchSegment> source,int stretchSegmentIndex,
        out List<SketchSegment> normalized,out double referenceSpan,out string error)
    {
        normalized=new();referenceSpan=0;error="";
        try
        {
            ArgumentNullException.ThrowIfNull(source);Require(stretchSegmentIndex>=0&&stretchSegmentIndex<source.Count,"Select a horizontal line as the variable-length segment.");
            var middle=source[stretchSegmentIndex];
            Require(middle.Kind==SegmentKind.Line&&Math.Abs(middle.Start.Z-middle.End.Z)<Eps,"The variable-length segment must be a horizontal line.");
            referenceSpan=Math.Abs(middle.End.Rho-middle.Start.Rho);Require(referenceSpan>Eps,"The variable-length segment must have positive length.");
            double center=(middle.Start.Rho+middle.End.Rho)/2,z=middle.Start.Z;
            normalized=ShapePlacement.CopySegments(source,-center,-z);return true;
        }
        catch(Exception e){normalized=new();referenceSpan=0;error=e.Message;return false;}
    }
    /// <summary>Rebase an inserted shape to its variable segment without moving its current world geometry.
    /// Call explicitly when switching a fixed, insertion-point-based shape to boundary fitting; persisted groups are not migrated.</summary>
    public static bool TryNormalizeGroupShape(SlabGroup group,int stretchSegmentIndex,out SketchPoint shift,out string error)
    {
        shift=new();error="";
        if(group.Kind!=SlabGroupKind.Orthogonal){error="Only orthogonal rebar uses a horizontal variable-length segment.";return false;}
        if(!TryNormalizeShape(group.ShapeSnapshot,stretchSegmentIndex,out var normalized,out var span,out error))return false;
        var middle=group.ShapeSnapshot[stretchSegmentIndex];
        double u=middle.Start.Rho/2+middle.End.Rho/2,v=middle.Start.Z;
        double along=group.AlongOffset+u,z=group.Z+v;
        if(!new[]{u,v,along,z}.All(Finite)){error="Shape coordinates must be finite values in mm.";return false;}
        group.ShapeSnapshot=normalized;group.StretchSegmentIndex=stretchSegmentIndex;group.ReferenceSpan=span;
        group.AlongOffset=along;group.Z=z;shift=new(u,v);return true;
    }
    /// <summary>Choose the longest horizontal segment at the lowest elevation. Ambiguous ties require an explicit selection.</summary>
    public static int DefaultStretchSegmentIndex(IReadOnlyList<SketchSegment> source)
    {
        var horizontal=source.Select((segment,index)=>(segment,index))
            .Where(x=>x.segment.Kind==SegmentKind.Line&&new[]{x.segment.Start.Rho,x.segment.Start.Z,x.segment.End.Rho,x.segment.End.Z}.All(Finite)
                &&Math.Abs(x.segment.Start.Z-x.segment.End.Z)<Eps&&Math.Abs(x.segment.End.Rho-x.segment.Start.Rho)>Eps).ToArray();
        if(horizontal.Length==0)return -1;
        double bottom=horizontal.Min(x=>x.segment.Start.Z);
        var lowest=horizontal.Where(x=>Math.Abs(x.segment.Start.Z-bottom)<Eps).ToArray();
        double longest=lowest.Max(x=>Math.Abs(x.segment.End.Rho-x.segment.Start.Rho));
        var matches=lowest.Where(x=>Math.Abs(Math.Abs(x.segment.End.Rho-x.segment.Start.Rho)-longest)<Eps).ToArray();
        return matches.Length==1?matches[0].index:-1;
    }
    /// <summary>Shared range calculation for preview and UI. The last bar stays on the spacing grid and never extends the range.</summary>
    public static int RangeCount(double start,double end,double spacing)
    {
        Require(new[]{start,end,spacing}.All(Finite),"Start, end, and spacing must be finite values in mm.");
        Require(spacing>0&&end>=start,"Array end must not precede start; spacing must be positive.");
        double q=StableInteger((end-start)/spacing);
        Require(Finite(q)&&q<MaxBars,"Array count exceeds the limit.");return checked((int)Math.Floor(q)+1);
    }
    /// <summary>Reads a legacy count group without changing it; its visible end is the actual last bar, not its unused EndOffset.</summary>
    public static bool TryGetOrthogonalRange(SlabGroup group,out int count,out double last,out string error)
    {
        count=0;last=0;error="";
        try
        {
            Require(group.Kind==SlabGroupKind.Orthogonal,"This rebar group does not use a linear array.");
            Require(Finite(group.StartOffset)&&Finite(group.Spacing),"Start and spacing must be finite values in mm.");
            int n=group.UseEndOffset?RangeCount(group.StartOffset,group.EndOffset,group.Spacing):group.Count;
            Require(n>=1&&n<=MaxBars,$"Bar count must be 1–{MaxBars}.");Require(n==1||group.Spacing>0,"Spacing must be positive for multiple bars.");
            double end=group.StartOffset+(n-1)*group.Spacing;Require(Finite(end),"Array end must be a finite value in mm.");
            count=n;last=group.UseEndOffset?Math.Min(group.EndOffset,end):end;return true;
        }
        catch(Exception e){error=e.Message;return false;}
    }
    /// <summary>Shared full-circle count for radial preview and UI. The 360-degree endpoint is never duplicated.</summary>
    public static bool TryGetRadialCount(SlabGroup group,out int count,out string error)
    {
        count=0;error="";
        try
        {
            Require(group.Kind==SlabGroupKind.Radial,"This rebar group does not use a circular array.");
            int n=group.Count;
            if(group.UseAngleStep)
            {
                Require(Finite(group.AngleStep)&&group.AngleStep>0&&group.AngleStep<=360,"Angle step must be greater than 0 and no greater than 360°.");
                double quotient=StableInteger(360/group.AngleStep);Require(Finite(quotient)&&quotient<=MaxBars,"Angle step produces too many bars.");n=(int)Math.Ceiling(quotient);
            }
            Require(n>=1&&n<=MaxBars,$"Bar count must be 1–{MaxBars}.");count=n;return true;
        }
        catch(Exception e){error=e.Message;return false;}
    }
    /// <summary>Explicit UI mode change, initially one fixed-shape bar. Its reference point never moves.
    /// Orthogonal-to-radial changes the horizontal heading to point radially outward; the reverse keeps the first world path.
    /// A fitted orthogonal group's first actual shape is frozen before changing mode. Other persisted fields remain intact.</summary>
    public static bool TryChangeDistributionKind(SlabDocument doc,SlabGroup group,SlabGroupKind target,out string error)
    {
        error="";
        try
        {
            ArgumentNullException.ThrowIfNull(doc);ArgumentNullException.ThrowIfNull(group);
            Require(target is SlabGroupKind.Orthogonal or SlabGroupKind.Radial,"Rings do not use profile placement modes.");
            Require(group.Kind is SlabGroupKind.Orthogonal or SlabGroupKind.Radial,"Only profile shapes can switch placement modes.");
            if(group.Kind==target)return true;
            Require(new[]{group.Z,group.DirectionDeg,group.AlongOffset,group.StartOffset,group.InnerRadius,group.StartAngle}.All(Finite),"Shape base point must use finite values in mm.");
            var shape=ShapePlacement.CopySegments(group.ShapeSnapshot);int stretch=group.StretchSegmentIndex;double span=group.ReferenceSpan;
            if(group.Kind==SlabGroupKind.Orthogonal&&group.FitToBoundary)
            {
                // Use the existing boundary fitter exactly once for the first member; do not reimplement its chord/end allowances.
                var first=System.Text.Json.JsonSerializer.Deserialize<SlabGroup>(System.Text.Json.JsonSerializer.Serialize(group,SlabDocument.JsonOptions),SlabDocument.JsonOptions)!;
                first.UseEndOffset=false;first.Count=1;
                var types=doc.Types.Where(t=>t.Id==group.TypeId).ToArray();Require(types.Length==1,"Select one rebar type.");
                var curves=BuildGroup(doc,first,types[0])[0].Curves;
                double angle=group.DirectionDeg*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle);
                SketchPoint Local(Point3 p)=>new(p.X*c+p.Y*s-group.AlongOffset,p.Z-group.Z);
                shape=curves.Select(curve=>new SketchSegment{Kind=curve.Kind,Start=Local(curve.Start),Middle=curve.Kind==SegmentKind.Arc?Local(curve.Middle):new(),End=Local(curve.End)}).ToList();
                if(group.ShapeSnapshot.Count==0)stretch=0;
                if(stretch>=0&&stretch<shape.Count)span=Math.Abs(shape[stretch].End.Rho-shape[stretch].Start.Rho);
            }
            else if(shape.Count==0)
            {
                if(group.Kind==SlabGroupKind.Orthogonal){Require(Finite(span)&&span>Eps,"Enter a reference span for fixed-span straight rebar.");shape=new(){Line2(new(-span/2,0),new(span/2,0))};}
                else{span=group.OuterRadius-group.InnerRadius;Require(Finite(span)&&span>Eps,"Radial end must exceed radial start.");shape=new(){Line2(new(0,0),new(span,0))};}
                stretch=0;
            }
            Require(shape.Count>0,"Shape path is empty.");
            foreach(var segment in shape)
                Require(new[]{segment.Start.Rho,segment.Start.Z,segment.End.Rho,segment.End.Z}.All(Finite)&&
                    (segment.Kind!=SegmentKind.Arc||new[]{segment.Middle.Rho,segment.Middle.Z}.All(Finite)),"Shape path coordinates must be finite values in mm.");
            double radius=group.InnerRadius,startAngle=group.StartAngle;
            if(target==SlabGroupKind.Radial)
            {
                double angle=group.DirectionDeg*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle);
                double x=group.AlongOffset*c-group.StartOffset*s,y=group.AlongOffset*s+group.StartOffset*c;
                radius=Math.Sqrt(x*x+y*y);startAngle=radius>0?Math.Atan2(y,x)*180/Math.PI:group.DirectionDeg;
                Require(Finite(radius)&&Finite(startAngle),"Shape base point exceeds the valid numeric range.");
            }
            else Require(radius>=0,"Radial base radius cannot be negative.");
            // Commit only after every prerequisite succeeds. Geometric cover/axis conflicts remain visible to the normal checker;
            // moving the reference point or shortening fixed ends here would silently change the user's design.
            group.ShapeSnapshot=shape;group.StretchSegmentIndex=stretch;group.ReferenceSpan=span;
            group.Kind=target;group.Count=1;group.UseEndOffset=false;group.UseAngleStep=false;group.FitToBoundary=false;
            if(target==SlabGroupKind.Radial){group.InnerRadius=radius;group.StartAngle=startAngle;}
            else{group.DirectionDeg=startAngle;group.AlongOffset=radius;group.StartOffset=0;group.EndOffset=0;}
            return true;
        }
        catch(Exception e){error=e.Message;return false;}
    }
    public static List<Point3> Sample(Curve3 curve,double maxStepMm=20)
    {
        Require(Finite(maxStepMm)&&maxStepMm>0,"Sampling step must be positive.");var e=new Eval(curve);
        int n=(int)Math.Ceiling(e.Length/maxStepMm);Require(n<=1000000,"Curve sampling count exceeds the limit.");n=Math.Max(1,n);
        return Enumerable.Range(0,n+1).Select(i=>e.At((double)i/n)).ToList();
    }
    public static double CurveLength(Curve3 curve)=>new Eval(curve).Length;
    /// <summary>Net clearance certificate, mm. Lines and complete horizontal circles use analytic distances;
    /// other curves keep the existing conservative lower bound without adding its .001 mm error back.</summary>
    public static double CertifiedClearanceMm(ExistingBarPath first,ExistingBarPath second)
    {
        Require(Finite(first.DiameterMm)&&first.DiameterMm>0&&Finite(second.DiameterMm)&&second.DiameterMm>0,"Bar diameter is invalid.");
        Require(first.Curves.Count>0&&second.Curves.Count>0,"Rebar path is empty.");
        double radii=(first.DiameterMm+second.DiameterMm)/2;
        if(first.Curves.All(c=>c.Kind==SegmentKind.Line)&&second.Curves.All(c=>c.Kind==SegmentKind.Line))
            return first.Curves.Min(a=>second.Curves.Min(b=>WallValidation.CurveDistanceMm(a,b)))-radii;
        if(TryHorizontalRing(first.Curves,out var c1,out var r1,out var error1)&&TryHorizontalRing(second.Curves,out var c2,out var r2,out var error2))
        {
            double offset=Math.Sqrt(Math.Pow(c1.X-c2.X,2)+Math.Pow(c1.Y-c2.Y,2));
            double horizontal=Math.Max(0,Math.Max(Math.Abs(r1-r2)-offset,offset-r1-r2));
            return Math.Sqrt(horizontal*horizontal+Math.Pow(c1.Z-c2.Z,2))-radii-error1-error2;
        }
        double certified=WallValidation.CertifiedSeparationMm(first,second);
        if(certified>=0)return certified;
        // Exact coordinate extrema give an independent lower bound. This can certify endpoint/tangent
        // contacts without incorrectly adding the distance solver's error estimate back to its result.
        var aBoxes=first.Curves.Select(c=>new Eval(c).Box).ToArray();var bBoxes=second.Curves.Select(c=>new Eval(c).Box).ToArray();
        double boxLower=aBoxes.Min(a=>bBoxes.Min(b=>a.Distance(b)))-radii;
        return Math.Max(certified,boxLower);
    }
    public static bool AcceptsCollision(double certifiedClearanceMm,double toleranceMm)
    {
        Require(Finite(toleranceMm)&&toleranceMm>=0&&toleranceMm<=100,"Collision tolerance must be 0–100 mm.");
        return !double.IsNaN(certifiedClearanceMm)&&certifiedClearanceMm>=-(toleranceMm>0?toleranceMm+CollisionNumericalEpsilonMm:Contact);
    }
    static bool TryHorizontalRing(List<Curve3> source,out Point3 center,out double radius,out double error)
    {
        center=new();radius=0;error=0;if(source.Count<2||source.Any(c=>c.Kind!=SegmentKind.Arc))return false;
        var curves=source.Select(c=>new Eval(c)).ToArray();center=curves[0].Center;radius=curves[0].Radius;
        if(Math.Abs(curves.Sum(c=>c.Sweep)-2*Math.PI)>1e-9)return false;
        for(int i=0;i<curves.Length;i++)
        {
            var curve=curves[i];double deviation=Length(Sub(curve.Center,center))+Math.Abs(curve.Radius-radius)+curve.Radius*(Math.Abs(curve.Ex.Z)+Math.Abs(curve.Ey.Z));
            if(deviation>1e-7||Length(Sub(curve.At(1),curves[(i+1)%curves.Length].At(0)))>1e-7)return false;
            error=Math.Max(error,deviation);
        }
        return true;
    }
    /// <summary>Preview only: disabled groups remain editable; this is never a build/validation authorization.</summary>
    public static List<BarPath3> PreviewGroup(SlabDocument doc,SlabGroup group)
    {
        Require(group.PositionConfirmed,"Group placement is unverified. Assumed positions are not displayed.");
        var types=doc.Types.Where(t=>t.Id==group.TypeId).ToArray();Require(types.Length==1,"Select one rebar type.");
        return BuildGroup(doc,group,types[0]);
    }

    static List<BarPath3> BuildGroup(SlabDocument doc,SlabGroup g,ReviewBarType type)
    {
        Require(new[]{g.Z,g.DirectionDeg,g.StartOffset,g.EndOffset,g.Spacing,g.Radius,g.RadiusStep,g.InnerRadius,g.OuterRadius,
            g.StartAngle,g.AngleStep,g.ReferenceSpan,g.AlongOffset,g.GroupBoundaryRadiusMm}.All(Finite),"Parameters must be finite values in mm.");
        Require(g.GroupBoundaryRadiusMm>=0,"Boundary radius cannot be negative.");
        var bars=new List<BarPath3>();
        void Add(List<Curve3> curves)=>bars.Add(new(){DraftId=g.Id,MemberIndex=bars.Count,TypeId=type.Id,DiameterMm=type.DiameterMm,Curves=curves});
        int count=g.Count;
        if(g.Kind==SlabGroupKind.Orthogonal&&g.UseEndOffset)
        {
            count=RangeCount(g.StartOffset,g.EndOffset,g.Spacing);
        }
        else if(g.Kind==SlabGroupKind.Radial)
        {
            Require(TryGetRadialCount(g,out count,out var countError),countError);
        }
        Require(count>=1&&count<=MaxBars,$"Bar count must be 1–{MaxBars}.");
        if(g.Kind==SlabGroupKind.Ring)
        {
            Require(count==1||g.RadiusStep!=0,"Radius step cannot be zero for multiple rings.");
            for(int j=0;j<count;j++)
            {
                double r=g.Radius+j*g.RadiusStep;Require(r>type.DiameterMm/2,"Ring radius must exceed the bar radius.");
                Add(new(){Arc(new(r,0,g.Z),new(0,r,g.Z),new(-r,0,g.Z)),Arc(new(-r,0,g.Z),new(0,-r,g.Z),new(r,0,g.Z))});
            }
        }
        else if(g.Kind==SlabGroupKind.Radial)
        {
            Require(g.InnerRadius>=0,"Radial start cannot be negative.");
            var source=g.ShapeSnapshot.Count>0?ShapePlacement.CopySegments(g.ShapeSnapshot):new(){Line2(new(0,0),new(g.OuterRadius-g.InnerRadius,0))};
            if(g.ShapeSnapshot.Count==0)Require(g.OuterRadius>g.InnerRadius,"Radial end must exceed radial start.");
            foreach(var e in source.Select(s=>new Eval(To3(s,p=>new(p.Rho+g.InnerRadius,0,p.Z)))))Require(e.Box.MinX>=-Eps,"Radial shape cannot cross the center axis.");
            for(int j=0;j<count;j++)
            {
                double delta=g.UseAngleStep?j*g.AngleStep:360d*j/count;Require(delta<360,"Circular array must not duplicate the 360° endpoint.");
                double a=(g.StartAngle+delta)*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a);
                Add(source.Select(segment=>To3(segment,p=>new((g.InnerRadius+p.Rho)*c,(g.InnerRadius+p.Rho)*s,g.Z+p.Z))).ToList());
            }
        }
        else if(g.Kind==SlabGroupKind.Orthogonal)
        {
            Require(count==1||g.Spacing>0,"Spacing must be positive for multiple bars.");
            var shape=ShapePlacement.CopySegments(g.ShapeSnapshot);double reference=g.ReferenceSpan;
            if(g.FitToBoundary&&shape.Count>0)
            {
                Require(TryNormalizeShape(shape,g.StretchSegmentIndex,out var normalized,out var measured,out var error),error);
                Require(reference>0&&Math.Abs(measured-reference)<.001,"Reference span does not match the selected horizontal segment.");shape=normalized;
            }
            double a=doc.Host.OuterRadiusMm-doc.CheckSettings.SideCoverMm-type.DiameterMm/2;
            if(g.GroupBoundaryRadiusMm>0)a=Math.Min(a,g.GroupBoundaryRadiusMm);
            Require(a>0,"Clear cover plus bar radius exceeds the slab radius.");
            double direction=g.DirectionDeg*Math.PI/180,c=Math.Cos(direction),s=Math.Sin(direction);
            double left=0,right=0;
            if(g.FitToBoundary&&shape.Count>0)
            {
                var box=Bounds.Union(shape.Select(seg=>new Eval(To3(seg,p=>new(p.Rho,0,p.Z))).Box));
                left=Math.Max(0,-reference/2-box.MinX);right=Math.Max(0,box.MaxX-reference/2);
            }
            for(int j=0;j<count;j++)
            {
                double eta=g.StartOffset+j*g.Spacing;
                // StableInteger may restore an exact endpoint lost by a few floating-point ULPs;
                // clamp only that arithmetic overshoot so no bar is placed beyond the requested end.
                if(g.UseEndOffset)eta=Math.Min(eta,g.EndOffset);
                List<SketchSegment> actual;
                if(g.FitToBoundary)
                {
                    Require(Math.Abs(eta)<a-Eps,"Array position is outside the effective circle or produces a zero-length tangent.");
                    double b=Math.Sqrt((a-eta)*(a+eta));
                    double span=2*Math.Min(b+g.AlongOffset-left,b-g.AlongOffset-right);
                    Require(span>Eps,"This array position has insufficient length for the fixed end shapes.");
                    actual=shape.Count==0?new(){Line2(new(-span/2,0),new(span/2,0))}:Fit(shape,g.StretchSegmentIndex,reference,span);
                }
                else
                {
                    Require(shape.Count>0||reference>Eps,"Enter a reference span for fixed-span straight rebar.");
                    actual=shape.Count>0?ShapePlacement.CopySegments(shape):new(){Line2(new(-reference/2,0),new(reference/2,0))};
                }
                Add(actual.Select(seg=>To3(seg,p=>new((g.AlongOffset+p.Rho)*c-eta*s,(g.AlongOffset+p.Rho)*s+eta*c,g.Z+p.Z))).ToList());
            }
        }
        else throw new ArgumentException("Unsupported slab rebar array type.");
        return bars;
    }
    static List<SketchSegment> Fit(List<SketchSegment> source,int middle,double reference,double span)
    {
        double sign=Math.Sign(source[middle].End.Rho-source[middle].Start.Rho),delta=(span-reference)/2;
        var output=new List<SketchSegment>();
        for(int i=0;i<source.Count;i++)
        {
            var s=source[i];double start=(i<=middle?-sign:sign)*delta,end=(i<middle?-sign:sign)*delta;
            output.Add(new(){Kind=s.Kind,Start=new(s.Start.Rho+start,s.Start.Z),End=new(s.End.Rho+end,s.End.Z),
                Middle=new(s.Middle.Rho+(i<middle?-sign:sign)*delta,s.Middle.Z)});
        }
        return output;
    }
    static double StableInteger(double q){double n=Math.Round(q),ulp=Math.BitIncrement(q)-q;return Math.Abs(q-n)<=4*ulp?n:q;}

    static bool Context(SlabDocument doc,ValidationReport report,out List<Edge> edges)
    {
        edges=new();
        try
        {
            ArgumentNullException.ThrowIfNull(doc);var h=doc.Host;var c=doc.CheckSettings;
            Require(h.IsVerified&&h.ExistingScanComplete,"Host or existing rebar has not been verified.");Require(c.CoverConfirmed,"Confirm the clear cover.");
            Require(new[]{h.OuterRadiusMm,h.HeightMm,c.TopCoverMm,c.BottomCoverMm,c.SideCoverMm}.All(Finite)&&h.OuterRadiusMm>0&&h.HeightMm>0&&h.InnerRadiusMm==0,"Slab host must be a valid solid of revolution without a central opening.");
            Require(c.TopCoverMm>=0&&c.BottomCoverMm>=0&&c.SideCoverMm>=0,"Clear cover cannot be negative.");
            Require(Finite(c.CollisionToleranceMm)&&c.CollisionToleranceMm>=0&&c.CollisionToleranceMm<=100,"Collision tolerance must be 0–100 mm.");
            var p=h.SectionBoundary;Require(p.Count>=3&&p.All(v=>Finite(v.Rho)&&Finite(v.Z)&&v.Rho>=-Eps),"Actual slab section is missing.");
            double area=0;for(int i=0;i<p.Count;i++)area+=p[i].Rho*p[(i+1)%p.Count].Z-p[(i+1)%p.Count].Rho*p[i].Z;
            Require(Math.Abs(area)>Eps,"Slab section area is invalid.");
            for(int i=0;i<p.Count;i++)
            {
                var a=p[i];var b=p[(i+1)%p.Count];if(Math.Abs(a.Rho)<Eps&&Math.Abs(b.Rho)<Eps)continue;
                Require(SketchDrawing.Distance(a,b)>Eps,"Slab section contains a zero-length edge.");double normalZ=-Math.Sign(area)*(b.Rho-a.Rho);
                bool top=normalZ>Eps;double cover=top?c.TopCoverMm:normalZ< -Eps?c.BottomCoverMm:c.SideCoverMm;
                edges.Add(new(a,b,cover,top));
            }
            Require(edges.Count>=3,"Slab physical boundary is incomplete.");return true;
        }
        catch(Exception e){Issue(report,"",-1,ValidationKind.Context,e.Message);return false;}
    }
    static void AddChecked(SlabDocument doc,ValidationReport report,List<Edge> edges,List<Candidate> all,
        BarPath3 path,bool allowTop,bool existing,long elementId)
    {
        try
        {
            Require(Finite(path.DiameterMm)&&path.DiameterMm>0,"Bar diameter is invalid.");var curves=path.Curves.Select(c=>new Eval(c)).ToList();ValidatePath(curves,path.DiameterMm,doc.CheckSettings.CollisionToleranceMm,doc.CheckSettings.AllowCollisionViolation,report,path);
            if(allowTop&&!doc.CheckSettings.AllowConcreteBoundaryViolation)Require(curves.SelectMany(c=>new[]{c.At(0),c.At(.5),c.At(1)}).Any(p=>Inside(doc.Host.SectionBoundary,Radius(p),p.Z)&&p.Z<TopAt(doc.Host.SectionBoundary,Radius(p))-Eps),"Extended rebar must include a portion embedded in the slab.");
            var actual=new ExistingBarPath{ElementId=elementId,PositionIndex=path.MemberIndex,DiameterMm=path.DiameterMm,Curves=path.Curves,AllowTopExtension=allowTop};
            all.Add(new(path,actual,existing,Bounds.Union(curves.Select(c=>c.Box)).Expand(path.DiameterMm/2)));
            // Keep shape validation and collision candidates above. The user disabled only
            // cover/physical-boundary diagnostics; nominal covers still define fitted lengths.
            if(doc.CheckSettings.AllowConcreteBoundaryViolation)return;
            string? acceptedCover=null;bool coverComplete=true;
            foreach(var curve in curves)
            {
                // This override never authorizes steel outside the concrete; certify every curve,
                // including curves after the first nominal-cover failure, before recording acceptance.
                if(doc.CheckSettings.AllowCoverViolation&&!Cover(doc,edges,curve,path.DiameterMm,false,out var solidError,out bool solidIncomplete,true))
                {
                    Issue(report,path.DraftId,path.MemberIndex,solidIncomplete?ValidationKind.Incomplete:ValidationKind.Cover,"Solid boundary at zero clear cover: "+solidError);
                    if(solidIncomplete)report.IsComplete=false;coverComplete=false;break;
                }
                if(!Cover(doc,edges,curve,path.DiameterMm,allowTop,out var error,out bool incomplete))
                {
                    if(doc.CheckSettings.AllowCoverViolation&&!incomplete){acceptedCover??=error;continue;}
                    Issue(report,path.DraftId,path.MemberIndex,incomplete?ValidationKind.Incomplete:ValidationKind.Cover,error);
                    if(incomplete)report.IsComplete=false;coverComplete=false;break;
                }
            }
            if(coverComplete&&acceptedCover!=null)report.AcceptedCoverViolations.Add(new(){DraftId=path.DraftId,MemberIndex=path.MemberIndex,Kind=ValidationKind.Cover,
                Message="Cover shortfall accepted · "+acceptedCover+" Nominal cover retained; the complete bar passed the solid-boundary check at zero clear cover."});
        }
        catch(Exception e){Issue(report,path.DraftId,path.MemberIndex,ValidationKind.InvalidPath,e.Message);}
    }
    static void ValidatePath(List<Eval> path,double diameter,double tolerance,bool allowCollision,ValidationReport report,BarPath3 identity)
    {
        Require(path.Count>0,"Rebar shape is empty.");
        for(int i=0;i<path.Count;i++)
        {
            Require(!path[i].Arc||path[i].Radius>diameter/2,"Bend centerline radius must exceed the bar radius.");
            if(i>0){Require(Length(Sub(path[i-1].At(1),path[i].At(0)))<=.001,"Shape path is disconnected.");Require(Dot(path[i-1].Tangent(1),path[i].Tangent(0))>.99999,"Corner is not tangent. Add an arc.");}
        }
        bool closed=Length(Sub(path[0].At(0),path[^1].At(1)))<=.001;
        if(closed)Require(Dot(path[^1].Tangent(1),path[0].Tangent(0))>.99999,"Closed path is not tangent.");
        for(int i=0;i<path.Count;i++)for(int j=i+2;j<path.Count;j++)
        {
            if(closed&&i==0&&j==path.Count-1)continue;
            if(j==i+2&&!path[i].Arc&&!path[j].Arc&&path[i+1].Arc&&path[i+1].Sweep<=Math.PI/2+Eps)continue;
            if(path[i].Box.Expand(diameter/2).Overlap(path[j].Box.Expand(diameter/2)))
            {
                if(tolerance==0&&!allowCollision){Require(WallValidation.CurveDistanceMm(path[i].Curve,path[j].Curve)>=diameter-Contact,"Nonadjacent shape segments collide.");continue;}
                try
                {
                    double gap=CertifiedClearanceMm(new(){DiameterMm=diameter,Curves=new(){path[i].Curve}},new(){DiameterMm=diameter,Curves=new(){path[j].Curve}});
                    // PositiveInfinity is the existing certified broad-phase separation sentinel.
                    Require(!double.IsNaN(gap)&&!double.IsNegativeInfinity(gap),"No valid self-collision distance was obtained.");
                    if(gap< -CollisionNumericalEpsilonMm)
                    {
                        var issue=new ValidationIssue{DraftId=identity.DraftId,MemberIndex=identity.MemberIndex,OtherDraftId=identity.DraftId,OtherMemberIndex=identity.MemberIndex,
                            Kind=ValidationKind.Collision,ClearanceMm=gap,Message=$"Segments {i+1}/{j+1} of the same bar: penetration upper bound {-gap:0.######} mm."};
                        if(allowCollision||AcceptsCollision(gap,tolerance)){issue.Message="Accepted · "+issue.Message;report.AcceptedCollisions.Add(issue);}
                        else report.Issues.Add(issue);
                    }
                }
                catch(Exception e) when(allowCollision)
                {report.AcceptedCollisions.Add(new(){DraftId=identity.DraftId,MemberIndex=identity.MemberIndex,OtherDraftId=identity.DraftId,OtherMemberIndex=identity.MemberIndex,
                    Kind=ValidationKind.Incomplete,Message=$"Accepted collision warning · Collision between segments {i+1}/{j+1} of the same bar not quantified: {e.Message}"});}
            }
        }
    }

    static bool Cover(SlabDocument doc,List<Edge> edges,Eval curve,double diameter,bool allowTop,out string error,out bool incomplete,bool zeroCover=false)
    {
        error="";incomplete=false;var polygon=doc.Host.SectionBoundary;double contact=zeroCover?CollisionNumericalEpsilonMm:Contact;
        foreach(var t in new[]{0d,.5,1d})
        {
            var p=curve.At(t);double r=Radius(p);
            if(!Inside(polygon,r,p.Z)&&!(allowTop&&r<=doc.Host.OuterRadiusMm+Eps&&p.Z>=TopAt(polygon,r)-Eps))
            {error="Rebar extends beyond the slab concrete boundary.";return false;}
        }
        int samples=0;bool exhausted=false;
        foreach(var edge in edges)
        {
            if(allowTop&&edge.Top)continue;double required=(zeroCover?0:edge.Cover)+diameter/2;
            if(curve.Arc&&Math.Abs(curve.Center.X)<Eps&&Math.Abs(curve.Center.Y)<Eps&&Math.Abs(curve.Ex.Z)<Eps&&Math.Abs(curve.Ey.Z)<Eps)
            {
                if(Distance2(curve.Radius,curve.Center.Z,edge.A,edge.B)>=required-contact)continue;
                error="Ring clear cover is insufficient.";return false;
            }
            // Infinite-plane distance is a certified lower bound on distance to a finite planar annulus.
            if(Math.Abs(edge.A.Z-edge.B.Z)<Eps)
            {
                double z=edge.A.Z,lower=curve.Box.MinZ>z?curve.Box.MinZ-z:curve.Box.MaxZ<z?z-curve.Box.MaxZ:0;
                if(lower>=required-contact)continue;
            }
            if(Math.Abs(edge.A.Rho-edge.B.Rho)<Eps&&curve.TryRadialRange(out double minR,out double maxR))
            {
                double r=edge.A.Rho,lower=minR>r?minR-r:maxR<r?r-maxR:0;
                if(lower>=required-contact)continue;
            }
            double Dist(double t){samples++;var p=curve.At(t);return Distance2(Radius(p),p.Z,edge.A,edge.B)-required;}
            bool Certify(double lo,double hi,int depth)
            {
                double mid=(lo+hi)/2,d=Dist(mid);if(d< -contact)return false;
                if(d-curve.Length*(hi-lo)/2>=-contact)return true;
                if(depth>=24||samples>250000){exhausted=true;return false;}
                return Certify(lo,mid,depth+1)&&Certify(mid,hi,depth+1);
            }
            double start=Dist(0),end=Dist(1);
            if(start< -contact||end< -contact||!Certify(0,1,0))
            {
                incomplete=exhausted;
                error=incomplete?"Clear cover numerical verification is incomplete.":$"{(edge.Top?"Top face":"Side / bottom face")} clear cover is insufficient, or a safe bound for the continuous curve was not obtained.";return false;
            }
        }
        if(allowTop)
        {
            double limit=doc.Host.OuterRadiusMm-(zeroCover?0:doc.CheckSettings.SideCoverMm)-diameter/2;
            if(curve.TryRadialRange(out _,out double maxR)&&maxR>limit+contact){error="Extended rebar exceeds the effective side boundary.";return false;}
            // For non-constant-height arcs, certify radial containment with a Lipschitz bound as well.
            if(!curve.TryRadialRange(out _,out _))
            {
                bool Radial(double lo,double hi,int depth){double mid=(lo+hi)/2,clear=limit-Radius(curve.At(mid));if(clear< -contact)return false;if(clear-curve.Length*(hi-lo)/2>=-contact)return true;return depth<24&&Radial(lo,mid,depth+1)&&Radial(mid,hi,depth+1);}
                if(!Radial(0,1,0)){error="Extended rebar failed the side-boundary check.";return false;}
            }
        }
        return true;
    }
    static void AddExisting(ValidationReport report,List<Candidate> all,List<ExistingBarPath> existing)
    {
        foreach(var bar in existing)
        {
            try
            {
                Require(Finite(bar.DiameterMm)&&bar.DiameterMm>0,"Existing rebar diameter is invalid.");var curves=bar.Curves.Select(c=>new Eval(c)).ToList();Require(curves.Count>0,"Existing path is empty.");
                var path=new BarPath3{DraftId=$"existing/{bar.ElementId}",MemberIndex=bar.PositionIndex,DiameterMm=bar.DiameterMm,Curves=bar.Curves};
                all.Add(new(path,bar,true,Bounds.Union(curves.Select(c=>c.Box)).Expand(bar.DiameterMm/2)));
            }
            catch(Exception e){report.IsComplete=false;Issue(report,"",-1,ValidationKind.Incomplete,$"Existing rebar {bar.ElementId}: {e.Message}");}
        }
    }
    static void Collisions(ValidationReport report,List<Candidate> candidates,double tolerance,bool allowCollision)
    {
        var rows=candidates.OrderBy(c=>c.Box.MinX).ToArray();int work=0;
        for(int i=0;i<rows.Length;i++)for(int j=i+1;j<rows.Length&&rows[j].Box.MinX<=rows[i].Box.MaxX+Contact;j++)
        {
            var a=rows[i];var b=rows[j];if(a.Existing&&b.Existing||!a.Box.Overlap(b.Box))continue;
            if(++work>MaxPairs||report.Issues.Count>2000)
            {
                if(allowCollision)report.AcceptedCollisions.Add(new(){Kind=ValidationKind.Incomplete,Message="Accepted collision warning · Pair count exceeds the limit; remaining collisions are unquantified."});
                else{report.IsComplete=false;Issue(report,"",-1,ValidationKind.Incomplete,"Check limit exceeded. Process smaller groups.");}return;
            }
            if(a.Existing)(a,b)=(b,a);
            try
            {
                double gap=tolerance>0||allowCollision?CertifiedClearanceMm(a.Actual,b.Actual):WallValidation.CertifiedSeparationMm(a.Actual,b.Actual);
                Require(!double.IsNaN(gap)&&!double.IsNegativeInfinity(gap),"No valid collision distance was obtained.");
                if(gap< -(tolerance>0||allowCollision?CollisionNumericalEpsilonMm:Contact))
                {
                    var issue=new ValidationIssue{DraftId=a.Path.DraftId,MemberIndex=a.Path.MemberIndex,OtherDraftId=b.Existing?"":b.Path.DraftId,
                        OtherMemberIndex=b.Path.MemberIndex,ExistingElementId=b.Existing?b.Actual.ElementId:null,Kind=ValidationKind.Collision,ClearanceMm=gap,
                        Message=$"Collision with {(b.Existing?"existing rebar "+b.Actual.ElementId:"another bar")}; penetration upper bound {-gap:0.######} mm."};
                    if(allowCollision||tolerance>0&&AcceptsCollision(gap,tolerance)){issue.Message="Accepted · "+issue.Message;report.AcceptedCollisions.Add(issue);}
                    else report.Issues.Add(issue);
                }
            }
            catch(Exception e)
            {
                if(allowCollision)report.AcceptedCollisions.Add(new(){DraftId=a.Path.DraftId,MemberIndex=a.Path.MemberIndex,OtherDraftId=b.Existing?"":b.Path.DraftId,
                    OtherMemberIndex=b.Path.MemberIndex,ExistingElementId=b.Existing?b.Actual.ElementId:null,Kind=ValidationKind.Incomplete,Message="Accepted collision warning · Collision not quantified: "+e.Message});
                else{report.IsComplete=false;Issue(report,a.Path.DraftId,a.Path.MemberIndex,ValidationKind.Incomplete,"Collision check incomplete: "+e.Message);}
            }
        }
    }
    static void Issue(ValidationReport r,string id,int index,ValidationKind kind,string text)=>r.Issues.Add(new(){DraftId=id,MemberIndex=index,Kind=kind,Message=text});
    static bool Inside(List<SketchPoint> p,double r,double z)
    {
        bool yes=false;for(int i=0,j=p.Count-1;i<p.Count;j=i++)
        {var a=p[j];var b=p[i];if(Distance2(r,z,a,b)<Eps)return true;if((a.Z>z)!=(b.Z>z)&&r<(b.Rho-a.Rho)*(z-a.Z)/(b.Z-a.Z)+a.Rho)yes=!yes;}return yes;
    }
    static double TopAt(List<SketchPoint> p,double r)
    {
        double top=double.NegativeInfinity;for(int i=0;i<p.Count;i++)
        {var a=p[i];var b=p[(i+1)%p.Count];if(Math.Abs(a.Rho-b.Rho)<Eps){if(Math.Abs(r-a.Rho)<Eps)top=Math.Max(top,Math.Max(a.Z,b.Z));continue;}double t=(r-a.Rho)/(b.Rho-a.Rho);if(t>=-Eps&&t<=1+Eps)top=Math.Max(top,a.Z+t*(b.Z-a.Z));}return top;
    }
    static double Distance2(double x,double y,SketchPoint a,SketchPoint b)
    {double dx=b.Rho-a.Rho,dy=b.Z-a.Z,t=Math.Clamp(((x-a.Rho)*dx+(y-a.Z)*dy)/(dx*dx+dy*dy),0,1);return Math.Sqrt(Math.Pow(x-a.Rho-t*dx,2)+Math.Pow(y-a.Z-t*dy,2));}
    static Curve3 To3(SketchSegment s,Func<SketchPoint,Point3> convert)=>new(){Kind=s.Kind,Start=convert(s.Start),Middle=s.Kind==SegmentKind.Arc?convert(s.Middle):new(),End=convert(s.End)};
    static Curve3 Arc(Point3 start,Point3 mid,Point3 end)=>new(){Kind=SegmentKind.Arc,Start=start,Middle=mid,End=end};
    static SketchSegment Line2(SketchPoint a,SketchPoint b)=>new(){Kind=SegmentKind.Line,Start=a,End=b};
    static void Require(bool condition,string message){if(!condition)throw new ArgumentException(message);}
    static bool Finite(double v)=>double.IsFinite(v)&&Math.Abs(v)<=1e9;
    static double Radius(Point3 p)=>Math.Sqrt(p.X*p.X+p.Y*p.Y);
    static Point3 Add(Point3 a,Point3 b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    static Point3 Sub(Point3 a,Point3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    static Point3 Mul(Point3 a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
    static double Dot(Point3 a,Point3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    static Point3 Cross(Point3 a,Point3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    static double Length(Point3 a)=>Math.Sqrt(Dot(a,a));
    static Point3 Unit(Point3 a){double n=Length(a);Require(n>Eps,"Path direction is invalid.");return Mul(a,1/n);}
    sealed class Eval
    {
        public Curve3 Curve;public bool Arc;public double Radius,Length,Sweep;public Point3 Center=new(),Ex=new(),Ey=new();public Bounds Box;
        public Eval(Curve3 curve)
        {
            Curve=curve;Require(curve.Kind is SegmentKind.Line or SegmentKind.Arc,"Unsupported curve type.");Arc=curve.Kind==SegmentKind.Arc;
            foreach(var p in Arc?new[]{curve.Start,curve.Middle,curve.End}:new[]{curve.Start,curve.End})Require(Finite(p.X)&&Finite(p.Y)&&Finite(p.Z),"Curve coordinates are invalid or exceed the unit range.");
            Require(SlabGeometry.Length(Sub(curve.End,curve.Start))>Eps,"Curve contains a zero-length segment.");
            if(!Arc){Length=SlabGeometry.Length(Sub(curve.End,curve.Start));Box=Bounds.Points(new[]{curve.Start,curve.End});return;}
            var u=Sub(curve.Middle,curve.Start);var v=Sub(curve.End,curve.Start);var n=Cross(u,v);double nn=Dot(n,n);
            Require(nn>1e-12*Dot(u,u)*Dot(v,v),"The three arc points are collinear or numerically unstable.");
            Center=Add(curve.Start,Mul(Add(Mul(Cross(n,u),Dot(v,v)),Mul(Cross(v,n),Dot(u,u))),1/(2*nn)));
            Radius=SlabGeometry.Length(Sub(curve.Start,Center));Ex=Unit(Sub(curve.Start,Center));Ey=Cross(Unit(n),Ex);
            double Angle(Point3 p){var d=Sub(p,Center);double a=Math.Atan2(Dot(d,Ey),Dot(d,Ex));return a<0?a+2*Math.PI:a;}
            Sweep=Angle(curve.End);double middle=Angle(curve.Middle);Require(Sweep>Eps&&middle>0&&middle<Sweep,"Arc through-point is outside the directed arc.");Length=Radius*Sweep;
            var points=new List<Point3>{curve.Start,curve.End};
            foreach(var pair in new[]{(Ex.X,Ey.X),(Ex.Y,Ey.Y),(Ex.Z,Ey.Z)})
            {double a=Math.Atan2(pair.Item2,pair.Item1);for(int k=-2;k<=3;k++){double t=a+k*Math.PI;if(t>0&&t<Sweep)points.Add(At(t/Sweep));}}
            Box=Bounds.Points(points);
        }
        public Point3 At(double t)=>!Arc?Add(Curve.Start,Mul(Sub(Curve.End,Curve.Start),t)):Add(Center,Mul(Add(Mul(Ex,Math.Cos(Sweep*t)),Mul(Ey,Math.Sin(Sweep*t))),Radius));
        public Point3 Tangent(double t)=>!Arc?Unit(Sub(Curve.End,Curve.Start)):Add(Mul(Ex,-Math.Sin(Sweep*t)),Mul(Ey,Math.Cos(Sweep*t)));
        public bool TryRadialRange(out double min,out double max)
        {
            if(!Arc)
            {var d=Sub(Curve.End,Curve.Start);double den=d.X*d.X+d.Y*d.Y,t=den>Eps?Math.Clamp(-(Curve.Start.X*d.X+Curve.Start.Y*d.Y)/den,0,1):0;min=SlabGeometry.Radius(At(t));max=Math.Max(SlabGeometry.Radius(Curve.Start),SlabGeometry.Radius(Curve.End));return true;}
            if(Math.Abs(Center.X)<Eps&&Math.Abs(Center.Y)<Eps&&Math.Abs(Ex.Z)<Eps&&Math.Abs(Ey.Z)<Eps){min=max=Radius;return true;}
            min=max=0;return false;
        }
    }
    sealed record Bounds(double MinX,double MinY,double MinZ,double MaxX,double MaxY,double MaxZ)
    {
        public Bounds Expand(double d)=>new(MinX-d,MinY-d,MinZ-d,MaxX+d,MaxY+d,MaxZ+d);
        public bool Overlap(Bounds b)=>MinX<=b.MaxX&&MaxX>=b.MinX&&MinY<=b.MaxY&&MaxY>=b.MinY&&MinZ<=b.MaxZ&&MaxZ>=b.MinZ;
        public double Distance(Bounds b)
        {double x=Math.Max(0,Math.Max(MinX-b.MaxX,b.MinX-MaxX)),y=Math.Max(0,Math.Max(MinY-b.MaxY,b.MinY-MaxY)),z=Math.Max(0,Math.Max(MinZ-b.MaxZ,b.MinZ-MaxZ));return Math.Sqrt(x*x+y*y+z*z);}
        public static Bounds Points(IEnumerable<Point3> points){var p=points.ToArray();return new(p.Min(v=>v.X),p.Min(v=>v.Y),p.Min(v=>v.Z),p.Max(v=>v.X),p.Max(v=>v.Y),p.Max(v=>v.Z));}
        public static Bounds Union(IEnumerable<Bounds> boxes){var b=boxes.ToArray();Require(b.Length>0,"An empty path has no bounds.");return new(b.Min(v=>v.MinX),b.Min(v=>v.MinY),b.Min(v=>v.MinZ),b.Max(v=>v.MaxX),b.Max(v=>v.MaxY),b.Max(v=>v.MaxZ));}
    }
}
