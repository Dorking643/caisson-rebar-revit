using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#if !PURE_GEOMETRY
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
#endif

namespace CaissonWallUi;

/// <summary>A native, transformed centreline, already converted once to host-local millimetres.</summary>
public sealed class WallImportBar
{
    public long ElementId { get; set; }
    public string UniqueId { get; set; } = "";
    public int PositionIndex { get; set; }
    public long TypeId { get; set; }
    public string TypeUniqueId { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string SourceBarId { get; set; } = "";
    public string SourceMark { get; set; } = "";
    public string Grade { get; set; } = "";
    public double NominalDiameterMm { get; set; }
    public double ModelDiameterMm { get; set; }
    public double StandardBendDiameterMm { get; set; }
    public double StandardHookBendDiameterMm { get; set; }
    public double StirrupTieBendDiameterMm { get; set; }
    public bool AuthorizedTopExtension { get; set; }
    public List<Curve3> Curves { get; set; } = new();
}

/// <summary>MemberIndex also indexes the draft's parallel source identity lists.</summary>
public sealed class WallImportBinding
{
    public string DraftId { get; set; } = "";
    public int MemberIndex { get; set; }
    public long SourceElementId { get; set; }
    public string SourceUniqueId { get; set; } = "";
    public int SourcePositionIndex { get; set; }
    public string SourceBarId { get; set; } = "";
    public string SourceMark { get; set; } = "";
    public long SourceTypeId { get; set; }
    public string SourceTypeUniqueId { get; set; } = "";
    public string SourceTypeName { get; set; } = "";
    public double StandardBendDiameterMm { get; set; }
    public double StandardHookBendDiameterMm { get; set; }
    public double StirrupTieBendDiameterMm { get; set; }
    public double ActualAngleDeg { get; set; }
    public double RoundTripErrorMm { get; set; }
}

public sealed class WallRebarImportResult
{
    public ReviewDocument Document { get; set; } = new();
    public string Summary { get; set; } = "";
    public int SourceBarCount { get; set; }
    public int DraftCount { get; set; }
    public int RingCount { get; set; }
    public int ProfileCount { get; set; }
    public int ShapeCount { get; set; }
    public int UniformProfileGroupCount { get; set; }
    public bool SkippedNonEmpty { get; set; }
    public double MaxRoundTripErrorMm { get; set; }
    public List<string> Notes { get; set; } = new();
    public List<WallImportBinding> Bindings { get; set; } = new();
}

/// <summary>
/// Read-only native import. Geometry is never regenerated from metadata or nominal drawing lengths.
/// Unsupported geometry aborts the whole import; no partially imported document is returned.
/// </summary>
public static class WallRebarImporter
{
    public const double RoundTripToleranceMm = .000001;
    const double ShapeToleranceMm = .0000001;
    const double AngleToleranceDeg = .000000001;
    public const string SampleOwner = "case-a-original-wall";
    public static readonly Guid SampleSchemaId = new("a25097e6-fd9e-48c8-a7b2-c55edb59469c");

#if !PURE_GEOMETRY
    /// <summary>No transaction, creation, deletion, parameter write, or document save.</summary>
    public static WallRebarImportResult Import(UIApplication ui, ReviewDocument seed)
    {
        if (seed.Drafts.Count != 0) return NonEmpty(seed);
        var doc = !string.IsNullOrEmpty(seed.Host.DocumentKey)
            ? WallModelEngine.ResolveDocument(ui, seed)
            : ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Open a project first.");
        string key = doc.PathName + "#" + doc.ProjectInformation.UniqueId;
        if (!string.IsNullOrEmpty(seed.Host.DocumentKey) && seed.Host.DocumentKey != key)
            throw new InvalidOperationException("Active project does not match the draft host. Reload the host.");
        var design = ReviewDocument.FromJson(seed.ToJson());
        design.Host = WallHostReader.ReadHost(ui, seed.Host.HostId > 0 ? seed.Host.HostId : null, doc);
        if (!string.IsNullOrEmpty(seed.Host.HostUniqueId) && seed.Host.HostUniqueId != design.Host.HostUniqueId)
            throw new InvalidOperationException("Host identity has changed. Rebar was not imported.");
        var nativeBars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>()
            .Where(b => b.GetHostId().Value == design.Host.HostId).OrderBy(b => b.Id.Value).ToList();
        if (nativeBars.Count == 0) throw new InvalidOperationException("Current wall host has no native rebar to import.");
        var rows = new List<WallImportBar>();
        foreach (var rb in nativeBars)
        {
            try
            {
                if (doc.GetElement(rb.GetTypeId()) is not RebarBarType type)
                    throw new InvalidOperationException("Rebar type could not be read.");
                // The present UI identity lists bind one native instance to one member. Do not silently
                // flatten a multi-position set, which would make later source exclusion ambiguous.
                if (rb.NumberOfBarPositions != 1 || !rb.DoesBarExistAtPosition(0))
                    throw new InvalidOperationException("Import supports one bar per native instance. Multi-position rebar sets require separate mapping.");
                var metadata = ReadSampleMetadata(rb);
                string mark = Text(metadata, "source_mark");
                if (string.IsNullOrWhiteSpace(mark)) mark = rb.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                string grade = Text(metadata, "grade");
                if (string.IsNullOrWhiteSpace(grade)) grade = GradeInTypeName(type.Name);
                var row = new WallImportBar
                {
                    ElementId = rb.Id.Value, UniqueId = rb.UniqueId, TypeId = type.Id.Value,
                    TypeUniqueId = type.UniqueId, TypeName = type.Name, SourceBarId = Text(metadata, "bar_id"),
                    SourceMark = mark, Grade = grade,
                    NominalDiameterMm = WallHostReader.Mm(type.BarNominalDiameter),
                    ModelDiameterMm = WallHostReader.Mm(type.BarModelDiameter),
                    StandardBendDiameterMm = WallHostReader.Mm(type.StandardBendDiameter),
                    StandardHookBendDiameterMm = WallHostReader.Mm(type.StandardHookBendDiameter),
                    StirrupTieBendDiameterMm = WallHostReader.Mm(type.StirrupTieBendDiameter),
                    AuthorizedTopExtension = Text(metadata, "owner") == SampleOwner && mark is "N1" or "N2"
                };
                foreach (var curve in rb.GetTransformedCenterlineCurves(false, false, false,
                    MultiplanarOption.IncludeAllMultiplanarCurves, 0))
                    row.Curves.AddRange(WallHostReader.ToLocalCurves(curve, design.Host));
                rows.Add(row);
            }
            catch (Exception error)
            { throw new InvalidOperationException($"Rebar {rb.Id.Value} cannot be imported without loss: {error.Message}", error); }
        }
        var result = ImportReadback(design, rows, key);
        // Exclude only successfully imported identities, in this exact source document. Other scene
        // bars, including unsupported reinforcement carriers, remain checked by the scene reader.
        result.Document.ExistingBars = WallHostReader.ReadExisting(doc, result.Document.Host,
            rows.Select(r => r.ElementId).ToHashSet());
        double? cover = WallHostReader.ReadSourceCover(doc, result.Document.Host);
        if (cover.HasValue)
        {
            var s = result.Document.CheckSettings;
            s.InnerCoverMm = s.OuterCoverMm = s.TopCoverMm = s.BottomCoverMm = cover.Value;
            s.CoverConfirmed = true;
        }
        else result.Notes.Add("Host cover could not be read. Current UI values are retained; confirm them before checking.");
        return result;
    }

