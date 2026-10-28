using System;
using System.Collections.Generic;
using System.Linq;

namespace CaissonWallUi;

/// <summary>In-memory filleting of two adjacent sketch lines. All coordinates and diameters are millimetres.</summary>
public static class FilletGeometry
{
    // Drawing default only. This centreline diameter is not a bar diameter, inner bend
    // diameter or a statement of compliance with a reinforcement detailing standard.
    public const double DefaultDiameterMm = 80;
    // Numerical drawing tolerances, not construction tolerances or code requirements.
    const double PointToleranceMm = 0.000001;
    const double DirectionTolerance = 0.0000000001;

    /// <summary>
    /// Rounds every sharp line-line junction, including a closed path's seam. Existing
    /// arcs are retained exactly. The whole operation is atomic: no partial result is
    /// returned if any corner cannot fit or an existing curved join is not tangent.
    /// </summary>
    public static bool TryApplyDefaults(IReadOnlyList<SketchSegment> source, double diameterMm,
        out List<SketchSegment> result, out int addedCount, out string error)
    {
        result = new(); addedCount = 0; error = "";
        if (source == null || source.Any(s => s == null || !Finite(s.Start) || !Finite(s.Middle) || !Finite(s.End)))
        { error = "Path contains invalid coordinates."; return false; }
        if (!double.IsFinite(diameterMm) || diameterMm <= 0)
        { error = "Centerline bend diameter must be a finite value greater than zero."; return false; }
        for (int i = 0; i < source.Count; i++)
        {
            if (!TryTangent(source[i], false, out _, out _))
            { error = $"Segment {i + 1} is invalid. Automatic fillets cannot be applied."; return false; }
            if (i > 0 && Distance(source[i - 1].End, source[i].Start) > PointToleranceMm)
            { error = $"Segments {i} and {i + 1} are disconnected. Automatic fillets cannot be applied."; return false; }
        }
        bool closed = source.Count > 1 && Distance(source[^1].End, source[0].Start) <= PointToleranceMm;
        var work = source.Select(Clone).ToList();
        int count = 0;
        for (int i = 0; i + 1 < work.Count; i++)
        {
            if (!NeedsFillet(work[i], work[i + 1], out bool needed, out error)) return false;
            if (!needed) continue;
            if (!TryApply(work, i, i + 1, diameterMm, out var next, out error))
            { error = $"Default fillet not applied: {error}"; return false; }
            work = next; count++; i++; // Skip the inserted arc; inspect the next original junction.
        }
        if (closed)
        {
            if (!NeedsFillet(work[^1], work[0], out bool needed, out error)) return false;
            if (needed)
            {
                if (!TryApply(new[] { work[^1], work[0] }, 0, 1, diameterMm, out var seam, out error))
                { error = $"Default fillet at the closed joint not applied: {error}"; return false; }
                work[^1] = seam[0]; work[0] = seam[2]; work.Add(seam[1]); count++;
            }
        }
        result = work; addedCount = count; return true;
    }

    static bool NeedsFillet(SketchSegment incoming, SketchSegment outgoing, out bool needed, out string error)
    {
        needed = false; error = "";
        if (!TryTangent(incoming, true, out double ix, out double iy)
            || !TryTangent(outgoing, false, out double ox, out double oy))
        { error = "Path contains invalid lines or arcs."; return false; }
        double dot = Math.Clamp(ix * ox + iy * oy, -1, 1), cross = ix * oy - iy * ox;
        if (incoming.Kind != SegmentKind.Line || outgoing.Kind != SegmentKind.Line)
        {
            if (dot < .99999) { error = "An existing arc is not tangent to the adjacent segment. Adjust the shape."; return false; }
            return true;
        }
        if (Math.Abs(cross) <= DirectionTolerance)
        {
            if (dot < 0) { error = "The lines reverse direction. Automatic fillets cannot be applied."; return false; }
            return true;
        }
        needed = true; return true;
    }

    static bool TryTangent(SketchSegment segment, bool atEnd, out double dx, out double dy)
    {
        dx = dy = 0;
        if (segment.Kind == SegmentKind.Line)
        {
            dx = segment.End.Rho - segment.Start.Rho; dy = segment.End.Z - segment.Start.Z;
        }
        else if (segment.Kind == SegmentKind.Arc)
        {
            // Fit in a translated frame to avoid cancellation for large local coordinates.
            double bx = segment.Middle.Rho - segment.Start.Rho, by = segment.Middle.Z - segment.Start.Z;
            double cx = segment.End.Rho - segment.Start.Rho, cy = segment.End.Z - segment.Start.Z;
            double cross = bx * cy - by * cx, chord = Norm(cx, cy);
            if (chord <= PointToleranceMm || Math.Abs(cross) / chord <= PointToleranceMm) return false;
            double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double centerX = (b2 * cy - c2 * by) / (2 * cross), centerY = (bx * c2 - cx * b2) / (2 * cross);
            double rx = (atEnd ? cx : 0) - centerX, ry = (atEnd ? cy : 0) - centerY;
            double sign = Math.Sign(cross); dx = -sign * ry; dy = sign * rx;
        }
        else return false;
        double length = Norm(dx, dy);
        if (!double.IsFinite(length) || length <= PointToleranceMm) return false;
        dx /= length; dy /= length; return true;
    }

