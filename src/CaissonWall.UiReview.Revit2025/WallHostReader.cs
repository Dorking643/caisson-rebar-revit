using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace CaissonWallUi;

/// <summary>Reads real host and scene geometry on the Revit API thread. Never starts a transaction.</summary>
public static class WallHostReader
{
    const double EpsilonMm = .001;
    public static double Mm(double value) => UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters);

    public static HostContext ReadHost(UIApplication ui, long? hostId = null, Document? document = null)
    {
        var doc = document ?? ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Open a project first.");
        Element? host;
        if (hostId.HasValue) host = doc.GetElement(new ElementId(hostId.Value));
        else
        {
            var selected = ui.ActiveUIDocument!.Selection.GetElementIds().Select(doc.GetElement).Where(e => e is not null).ToArray();
            host = selected.FirstOrDefault(e => Mark(e!) == "CJ-WALL");
            var marked = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(e => Mark(e) == "CJ-WALL").ToArray();
            host ??= marked.Length == 1 ? marked[0] : null;
            if (host is null && marked.Length > 1) throw new InvalidOperationException("Multiple walls were found. Select the host in Revit first.");
        }
        if (host is null || !RebarHostData.IsValidHost(host)) throw new InvalidOperationException("Wall host is missing or cannot host rebar.");
        var solids = Solids(host);
        if (solids.Count != 1) throw new InvalidOperationException("Only a wall formed by a single solid of revolution is supported.");
        var box = host.get_BoundingBox(null) ?? throw new InvalidOperationException("Wall boundary could not be read.");
        var corners = BoxCorners(box);
        var context = new HostContext
        {
            DocumentTitle = doc.Title, DocumentKey = doc.PathName + "#" + doc.ProjectInformation.UniqueId,
            HostId = host.Id.Value, HostUniqueId = host.UniqueId, HostName = "CJ-WALL",
            OriginXmm = (corners.Min(p => Mm(p.X)) + corners.Max(p => Mm(p.X))) / 2,
            OriginYmm = (corners.Min(p => Mm(p.Y)) + corners.Max(p => Mm(p.Y))) / 2,
            TopZmm = corners.Max(p => Mm(p.Z)), HeightMm = Mm(corners.Max(p => p.Z) - corners.Min(p => p.Z))
        };
        var mainFaces = ReadHostFaces(solids[0], context, out var openingFaces);
        context.SectionBoundary = SectionProfile(mainFaces, context);
        context.InnerRadiusMm = context.SectionBoundary.Min(p => p.Rho);
        context.OuterRadiusMm = context.SectionBoundary.Max(p => p.Rho);
        if (context.InnerRadiusMm <= 0 || context.OuterRadiusMm <= context.InnerRadiusMm || context.HeightMm <= 0)
            throw new InvalidOperationException("Wall boundary dimensions are invalid.");
        context.HostOpenings = ReadOpenings(openingFaces, context);
        VerifyOpeningGeometry(solids[0], context);
        context.IsVerified = true;
        return context;
    }

    public static double? ReadSourceCover(Document document, HostContext host)
    {
        var element = document.GetElement(new ElementId(host.HostId));
        if (element is null || element.UniqueId != host.HostUniqueId) return null;
        Parameter? parameter = element.LookupParameter("图纸保护层");
        if (parameter is null && element is FamilyInstance instance) parameter = instance.Symbol.LookupParameter("图纸保护层");
        if (parameter is { StorageType: StorageType.Double, HasValue: true })
        {
            double cover = Mm(parameter.AsDouble());
            if (double.IsFinite(cover) && cover >= 0) return cover;
        }
        var common = RebarHostData.GetRebarHostData(element)?.GetCommonCoverType();
        if (common is not null)
        {
            double cover = Mm(common.CoverDistance);
            if (double.IsFinite(cover) && cover >= 0) return cover;
        }
        return null;
    }

    /// <summary>All scene bars whose bounding boxes meet the host are inspected, regardless of ownership or host.</summary>
    public static List<ExistingBarPath> ReadExisting(Document document, HostContext host, ISet<long>? excludedIds = null)
    {
        var result = new List<ExistingBarPath>();
        var failures = new List<string>();
        var actualHost = document.GetElement(new ElementId(host.HostId));
        if (actualHost is null || actualHost.UniqueId != host.HostUniqueId)
            throw new InvalidOperationException("Host identity has changed.");
        foreach (var rebar in new FilteredElementCollector(document).OfClass(typeof(Rebar)).Cast<Rebar>())
        {
            if (excludedIds?.Contains(rebar.Id.Value) == true) continue;
            if (!NearHost(rebar, host)) continue;
            try
            {
                if (document.GetElement(rebar.GetTypeId()) is not RebarBarType type) throw new InvalidOperationException("Rebar type could not be read");
                double diameter = Mm(type.BarModelDiameter);
                if (!double.IsFinite(diameter) || diameter <= 0) throw new InvalidOperationException("Physical bar diameter is invalid");
                if (rebar.NumberOfBarPositions < 1 || rebar.NumberOfBarPositions > 10000) throw new InvalidOperationException("Rebar set count is invalid or too large");
                for (int i = 0; i < rebar.NumberOfBarPositions; i++)
                {
                    if (!rebar.DoesBarExistAtPosition(i)) continue;
                    // Since Revit 2022 this includes the distribution and per-bar moved transforms exactly once.
                    var curves = rebar.GetTransformedCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, i);
                    if (curves.Count == 0) throw new InvalidOperationException("Centerline is empty");
                    var row = new ExistingBarPath { ElementId = rebar.Id.Value, PositionIndex = i, DiameterMm = diameter };
                    foreach (var curve in curves) row.Curves.AddRange(ToLocalCurves(curve, host));
                    result.Add(row);
                }
            }
            catch (Exception error) { failures.Add($"Rebar {rebar.Id.Value}: {error.Message}"); }
        }
        // Do not silently certify an unsupported reinforcement carrier as an empty scene.
        foreach (var reinforcement in new FilteredElementCollector(document).OfClass(typeof(RebarInSystem)).WhereElementIsNotElementType())
            if (NearHost(reinforcement, host)) failures.Add($"System rebar {reinforcement.Id.Value} has not been loaded");
        foreach (var reinforcement in new FilteredElementCollector(document).OfClass(typeof(RebarContainer)).WhereElementIsNotElementType())
            if (NearHost(reinforcement, host)) failures.Add($"Rebar container {reinforcement.Id.Value} has not been loaded");
        host.ExistingScanComplete = failures.Count == 0;
        host.ExistingScanNote = failures.Count == 0 ? $"Loaded {result.Count} existing bars" : string.Join("; ", failures.Take(6));
        host.ExistingSnapshotUtc = DateTime.UtcNow.ToString("o");
        return result;
    }

    public static void Refresh(ReviewDocument design, UIApplication ui, ISet<long>? excludedIds = null, bool initializeCover = false)
    {
        var fresh = ReadHost(ui, design.Host.HostId > 0 ? design.Host.HostId : null);
        design.Host = fresh;
        design.ExistingBars = ReadExisting(ui.ActiveUIDocument.Document, fresh, excludedIds);
        if (initializeCover && !design.CheckSettings.CoverConfirmed)
        {
            var cover = ReadSourceCover(ui.ActiveUIDocument.Document, fresh);
            if (cover.HasValue)
            {
                design.CheckSettings.InnerCoverMm = design.CheckSettings.OuterCoverMm = cover.Value;
                design.CheckSettings.TopCoverMm = design.CheckSettings.BottomCoverMm = cover.Value;
                design.CheckSettings.CoverConfirmed = true;
            }
        }
    }

    public static List<Solid> Solids(Element element)
    {
        var result = new List<Solid>();
        void Walk(GeometryElement geometry)
        {
            foreach (var item in geometry)
                if (item is Solid solid && solid.Volume > 1e-10) result.Add(solid);
                else if (item is GeometryInstance instance) Walk(instance.GetInstanceGeometry());
        }
        var geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
        if (geometry is not null) Walk(geometry);
        return result;
    }

    public static List<Curve3> ToLocalCurves(Curve curve, HostContext host)
    {
        if (curve is Arc circle && !curve.IsBound)
        {
            using var first = Arc.Create(circle.Center, circle.Radius, 0, Math.PI, circle.XDirection, circle.YDirection);
            using var second = Arc.Create(circle.Center, circle.Radius, Math.PI, 2 * Math.PI, circle.XDirection, circle.YDirection);
            return [ToLocalCurve(first, host), ToLocalCurve(second, host)];
        }
        return [ToLocalCurve(curve, host)];
    }
    public static Point3 Local(XYZ point, HostContext host) => new(Mm(point.X) - host.OriginXmm, Mm(point.Y) - host.OriginYmm, Mm(point.Z) - host.TopZmm);
    static Curve3 ToLocalCurve(Curve curve, HostContext host)
    {
        if (!curve.IsBound || curve is not Line && curve is not Arc) throw new InvalidOperationException("Centerline contains an unsupported curve");
        var result = new Curve3 { Kind = curve is Arc ? SegmentKind.Arc : SegmentKind.Line,
            Start = Local(curve.GetEndPoint(0), host), Middle = Local(curve.Evaluate(.5, true), host), End = Local(curve.GetEndPoint(1), host) };
        if (new[] { result.Start, result.Middle, result.End }.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)))
            throw new InvalidOperationException("Centerline coordinates are invalid");
        return result;
    }

    static string? Mark(Element element) => element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
    static XYZ[] BoxCorners(BoundingBoxXYZ box) => (from x in new[] { box.Min.X, box.Max.X }
        from y in new[] { box.Min.Y, box.Max.Y } from z in new[] { box.Min.Z, box.Max.Z }
        select box.Transform.OfPoint(new XYZ(x, y, z))).ToArray();
    static bool NearHost(Element element, HostContext host)
    {
        // A missing/invalid bounding box is retained and reported by the centreline read, never silently omitted.
        var box = element.get_BoundingBox(null); if (box is null) return true;
        var points = BoxCorners(box); double pad = 1;
        return points.Min(p => Mm(p.X)) <= host.OriginXmm + host.OuterRadiusMm + pad && points.Max(p => Mm(p.X)) >= host.OriginXmm - host.OuterRadiusMm - pad &&
            points.Min(p => Mm(p.Y)) <= host.OriginYmm + host.OuterRadiusMm + pad && points.Max(p => Mm(p.Y)) >= host.OriginYmm - host.OuterRadiusMm - pad &&
            points.Min(p => Mm(p.Z)) <= host.TopZmm + (host.SectionBoundary.Count>0?host.SectionBoundary.Max(p=>p.Z):0) + pad && points.Max(p => Mm(p.Z)) >= host.TopZmm - host.HeightMm - pad;
    }
    static List<Face> ReadHostFaces(Solid solid, HostContext host, out List<CylindricalFace> openings)
    {
        // The actual vertical surfaces establish the axis. A trimmed hole must not determine it from its own face.
        var axisOrigins = new List<XYZ>();
        foreach (Face face in solid.Faces)
        {
            (XYZ? axis, XYZ? origin) = face switch
            {
                CylindricalFace cylinder => (cylinder.Axis, cylinder.Origin),
                ConicalFace cone => (cone.Axis, cone.Origin),
                RevolvedFace revolved when revolved.Curve is Line => (revolved.Axis, revolved.Origin),
                _ => (null, null)
            };
            if (axis is not null && axis.Normalize().CrossProduct(XYZ.BasisZ).GetLength() < 1e-9) axisOrigins.Add(origin!);
        }
        if (axisOrigins.Count == 0) throw new InvalidOperationException("Wall has no coaxial surface of revolution to establish its center axis.");
        host.OriginXmm = Mm(axisOrigins[0].X); host.OriginYmm = Mm(axisOrigins[0].Y);
        bool Axis(XYZ axis, XYZ origin) => axis.Normalize().CrossProduct(XYZ.BasisZ).GetLength() < 1e-9 &&
            Math.Abs(Mm(origin.X) - host.OriginXmm) < EpsilonMm && Math.Abs(Mm(origin.Y) - host.OriginYmm) < EpsilonMm;
        var main = new List<Face>(); openings = new();
        foreach (Face face in solid.Faces)
        {
            bool valid = face switch
            {
                PlanarFace plane => plane.FaceNormal.CrossProduct(XYZ.BasisZ).GetLength() < 1e-9,
                CylindricalFace cylinder => Axis(cylinder.Axis, cylinder.Origin),
                ConicalFace cone => Axis(cone.Axis, cone.Origin),
                RevolvedFace revolved => revolved.Curve is Line && Axis(revolved.Axis, revolved.Origin),
                _ => false
            };
            if (valid) main.Add(face);
            else if (face is CylindricalFace hole && Math.Abs(hole.Axis.Normalize().Z) < 1e-9) openings.Add(hole);
            else throw new InvalidOperationException("Unsupported wall geometry. Only coaxial solids with straight generatrices and transverse circular through-openings are supported.");
        }
        if (openings.Count > 128) throw new InvalidOperationException("Too many wall opening faces. Separate verification is required.");
        return main;
    }
    static List<SketchPoint> SectionProfile(IEnumerable<Face> faces, HostContext host)
    {
        var edges = new List<(SketchPoint A, SketchPoint B)>();
        bool Same(SketchPoint a, SketchPoint b) => SketchDrawing.Distance(a, b) <= EpsilonMm;
        foreach (Face face in faces)
        {
            var points = new List<SketchPoint>();
            // Hole intersection splines are deliberately excluded. Only actual coaxial circular edges establish
            // the two ends of a straight generator; every remaining surface is certified below against the solid.
            foreach (var loop in face.GetEdgesAsCurveLoops()) foreach (var curve in loop)
            {
                if (curve is not Arc arc || arc.Normal.CrossProduct(XYZ.BasisZ).GetLength() >= 1e-9 ||
                    Math.Abs(Mm(arc.Center.X) - host.OriginXmm) >= EpsilonMm || Math.Abs(Mm(arc.Center.Y) - host.OriginYmm) >= EpsilonMm) continue;
                var section = new SketchPoint(Mm(arc.Radius), Mm(arc.Center.Z) - host.TopZmm);
                if (!points.Any(p => Same(p, section))) points.Add(section);
            }
            if (points.Count != 2) throw new InvalidOperationException("Wall section does not form a continuous straight generatrix. Creation is disabled.");
            var a = points[0]; var b = points[1];
            if (!edges.Any(e => Same(e.A, a) && Same(e.B, b) || Same(e.A, b) && Same(e.B, a))) edges.Add((a, b));
        }
        if (edges.Count < 3) throw new InvalidOperationException("Wall section has insufficient boundary edges.");
        var first = edges[0]; var path = new List<SketchPoint> { first.A, first.B }; edges.RemoveAt(0);
        while (edges.Count > 0)
        {
            var matches = edges.Where(e => Same(e.A, path[^1]) || Same(e.B, path[^1])).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Wall section boundary has branches or gaps.");
            var edge = matches[0]; path.Add(Same(edge.A, path[^1]) ? edge.B : edge.A); edges.Remove(edge);
        }
        if (!Same(path[0], path[^1])) throw new InvalidOperationException("Wall section boundary is not closed.");
        path.RemoveAt(path.Count - 1);
        return path;
    }

    static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    static XYZ World(Point3 p, HostContext host) => new(Ft(host.OriginXmm + p.X), Ft(host.OriginYmm + p.Y), Ft(host.TopZmm + p.Z));
    static List<HostOpening> ReadOpenings(List<CylindricalFace> faces, HostContext host)
    {
        var result = new List<HostOpening>();
        foreach (var face in faces)
        {
            var axis = face.Axis.Normalize(); if (axis.X < -1e-9 || Math.Abs(axis.X) < 1e-9 && axis.Y < 0) axis = -axis;
            var rx = face.get_Radius(0); var ry = face.get_Radius(1); double radius = rx.GetLength();
            if (radius <= 0 || Math.Abs(Mm(radius - ry.GetLength())) > EpsilonMm ||
                Math.Abs(rx.Normalize().DotProduct(ry.Normalize())) > 1e-9 ||
                Math.Abs(rx.Normalize().DotProduct(axis)) > 1e-9 || Math.Abs(ry.Normalize().DotProduct(axis)) > 1e-9)
                throw new InvalidOperationException("Transverse opening is not a supported circular cylinder.");
            var anchor = new XYZ(Ft(host.OriginXmm), Ft(host.OriginYmm), face.Origin.Z);
            var delta = anchor - face.Origin;
            if (Mm((delta - axis * delta.DotProduct(axis)).GetLength()) > EpsilonMm || Mm(radius) >= host.InnerRadiusMm)
                throw new InvalidOperationException("Only transverse circular openings on one wall side with an axis through the well center are supported.");
            var bounds = face.GetBoundingBox(); UV? inside = null;
            for (int u = 0; u < 17 && inside is null; u++) for (int v = 0; v < 17 && inside is null; v++)
            {
                var uv = new UV(bounds.Min.U + (bounds.Max.U - bounds.Min.U) * (u + .5) / 17,
                    bounds.Min.V + (bounds.Max.V - bounds.Min.V) * (v + .5) / 17);
                if (face.IsInside(uv)) inside = uv;
            }
            if (inside is null) throw new InvalidOperationException("The interior side of the cylindrical opening could not be verified.");
            var sample = face.Evaluate(inside); var radial = sample - anchor; radial -= axis * radial.DotProduct(axis);
            if (face.ComputeNormal(inside).DotProduct(radial.Normalize()) > -.999999)
                throw new InvalidOperationException("Transverse cylinder is a protruding solid and cannot be read as an opening.");
            var projections = new[] { face.Evaluate(bounds.Min), face.Evaluate(bounds.Max),
                face.Evaluate(new UV(bounds.Min.U, bounds.Max.V)), face.Evaluate(new UV(bounds.Max.U, bounds.Min.V)) }
                .Select(p => Mm((p - anchor).DotProduct(axis))).ToArray();
            double start = projections.Min(), end = projections.Max();
            if (end - start <= EpsilonMm || start <= EpsilonMm && end >= -EpsilonMm)
                throw new InvalidOperationException("Opening crosses the well center or has an invalid axial range. Separate verification is required.");
            // Extend the finite trimmed surface envelope by 1 mm to avoid coincident artificial end caps.
            // This does not create a through-going infinite cylinder across the opposite wall.
            var a = Local(anchor + axis * Ft(start - 1), host); var b = Local(anchor + axis * Ft(end + 1), host);
            var duplicate = result.FirstOrDefault(h => Math.Abs(h.RadiusMm - Mm(radius)) <= EpsilonMm &&
                Distance(h.AxisStart, a) <= EpsilonMm && Distance(h.AxisEnd, b) <= EpsilonMm);
            if (duplicate is not null) duplicate.SourceFaceCount++;
            else result.Add(new HostOpening { AxisStart = a, AxisEnd = b, RadiusMm = Mm(radius), SourceFaceCount = 1 });
        }
        result = result.OrderBy(h => h.AxisStart.Z).ThenBy(h => h.AxisStart.X).ThenBy(h => h.AxisStart.Y).ToList();
        for (int i = 0; i < result.Count; i++) result[i].Id = "孔 " + (i + 1);
        return result;
    }
    static double Distance(Point3 a, Point3 b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2)+Math.Pow(a.Z-b.Z,2));

    static void VerifyOpeningGeometry(Solid actual, HostContext host)
    {
        if (host.HostOpenings.Count == 0)
        {
            host.OpeningGeometryVerified = true; host.OpeningGeometryNote = "Coaxial wall verified; no transverse openings."; return;
        }
        Solid? reconstructed = null;
        try
        {
            using var profile = new CurveLoop();
            for (int i = 0; i < host.SectionBoundary.Count; i++)
            {
                var a = host.SectionBoundary[i]; var b = host.SectionBoundary[(i+1)%host.SectionBoundary.Count];
                profile.Append(Line.CreateBound(World(new Point3(a.Rho,0,a.Z),host),World(new Point3(b.Rho,0,b.Z),host)));
            }
            using var frame = new Frame(World(new Point3(),host),XYZ.BasisX,XYZ.BasisY,XYZ.BasisZ);
            reconstructed = GeometryCreationUtilities.CreateRevolvedGeometry(frame,new List<CurveLoop>{profile},0,2*Math.PI);
            foreach (var hole in host.HostOpenings)
            {
                var start = World(hole.AxisStart,host); var end = World(hole.AxisEnd,host); var axis=(end-start).Normalize();
                var x=XYZ.BasisZ; var y=axis.CrossProduct(x).Normalize();
                using var ring = new CurveLoop();
                ring.Append(Arc.Create(start,Ft(hole.RadiusMm),0,Math.PI,x,y));
                ring.Append(Arc.Create(start,Ft(hole.RadiusMm),Math.PI,2*Math.PI,x,y));
                using var cutter=GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop>{ring},axis,start.DistanceTo(end));
                var next=BooleanOperationsUtils.ExecuteBooleanOperation(reconstructed,cutter,BooleanOperationsType.Difference);
                reconstructed.Dispose(); reconstructed=next;
            }
            using var missing=BooleanOperationsUtils.ExecuteBooleanOperation(actual,reconstructed,BooleanOperationsType.Difference);
            using var excess=BooleanOperationsUtils.ExecuteBooleanOperation(reconstructed,actual,BooleanOperationsType.Difference);
            double residual=(Math.Abs(missing.Volume)+Math.Abs(excess.Volume))*Math.Pow(304.8,3);
            double tolerance=Math.Max(1,Math.Abs(actual.Volume)*Math.Pow(304.8,3)*1e-8);
            if (!double.IsFinite(residual) || residual>tolerance)
                throw new InvalidOperationException($"Wall minus openings differs from the actual solid: symmetric difference {residual:0.###} mm³ (limit {tolerance:0.###}).");
            host.OpeningGeometryVerified=true;
            host.OpeningGeometryNote=$"Verified {host.HostOpenings.Count} transverse circular openings; symmetric difference from actual solid {residual:0.###} mm³.";
        }
        catch (Exception error)
        {
            host.OpeningGeometryVerified=false;
            throw new InvalidOperationException("Wall opening geometry verification failed. Openings were not ignored as a complete cylinder: "+error.Message,error);
        }
        finally { reconstructed?.Dispose(); }
    }
}
