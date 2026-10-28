using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CaissonWallUi;

/// <summary>Reads the current native host on every opening. Never writes the Revit document.</summary>
public static class WallStartupLoader
{
    const double MatchToleranceMm = .001;

    public static ReviewDocument Load(UIApplication ui, Document doc, HostContext host, ReviewDocument librarySeed)
    {
        string key = doc.PathName + "#" + doc.ProjectInformation.UniqueId;
        if (host.DocumentKey != key || doc.GetElement(new ElementId(host.HostId))?.UniqueId != host.HostUniqueId)
            throw new InvalidOperationException("Current project does not match the wall host. Previous draft was not loaded.");

        var bars = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>()
            .Where(b => b.GetHostId().Value == host.HostId).OrderBy(b => b.Id.Value).ToArray();
        var seed = EmptySeed(host, librarySeed);
        if (bars.Length == 0) return seed;

        var metadata = bars.ToDictionary(b => b.Id.Value, WallModelEngine.Metadata);
        var owned = bars.Where(b => metadata[b.Id.Value] != null).ToArray();
        foreach (var bar in owned)
        {
            var meta = metadata[bar.Id.Value]!;
            if (meta["host_unique_id"]?.ToString() != host.HostUniqueId || string.IsNullOrWhiteSpace(meta["owner"]?.ToString()))
                throw new InvalidOperationException($"Host or group record for rebar {bar.Id.Value} is inconsistent. Automatic loading stopped.");
        }
        var owners = owned.Select(b => metadata[b.Id.Value]!["owner"]!.ToString()).Distinct().ToArray();
        if (owners.Length > 1) throw new InvalidOperationException("Multiple rebar parameter sets exist for this wall. Automatic selection is unavailable.");
        if (owners.Length == 0)
        {
            seed.Types.RemoveAll(t => t.Id.StartsWith("native-type/", StringComparison.Ordinal));
            return WallRebarImporter.Import(ui, seed).Document;
        }

        var storageKeys = owned.Select(b => StorageKey(metadata[b.Id.Value]!)).Distinct().ToArray();
        if (storageKeys.Length != 1) throw new InvalidOperationException("Current rebar references different parameter records. Automatic loading stopped.");
        var saved = WallModelEngine.StoredDesign(doc, metadata[owned[0].Id.Value]!)
            ?? throw new InvalidOperationException("Model rebar parameters cannot be read. Previous draft was not loaded.");
        ReviewDocument stored;
        try { stored = ReviewDocument.FromJson(saved.ToJsonString()); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException("Model rebar parameters are damaged. Previous draft was not loaded.", ex); }
        if (stored.ModelOwnerId != owners[0] || stored.Host.HostUniqueId != host.HostUniqueId)
            throw new InvalidOperationException("Model parameters contain inconsistent group or host identities. Automatic loading stopped.");
        stored.Host = host;
        var originalById = stored.Drafts.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var priorByElement = owned.ToDictionary(b => b.Id.Value, b =>
        {
            string id = metadata[b.Id.Value]!["draft"]?.ToString() ?? "";
            return originalById.GetValueOrDefault(id);
        });

        // A stored design preserves intended array rules only while every actual member agrees.
        if (owned.Length == bars.Length && Matches(doc, stored, owned, metadata))
        {
            DeactivateStaleImport(doc, stored);
            MergeLibrary(stored, librarySeed);
            Prepare(stored);
            return stored;
        }

        // Native edits take precedence over old saved positions. Keep the same owner so rebuilding
        // replaces this plugin's bars; the existing build guard still protects unrelated native bars.
        seed = EmptySeed(host, stored);
        seed.ModelOwnerId = stored.ModelOwnerId;
        seed.CheckSettings = stored.CheckSettings;
        MergeLibrary(seed, librarySeed);
        seed.Types.RemoveAll(t => t.Id.StartsWith("native-type/", StringComparison.Ordinal));
        var imported = WallRebarImporter.Import(ui, seed).Document;
        if (stored.CheckSettings.CoverConfirmed) imported.CheckSettings = stored.CheckSettings;
        RestoreMemberSettings(imported, priorByElement);
        Prepare(imported);
        return imported;
    }

    static string StorageKey(JsonObject meta)
    {
        if (meta["design_storage_id"] is { } id) return "storage/" + id.ToString();
        if (meta["design"] is { } legacy) return "legacy/" + legacy.ToJsonString();
        throw new InvalidOperationException("Rebar parameter record is missing. Previous draft was not loaded.");
    }