    static JsonObject? ReadSampleMetadata(Element element)
    {
        var schema = Schema.Lookup(SampleSchemaId);
        if (schema is null) return null;
        var entity = element.GetEntity(schema);
        if (!entity.IsValid()) return null;
        var field = schema.GetField("Data");
        if (field is null) return null;
        return JsonNode.Parse(entity.Get<string>(field)) as JsonObject;
    }
    static string Text(JsonObject? value, string key) => value?[key]?.ToString() ?? "";
    static string GradeInTypeName(string name)
        => Regex.Match(name, @"(?<![A-Z0-9])(HRB400E|HRB500E|HRB400|HRB500|HPB300)(?![A-Z0-9])").Value;
#endif

    sealed class Projection
    {
        public WallImportBar Source = new();
        public ReviewDraft Draft = new();
        public double AngleDeg;
    }

    /// <summary>Pure mathematical importer, also used by the native API route and offline tests.</summary>
    public static WallRebarImportResult ImportReadback(ReviewDocument seed, IReadOnlyList<WallImportBar> source, string sourceDocumentKey)
    {
        if (seed.Drafts.Count != 0) return NonEmpty(seed);
        if (source.Count == 0) throw new InvalidOperationException("No rebar available to import.");
        if (source.Count > 5000) throw new InvalidOperationException("Import is limited to 5000 bars at a time.");
        if (string.IsNullOrWhiteSpace(sourceDocumentKey)) throw new InvalidOperationException("Source project identity is missing.");
        if (!string.IsNullOrEmpty(seed.Host.DocumentKey) && seed.Host.DocumentKey != sourceDocumentKey)
            throw new InvalidOperationException("Source project identity does not match.");
        var result = new WallRebarImportResult { Document = ReviewDocument.FromJson(seed.ToJson()), SourceBarCount = source.Count };
        var design = result.Document;
        design.ImportSourceDocumentKey = sourceDocumentKey;
        var projected = new List<Projection>();
        var identities = new HashSet<long>();
        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        var typeRows = new Dictionary<string, WallImportBar>(StringComparer.Ordinal);
        foreach (var sourceBar in source)
        {
            ValidateSource(sourceBar);
            if (!identities.Add(sourceBar.ElementId) || !uniqueIds.Add(sourceBar.UniqueId))
                throw new InvalidOperationException($"Duplicate source rebar identity: {sourceBar.ElementId}.");
            if (typeRows.TryGetValue(sourceBar.TypeUniqueId, out var prior))
            {
                if (prior.TypeId != sourceBar.TypeId || prior.Grade != sourceBar.Grade ||
                    Math.Abs(prior.ModelDiameterMm - sourceBar.ModelDiameterMm) > RoundTripToleranceMm)
                    throw new InvalidOperationException($"Source type {sourceBar.TypeId} has inconsistent properties.");
            }
            else
            {
                typeRows.Add(sourceBar.TypeUniqueId, sourceBar);
                string typeId = TypeId(sourceBar);
                var old = design.Types.FirstOrDefault(t => t.Id == typeId);
                if (old is not null && (old.Grade != sourceBar.Grade || Math.Abs(old.DiameterMm - sourceBar.ModelDiameterMm) > RoundTripToleranceMm))
                    throw new InvalidOperationException($"UI already contains type {typeId} with different properties.");
                if (old is null) design.Types.Add(new ReviewBarType
                { Id = typeId, Name = sourceBar.TypeName, Grade = sourceBar.Grade, DiameterMm = sourceBar.ModelDiameterMm });
                if (string.IsNullOrWhiteSpace(sourceBar.Grade))
                    result.Notes.Add($"Grade for type {sourceBar.TypeId} has no explicit source and is left blank. It cannot be inferred from the deformation pattern alone.");
            }
            projected.Add(Project(sourceBar));
        }

        // Rings have no individual phase in this UI. Full-circle geometry is preserved exactly;
        // variable radius/elevation rings remain singletons instead of inventing an array rule.
        foreach (var item in projected.Where(p => p.Draft.Kind == DraftKind.Ring))
            AddDraft(result, new List<Projection> { item });

        var profiles = projected.Where(p => p.Draft.Kind == DraftKind.Profile).ToList();
        while (profiles.Count > 0)
        {
            var first = profiles[0];
            var equivalent = profiles.Where(p => EquivalentAbsolute(first, p)).OrderBy(p => p.AngleDeg).ToList();
            foreach (var p in equivalent) profiles.Remove(p);
            if (equivalent.Count > 1 && equivalent.Count <= GroupGeometry.CountLimit && Uniform(equivalent))
            {
                AddDraft(result, equivalent);
                result.UniformProfileGroupCount++;
            }
            else foreach (var p in equivalent) AddDraft(result, new List<Projection> { p });
        }

        // Stable ordering affects only the list, never group-member indices or source bindings.
        design.Drafts = design.Drafts.OrderBy(d => SourceMarkOrder(d.SourceMark)).ToList();
        // Shape definitions are shared, but each draft retains its own actual local snapshot.
        foreach (var draft in design.Drafts.Where(d => d.Kind == DraftKind.Profile))
        {
            var anchor = draft.Segments[0].Start.Copy();
            var local = ShapePlacement.CopySegments(draft.Segments, -anchor.Rho, -anchor.Z);
            var template = design.Shapes.FirstOrDefault(s => SameSegments(s.Segments, local));
            if (template is null)
            {
                template = new ShapeTemplate { Id = "native-shape/" + draft.Id, Name = draft.SourceMark + " · " + (design.Shapes.Count + 1), Segments = ShapePlacement.CopySegments(local) };
                design.Shapes.Add(template);
            }
            draft.ShapeTemplateId = template.Id; draft.ShapeName = template.Name;
            draft.InsertionPoint = anchor; draft.ShapeSnapshot = local;
        }
        design.ActiveTool = ReviewTool.Select; design.ActiveProfileId = null; design.PendingPoints.Clear();
        design.SavedUtc = DateTime.UtcNow.ToString("o");
        result.DraftCount = design.Drafts.Count;
        result.RingCount = projected.Count(p => p.Draft.Kind == DraftKind.Ring);
        result.ProfileCount = source.Count - result.RingCount;
        result.ShapeCount = design.Drafts.Select(d => d.ShapeTemplateId).Where(id => id is not null).Distinct().Count();
        result.Summary = $"Loaded {result.SourceBarCount} native bars from the current host: {result.RingCount} rings and {result.ProfileCount} profile bars; imported {result.DraftCount} drafts and {result.ShapeCount} actual shapes.";
        result.Notes.Add("Uniform circular groups retain their count and start angle. Bars with irregular angles, radii, or elevations remain separate drafts. Rings are reconstructed without laps. Import only reads the model.");
        result.Notes.Add("Shapes use the actual formed centerlines. Grades come from existing metadata or type names, not material testing. Native type identity and bend diameter are retained in the import binding.");
        return result;
    }