    /// <summary>
    /// Replaces a shared corner with its interior tangent arc. diameterMm is the
    /// centreline diameter D, so R=D/2. Selection order does not affect the result.
    /// The source is never modified. On failure, result is empty and error explains why.
    /// </summary>
    public static bool TryApply(IReadOnlyList<SketchSegment> source, int firstIndex, int secondIndex,
        double diameterMm, out List<SketchSegment> result, out string error)
    {
        result = new(); error = "";
        if (source == null || source.Count < 2) { error = "Draw at least two connected lines first."; return false; }
        if (!double.IsFinite(diameterMm) || diameterMm <= 0) { error = "Centerline bend diameter must be a finite value greater than zero."; return false; }
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= source.Count || secondIndex >= source.Count)
        { error = "Selected segment no longer exists. Select it again."; return false; }
        if (firstIndex == secondIndex) { error = "Select one line on each side of the corner."; return false; }
        int before = Math.Min(firstIndex, secondIndex), after = Math.Max(firstIndex, secondIndex);
        if (after != before + 1) { error = "The two lines must be adjacent in the path."; return false; }
        if (source.Any(s => s == null || !Finite(s.Start) || !Finite(s.Middle) || !Finite(s.End)))
        { error = "Path contains invalid coordinates."; return false; }
        var incoming = source[before]; var outgoing = source[after];
        if (incoming.Kind != SegmentKind.Line || outgoing.Kind != SegmentKind.Line)
        { error = "Both sides of the fillet must be straight lines."; return false; }
        if (Distance(incoming.End, outgoing.Start) > PointToleranceMm)
        { error = "The two lines do not share a corner."; return false; }

        var corner = incoming.End;
        double ix = corner.Rho - incoming.Start.Rho, iy = corner.Z - incoming.Start.Z;
        double ox = outgoing.End.Rho - corner.Rho, oy = outgoing.End.Z - corner.Z;
        double incomingLength = Norm(ix, iy), outgoingLength = Norm(ox, oy);
        if (!double.IsFinite(incomingLength) || !double.IsFinite(outgoingLength) || incomingLength <= PointToleranceMm || outgoingLength <= PointToleranceMm)
        { error = "Both lines at the corner must have nonzero length."; return false; }
        ix /= incomingLength; iy /= incomingLength; ox /= outgoingLength; oy /= outgoingLength;
        double cross = ix * oy - iy * ox;
        double dot = Math.Clamp(ix * ox + iy * oy, -1, 1);
        if (Math.Abs(cross) <= DirectionTolerance)
        { error = dot >= 0 ? "The lines are collinear. There is no corner to fillet." : "The lines reverse direction. An internal tangent arc cannot be created."; return false; }

        double turn = Math.Atan2(Math.Abs(cross), dot), sign = Math.Sign(cross);
        double radius = diameterMm / 2;
        // The path turns through turn; the tangency distance from the corner is R*tan(turn/2).
        double cut = radius * Math.Tan(turn / 2);
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(cut) || cut <= PointToleranceMm)
        { error = "This diameter and corner angle cannot produce a stable arc."; return false; }
        if (incomingLength - cut <= PointToleranceMm || outgoingLength - cut <= PointToleranceMm)
        { error = "Bend diameter is too large for the available line length. Reduce the diameter."; return false; }

        var tangentIn = new SketchPoint(corner.Rho - ix * cut, corner.Z - iy * cut);
        var tangentOut = new SketchPoint(corner.Rho + ox * cut, corner.Z + oy * cut);
        // The centre lies on the turning-side normal to the incoming tangent.
        double centerX = tangentIn.Rho - sign * iy * radius, centerY = tangentIn.Z + sign * ix * radius;
        double radialX = sign * iy * radius, radialY = -sign * ix * radius;
        double half = sign * turn / 2, cosine = Math.Cos(half), sine = Math.Sin(half);
        var middle = new SketchPoint(centerX + radialX * cosine - radialY * sine, centerY + radialX * sine + radialY * cosine);
        if (!Finite(tangentIn) || !Finite(tangentOut) || !Finite(middle) ||
            Distance(tangentIn, middle) <= PointToleranceMm || Distance(tangentOut, middle) <= PointToleranceMm)
        { error = "Arc coordinates are numerically unstable. Check the dimensions."; return false; }

        var copy = source.Select(Clone).ToList();
        copy[before].End = tangentIn.Copy();
        copy[after].Start = tangentOut.Copy();
        copy.Insert(after, new SketchSegment { Kind = SegmentKind.Arc, Start = tangentIn, Middle = middle, End = tangentOut });
        result = copy;
        return true;
    }

    static bool Finite(SketchPoint? point) => point != null && double.IsFinite(point.Rho) && double.IsFinite(point.Z);
    static double Norm(double x, double y)
    {
        double largest = Math.Max(Math.Abs(x), Math.Abs(y));
        if (largest == 0) return 0;
        return largest * Math.Sqrt(Math.Pow(x / largest, 2) + Math.Pow(y / largest, 2));
    }
    static double Distance(SketchPoint a, SketchPoint b) => Norm(a.Rho - b.Rho, a.Z - b.Z);
    static SketchSegment Clone(SketchSegment segment) => new()
    {
        Kind = segment.Kind, Start = segment.Start.Copy(), Middle = segment.Middle.Copy(), End = segment.End.Copy()
    };
}