    static ReviewDocument EmptySeed(HostContext host, ReviewDocument library)
    {
        var result = new ReviewDocument { Host = host };
        MergeLibrary(result, library);
        ReviewPresets.Ensure(result);
        return result;
    }

    static void MergeLibrary(ReviewDocument target, ReviewDocument library)
    {
        // Existing IDs in the current model always win over a local draft's library copy.
        foreach (var type in library.Types)
            if (!target.Types.Any(t => t.Id == type.Id))
                target.Types.Add(new ReviewBarType { Id = type.Id, Name = type.Name, Grade = type.Grade, DiameterMm = type.DiameterMm });
        foreach (var shape in library.Shapes)
            if (!target.Shapes.Any(s => s.Id == shape.Id)) target.Shapes.Add(shape.Copy());
    }

    static void DeactivateStaleImport(Document doc, ReviewDocument design)
    {
        if (design.ImportSourceDocumentKey != doc.PathName + "#" + doc.ProjectInformation.UniqueId) return;
        // Historical IDs remain available for provenance. Only an obsolete active exclusion is
        // disabled; the current native model is still bound through its owner and host metadata.
        foreach (var draft in design.Drafts)
        {
            if (draft.SourceElementIds.Count != draft.SourceUniqueIds.Count) { design.ImportSourceDocumentKey = ""; return; }
            for (int i = 0; i < draft.SourceElementIds.Count; i++)
                if (doc.GetElement(new ElementId(draft.SourceElementIds[i])) is not Rebar bar
                    || bar.UniqueId != draft.SourceUniqueIds[i] || bar.GetHostId().Value != design.Host.HostId)
                { design.ImportSourceDocumentKey = ""; return; }
        }
    }

    static void Prepare(ReviewDocument design)
    {
        design.SessionId = Guid.NewGuid().ToString("N");
        design.CreatesRevitObjects = false;
        design.ExistingBars.Clear();
        design.ActiveTool = ReviewTool.Select;
        design.ActiveProfileId = null;
        design.PendingPoints.Clear();
        ReviewPresets.Ensure(design);
    }

    static bool Matches(Document doc, ReviewDocument design, Rebar[] bars, Dictionary<long, JsonObject?> metadata)
    {
        var expected = new Dictionary<(string Draft, int Member), (ReviewDraft Draft, GroupMember Member, ReviewBarType Type)>();
        foreach (var draft in design.Drafts)
        {
            var type = design.Types.FirstOrDefault(t => t.Id == draft.TypeId);
            if (type == null || !GroupGeometry.TryMembers(draft, out var members, out _)) return false;
            foreach (var member in members)
                if (!expected.TryAdd((draft.Id, member.Index), (draft, member, type)))
                    throw new InvalidOperationException("Model parameters contain duplicate rebar path IDs.");
        }
        if (bars.Length != expected.Count) return false;
        var visited = new HashSet<(string, int)>();
        foreach (var bar in bars)
        {
            var meta = metadata[bar.Id.Value]!;
            string draftId = meta["draft"]?.ToString() ?? "";
            int memberIndex = meta["member"]?.GetValue<int>() ?? -1;
            var id = (draftId, memberIndex);
            if (!visited.Add(id) || !expected.TryGetValue(id, out var item)) return false;
            if (bar.Quantity != 1 || bar.NumberOfBarPositions != 1 || !bar.DoesBarExistAtPosition(0)) return false;
            if (doc.GetElement(bar.GetTypeId()) is not RebarBarType type) return false;
            if (Math.Abs(WallHostReader.Mm(type.BarModelDiameter) - item.Type.DiameterMm) > MatchToleranceMm
                || Math.Abs(WallHostReader.Mm(type.BarNominalDiameter) - item.Type.DiameterMm) > MatchToleranceMm) return false;
            string grade = Regex.Match(type.Name, @"(?<![A-Z0-9])(HRB400E|HRB500E|HRB400|HRB500|HPB300)(?![A-Z0-9])").Value;
            if (grade.Length > 0 && grade != item.Type.Grade) return false;
            var actual = bar.GetTransformedCenterlineCurves(false, false, false, MultiplanarOption.IncludeAllMultiplanarCurves, 0).ToArray();
            var target = Targets(item.Draft, item.Member).Select(c => WallModelEngine.Native(c, design.Host)).ToArray();
            try
            {
                if (actual.Length == 0 || target.Length == 0
                    || Math.Abs(WallHostReader.Mm(actual.Sum(c => c.Length) - target.Sum(c => c.Length))) > MatchToleranceMm
                    || !OneWayMatch(actual, target) || !OneWayMatch(target, actual)) return false;
            }
            finally { foreach (var curve in actual) curve.Dispose(); foreach (var curve in target) curve.Dispose(); }
        }
        return visited.Count == expected.Count;
    }