    static WallRebarImportResult NonEmpty(ReviewDocument seed) => new()
    {
        Document = ReviewDocument.FromJson(seed.ToJson()), SkippedNonEmpty = true, DraftCount = seed.Drafts.Count,
        Summary = "Drafts already exist. No drafts were overwritten or appended."
    };
    static string TypeId(WallImportBar bar) => "native-type/" + bar.TypeUniqueId;
    static int SourceMarkOrder(string mark)
        => mark.StartsWith("N", StringComparison.Ordinal) && int.TryParse(mark[1..], out int n) && n > 0 ? n : int.MaxValue;
    static void ValidateSource(WallImportBar b)
    {
        if (b.ElementId <= 0 || string.IsNullOrWhiteSpace(b.UniqueId) || b.TypeId <= 0 || string.IsNullOrWhiteSpace(b.TypeUniqueId))
            throw new InvalidOperationException("Native rebar or type identity is missing.");
        if (b.PositionIndex != 0) throw new InvalidOperationException($"Position mapping for rebar {b.ElementId} is not supported.");
        if (!double.IsFinite(b.ModelDiameterMm) || !double.IsFinite(b.NominalDiameterMm) || b.ModelDiameterMm <= 0 || b.NominalDiameterMm <= 0)
            throw new InvalidOperationException($"Rebar {b.ElementId} has an invalid diameter.");
        if (Math.Abs(b.ModelDiameterMm - b.NominalDiameterMm) > RoundTripToleranceMm)
            throw new InvalidOperationException($"Rebar {b.ElementId} has different model and nominal diameters. The single diameter field cannot represent both without loss.");
        if (b.Curves.Count == 0 || b.Curves.Any(c => c.Kind is not (SegmentKind.Line or SegmentKind.Arc) || Points(c).Any(p => !Finite(p))))
            throw new InvalidOperationException($"Centerline of rebar {b.ElementId} is empty or invalid.");
        for (int i = 1; i < b.Curves.Count; i++)
            if (Distance(b.Curves[i - 1].End, b.Curves[i].Start) > RoundTripToleranceMm)
                throw new InvalidOperationException($"Actual path of rebar {b.ElementId} is disconnected.");
    }
    static Projection Project(WallImportBar bar)
    {
        var draft = new ReviewDraft
        {
            Id = "native/" + bar.ElementId, Name = string.IsNullOrEmpty(bar.SourceMark) ? $"Loaded rebar {bar.ElementId}" : bar.SourceMark + " · " + bar.ElementId,
            TypeId = TypeId(bar), SourceMark = bar.SourceMark,
            AllowTopExtension = bar.AuthorizedTopExtension && bar.SourceMark is "N1" or "N2"
        };
        var projected = new Projection { Source = bar, Draft = draft };
        if (TryRing(bar.Curves, out var ring)) { draft.Kind = DraftKind.Ring; draft.RingPoint = ring; return projected; }
        var first = bar.Curves[0].Start;
        double angle = Math.Atan2(first.Y, first.X), ca = Math.Cos(angle), sa = Math.Sin(angle);
        SketchPoint Radial(Point3 p)
        {
            double rho = p.X * ca + p.Y * sa, off = -p.X * sa + p.Y * ca;
            if (Math.Abs(off) > RoundTripToleranceMm || rho <= 0)
                throw new InvalidOperationException($"Rebar {bar.ElementId} is neither a complete coaxial ring nor contained in one radial plane with positive radius.");
            return new(rho, p.Z);
        }
        draft.Kind = DraftKind.Profile;
        draft.Segments = bar.Curves.Select(c => new SketchSegment
        { Kind = c.Kind, Start = Radial(c.Start), Middle = c.Kind == SegmentKind.Arc ? Radial(c.Middle) : new(), End = Radial(c.End) }).ToList();
        projected.AngleDeg = Normalize(angle * 180 / Math.PI);
        // Disabled groups always mean zero degrees in GroupGeometry, including for one bar.
        draft.Group = new() { Enabled = true, Count = 1, ProfileMode = ProfileArrayMode.Count, StartAngleDeg = projected.AngleDeg };
        return projected;
    }
    static void AddDraft(WallRebarImportResult result, List<Projection> items)
    {
        var draft = items[0].Draft;
        if (items.Count > 1)
        {
            draft.Name = $"{draft.SourceMark} · {items.Count} bars (loaded)";
            draft.Group = new() { Enabled = true, Count = items.Count, ProfileMode = ProfileArrayMode.Count, StartAngleDeg = items[0].AngleDeg };
        }
        draft.SourceElementIds = items.Select(i => i.Source.ElementId).ToList();
        draft.SourceUniqueIds = items.Select(i => i.Source.UniqueId).ToList();
        result.Document.Drafts.Add(draft);
        if (!GroupGeometry.TryMembers(draft, out var members, out var error)) throw new InvalidOperationException(error);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i]; var source = item.Source;
            double residual = draft.Kind == DraftKind.Ring ? RingResidual(source.Curves, draft.RingPoint)
                : ProfileResidual(source.Curves, draft.Segments, members[i].AngleDeg);
            if (!double.IsFinite(residual) || residual > RoundTripToleranceMm)
                throw new InvalidOperationException($"Import round-trip error for rebar {source.ElementId}, {residual:G6} mm, exceeds the limit.");
            result.MaxRoundTripErrorMm = Math.Max(result.MaxRoundTripErrorMm, residual);
            result.Bindings.Add(new()
            {
                DraftId = draft.Id, MemberIndex = i, SourceElementId = source.ElementId, SourceUniqueId = source.UniqueId,
                SourcePositionIndex = source.PositionIndex, SourceBarId = source.SourceBarId, SourceMark = source.SourceMark,
                SourceTypeId = source.TypeId, SourceTypeUniqueId = source.TypeUniqueId, SourceTypeName = source.TypeName,
                StandardBendDiameterMm = source.StandardBendDiameterMm, StandardHookBendDiameterMm = source.StandardHookBendDiameterMm,
                StirrupTieBendDiameterMm = source.StirrupTieBendDiameterMm, ActualAngleDeg = item.AngleDeg, RoundTripErrorMm = residual
            });
        }
    }
    static bool EquivalentAbsolute(Projection a, Projection b)
        => a.Draft.TypeId == b.Draft.TypeId && a.Draft.SourceMark == b.Draft.SourceMark &&
           a.Draft.AllowTopExtension == b.Draft.AllowTopExtension && SameSegments(a.Draft.Segments, b.Draft.Segments);
    static bool Uniform(List<Projection> members)
    {
        double first = members[0].AngleDeg;
        for (int i = 0; i < members.Count; i++)
            if (Math.Abs(AngularDifference(members[i].AngleDeg, first + 360d * i / members.Count)) > AngleToleranceDeg) return false;
        return true;
    }
    static bool SameSegments(IReadOnlyList<SketchSegment> a, IReadOnlyList<SketchSegment> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Kind != b[i].Kind || SketchDrawing.Distance(a[i].Start, b[i].Start) > ShapeToleranceMm ||
                SketchDrawing.Distance(a[i].End, b[i].End) > ShapeToleranceMm ||
                a[i].Kind == SegmentKind.Arc && SketchDrawing.Distance(a[i].Middle, b[i].Middle) > ShapeToleranceMm) return false;
        return true;
    }
    static double ProfileResidual(IReadOnlyList<Curve3> source, IReadOnlyList<SketchSegment> segments, double degrees)
    {
        if (source.Count != segments.Count) return double.PositiveInfinity;
        double angle = degrees * Math.PI / 180, ca = Math.Cos(angle), sa = Math.Sin(angle), error = 0;
        Point3 Rotate(SketchPoint p) => new(p.Rho * ca, p.Rho * sa, p.Z);
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Kind != segments[i].Kind) return double.PositiveInfinity;
            error = Math.Max(error, Distance(source[i].Start, Rotate(segments[i].Start)));
            error = Math.Max(error, Distance(source[i].End, Rotate(segments[i].End)));
            if (source[i].Kind == SegmentKind.Arc) error = Math.Max(error, Distance(source[i].Middle, Rotate(segments[i].Middle)));
        }
        return error;
    }
    static bool TryRing(IReadOnlyList<Curve3> curves, out SketchPoint point)
    {
        point = new();
        if (curves.Count < 2 || curves.Any(c => c.Kind != SegmentKind.Arc)) return false;
        double radius = 0, z = curves[0].Start.Z, sum = 0; int direction = 0;
        foreach (var c in curves)
        {
            if (Points(c).Any(p => Math.Abs(p.Z - z) > RoundTripToleranceMm)) return false;
            // Fit the actual XY arc; a constant radius at three points alone would accept
            // an off-centre circle. Require its fitted centre to be the host's axis.
            var planar = new SketchSegment { Kind = SegmentKind.Arc,
                Start = new(c.Start.X, c.Start.Y), Middle = new(c.Middle.X, c.Middle.Y), End = new(c.End.X, c.End.Y) };
            if (!SketchDrawing.Arc(planar, out var centre, out double r, out _, out double sweep) ||
                Math.Sqrt(centre.Rho * centre.Rho + centre.Z * centre.Z) > RoundTripToleranceMm || r <= 0) return false;
            if (radius == 0) radius = r;
            if (Math.Abs(r - radius) > RoundTripToleranceMm) return false;
            int sign = Math.Sign(sweep);
            if (sign == 0 || direction != 0 && direction != sign) return false;
            direction = sign; sum += sweep;
        }
        if (Distance(curves[^1].End, curves[0].Start) > RoundTripToleranceMm ||
            Math.Abs(Math.Abs(sum) - 2 * Math.PI) * radius > RoundTripToleranceMm) return false;
        point = new(radius, z);
        return RingResidual(curves, point) <= RoundTripToleranceMm;
    }
    static double RingResidual(IEnumerable<Curve3> curves, SketchPoint ring)
        => curves.SelectMany(Points).Max(p => Math.Sqrt(Math.Pow(Math.Sqrt(p.X * p.X + p.Y * p.Y) - ring.Rho, 2) + Math.Pow(p.Z - ring.Z, 2)));
    static IEnumerable<Point3> Points(Curve3 c)
    {
        yield return c.Start;
        if (c.Kind == SegmentKind.Arc) yield return c.Middle;
        yield return c.End;
    }
    static bool Finite(Point3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    static double Distance(Point3 a, Point3 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
    static double Normalize(double angle) { angle %= 360; return angle < 0 ? angle + 360 : angle; }
    static double AngularDifference(double a, double b) { double d = Normalize(a - b); return d > 180 ? d - 360 : d; }
}