    static bool OneWayMatch(IEnumerable<Curve> from, Curve[] to)
    {
        foreach (var curve in from)
        {
            int count = Math.Clamp((int)Math.Ceiling(WallHostReader.Mm(curve.Length) / 20), 4, 5000);
            for (int i = 0; i <= count; i++)
            {
                var point = curve.Evaluate((double)i / count, true);
                if (to.All(other => WallHostReader.Mm(other.Distance(point)) > MatchToleranceMm)) return false;
            }
        }
        return true;
    }

    static IEnumerable<Curve3> Targets(ReviewDraft draft, GroupMember member)
    {
        if (draft.Kind == DraftKind.Ring)
        {
            double r = draft.RingPoint.Rho + member.RhoOffsetMm, z = draft.RingPoint.Z + member.ZOffsetMm;
            yield return new Curve3 { Kind = SegmentKind.Arc, Start = new(r, 0, z), Middle = new(0, r, z), End = new(-r, 0, z) };
            yield return new Curve3 { Kind = SegmentKind.Arc, Start = new(-r, 0, z), Middle = new(0, -r, z), End = new(r, 0, z) };
        }
        else
        {
            double angle = member.AngleDeg * Math.PI / 180;
            Point3 Rotate(SketchPoint p) => new(p.Rho * Math.Cos(angle), p.Rho * Math.Sin(angle), p.Z);
            foreach (var segment in draft.Segments)
                yield return new Curve3 { Kind = segment.Kind, Start = Rotate(segment.Start), Middle = Rotate(segment.Middle), End = Rotate(segment.End) };
        }
    }

    static void RestoreMemberSettings(ReviewDocument imported, Dictionary<long, ReviewDraft?> oldByElement)
    {
        var restored = new List<ReviewDraft>();
        foreach (var draft in imported.Drafts)
        {
            var old = draft.SourceElementIds.Select(id => oldByElement.GetValueOrDefault(id)).ToArray();
            var settings = old.Select(p => (Allow: p?.AllowTopExtension ?? draft.AllowTopExtension,
                Mark: p?.SourceMark ?? draft.SourceMark, Name: p?.Name ?? draft.Name)).ToArray();
            if (settings.Length == 0 || settings.Distinct().Count() == 1)
            {
                if (settings.Length > 0) { draft.AllowTopExtension = settings[0].Allow; draft.SourceMark = settings[0].Mark; draft.Name = settings[0].Name; }
                restored.Add(draft); continue;
            }
            // Import can regroup equal actual paths from different prior groups. A permission or
            // source label must never spread to neighbouring bars merely because geometry matches.
            if (!GroupGeometry.TryMembers(draft, out var members, out var error) || members.Count != settings.Length)
                throw new InvalidOperationException("Loaded rebar member settings could not be matched: " + error);
            for (int i = 0; i < members.Count; i++)
            {
                var one = new ReviewDraft
                {
                    Kind = draft.Kind, TypeId = draft.TypeId, Name = settings[i].Name, SourceMark = settings[i].Mark,
                    AllowTopExtension = settings[i].Allow, ShapeTemplateId = draft.ShapeTemplateId, ShapeName = draft.ShapeName,
                    InsertionPoint = draft.InsertionPoint?.Copy(), ShapeSnapshot = ShapePlacement.CopySegments(draft.ShapeSnapshot),
                    Segments = ShapePlacement.CopySegments(draft.Segments),
                    RingPoint = new(draft.RingPoint.Rho + members[i].RhoOffsetMm, draft.RingPoint.Z + members[i].ZOffsetMm),
                    Group = new GroupParameters { Enabled = draft.Kind == DraftKind.Profile, Count = 1, StartAngleDeg = members[i].AngleDeg },
                    SourceElementIds = new() { draft.SourceElementIds[i] }, SourceUniqueIds = new() { draft.SourceUniqueIds[i] }
                };
                restored.Add(one);
            }
        }
        imported.Drafts = restored;
    }
}
