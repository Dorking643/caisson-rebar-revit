using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CaissonWallUi;

public sealed class ReviewCanvas : Control
{
    public bool IsPlan { get; }
    public ReviewDocument Document { get; set; } = new();
    public ReviewSelection Selection { get; set; } = new();
    public ValidationReport? Validation { get; set; }
    public ShapeTemplate? InsertTemplate { get; set; }
    public double? InsertDiameterMm { get; set; }
    public event Action<SketchPoint>? SketchClicked;
    public event Action<ReviewSelection>? SelectionChanged;
    public event Action? FinishRequested;
    public event Action? DeleteRequested;
    double _scale = 1, _centerX, _centerY;
    bool _hasView, _panning;
    Point _panStart;
    SketchPoint? _hover;
    readonly Color _ink = Color.FromArgb(42, 61, 77);
    readonly Color _blue = Color.FromArgb(0, 119, 163);
    readonly Color _muted = Color.FromArgb(112, 129, 145);

    public ReviewCanvas(bool plan)
    {
        IsPlan = plan;
        DoubleBuffered = true;
        BackColor = Color.White;
        Dock = DockStyle.Fill;
        MinimumSize = new Size(180, 220);
        TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        AccessibleName = plan ? "Plan sketch" : "Radial section sketch";
    }

    RectangleF Plot => IsPlan
        ? new(40, 42, Math.Max(10, ClientSize.Width - 62), Math.Max(10, ClientSize.Height - 82))
        : new(72, 88, Math.Max(10, ClientSize.Width - 96), Math.Max(10, ClientSize.Height - 130));
    PointF Screen(double x, double y) => new((float)(Plot.Left + Plot.Width / 2 + (x - _centerX) * _scale), (float)(Plot.Top + Plot.Height / 2 - (y - _centerY) * _scale));
    PointF Screen(SketchPoint p) => Screen(p.Rho, p.Z);
    SketchPoint World(Point p) => new(_centerX + (p.X - Plot.Left - Plot.Width / 2) / _scale, _centerY - (p.Y - Plot.Top - Plot.Height / 2) / _scale);
    static double Snap(double value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);
    SketchPoint SnapForInput(SketchPoint point)
    {
        if (IsPlan) return new(Snap(point.Rho), Snap(point.Z));
        var display = SectionCoordinates.ToDisplay(point, Document.Host.InnerRadiusMm);
        return SectionCoordinates.ToInternal(Snap(display.X), Snap(display.Y), Document.Host.InnerRadiusMm);
    }

    public void Fit(bool wholeHost = false)
    {
        var h = Document.Host;
        double outer = Math.Max(1, h.OuterRadiusMm), inner = Math.Max(0, h.InnerRadiusMm), height = Math.Max(1, h.HeightMm);
        if (IsPlan)
        {
            if (!wholeHost)
            {
                var radii = Document.Drafts.SelectMany(DrawingPoints).Select(p => Math.Abs(p.Rho)).ToArray();
                if (radii.Length > 0) outer = Math.Max(outer, radii.Max());
            }
            _centerX = _centerY = 0;
            _scale = Math.Min(Plot.Width, Plot.Height) / (outer * 2.35);
        }
        else
        {
            var points = Document.Drafts.SelectMany(DrawingPoints).Concat(Document.PendingPoints).ToArray();
            double left = inner, right = outer, top = 0, bottom = -height;
            if (!wholeHost && points.Length > 0)
            {
                left = Math.Min(inner, points.Min(p => p.Rho)); right = Math.Max(outer, points.Max(p => p.Rho));
                // Always include the display origin at the inner wall's top corner.
                top = Math.Max(0, points.Max(p => p.Z)); bottom = Math.Min(0, points.Min(p => p.Z));
            }
            // One scale for both directions: reserve pixels for the origin marker without distorting the section.
            _scale = Math.Clamp(Math.Min(Math.Max(10, Plot.Width - 72) / Math.Max(100, right - left), Math.Max(10, Plot.Height - 64) / Math.Max(250, top - bottom)), .00001, 100);
            // Anchor the content's upper-left bounds. A shallow sketch must not centre the wall top vertically.
            _centerX = left + (Plot.Width / 2 - 36) / _scale;
            _centerY = top - (Plot.Height / 2 - 32) / _scale;
        }
        _scale = Math.Clamp(_scale, .00001, 100);
        _hasView = true; Invalidate();
    }

    readonly record struct PreviewMember(int Index, double AngleDeg, double RhoOffsetMm, double ZOffsetMm);
    static readonly PreviewMember[] SingleMember = [new(0, 0, 0, 0)];
    static IReadOnlyList<PreviewMember> PreviewMembers(ReviewDraft draft)
    {
        if (!GroupGeometry.TryMembers(draft, out var members, out _) || members.Count == 0) return SingleMember;
        return members.Select(m => new PreviewMember(m.Index, m.AngleDeg, m.RhoOffsetMm, m.ZOffsetMm)).ToArray();
    }
    static SketchPoint RingPoint(ReviewDraft draft, PreviewMember member)
        => new(draft.RingPoint.Rho + member.RhoOffsetMm, draft.RingPoint.Z + member.ZOffsetMm);
    static IEnumerable<(PreviewMember Member, SketchPoint Point)> RingPoints(ReviewDraft draft, IReadOnlyList<PreviewMember> members, bool distinctRadii)
    {
        HashSet<double>? seen = distinctRadii ? new() : null;
        foreach (var member in members)
        {
            var point = RingPoint(draft, member);
            if (seen != null && !seen.Add(point.Rho)) continue;
            yield return (member, point);
        }
    }
    public static double? NominalDiameter(ReviewDocument document, ReviewDraft draft)
    {
        double? diameter = document.Types.FirstOrDefault(t => t.Id == draft.TypeId)?.DiameterMm;
        return diameter is > 0 && double.IsFinite(diameter.Value) ? diameter : null;
    }
    float? DiameterPixels(ReviewDraft draft)
        => NominalDiameter(Document, draft) is {} diameter ? (float)(diameter * _scale) : null;
    IEnumerable<SketchPoint> DrawingPoints(ReviewDraft draft)
    {
        double half = (NominalDiameter(Document, draft) ?? 0) / 2;
        foreach (var point in CenterlinePoints(draft))
        {
            if (half == 0) { yield return point; continue; }
            yield return new(point.Rho - half, point.Z - half);
            yield return new(point.Rho + half, point.Z + half);
        }
    }
    static IEnumerable<SketchPoint> CenterlinePoints(ReviewDraft draft)
    {
        if (draft.Kind == DraftKind.Ring)
        {
            foreach (var (_, point) in RingPoints(draft, PreviewMembers(draft), false)) yield return point;
            yield break;
        }
        foreach (var segment in draft.Segments)
            foreach (var point in SegmentBoundsPoints(segment)) yield return point;
        if (draft.InsertionPoint != null) yield return draft.InsertionPoint;
    }
    static IEnumerable<SketchPoint> SegmentBoundsPoints(SketchSegment segment)
    {
        // Keep the path sampling and include exact cardinal extrema for framing and plan projection.
        foreach (var point in SketchDrawing.Polyline(segment)) yield return point;
        if (segment.Kind != SegmentKind.Arc || !SketchDrawing.Arc(segment, out var center, out var radius, out var start, out var sweep)) yield break;
        foreach (double angle in new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
        {
            if (ArcContains(start, sweep, angle)) yield return new(center.Rho + radius * Math.Cos(angle), center.Z + radius * Math.Sin(angle));
        }
    }
    static double PositiveAngle(double angle) { angle %= 2 * Math.PI; return angle < 0 ? angle + 2 * Math.PI : angle; }
    static bool ArcContains(double start, double sweep, double angle)
        => (sweep >= 0 ? PositiveAngle(angle - start) : PositiveAngle(start - angle)) <= Math.Abs(sweep) + 1e-10;

    // Exact centreline distance in internal rho,z millimetres; independent of screen sampling.
    public static double CenterlineDistanceMm(SketchSegment segment, SketchPoint point)
    {
        static double LineDistance(SketchPoint p, SketchPoint a, SketchPoint b)
        {
            double dx = b.Rho - a.Rho, dy = b.Z - a.Z, square = dx * dx + dy * dy;
            double t = square <= 0 ? 0 : Math.Clamp(((p.Rho - a.Rho) * dx + (p.Z - a.Z) * dy) / square, 0, 1);
            return Math.Sqrt(Math.Pow(p.Rho - a.Rho - t * dx, 2) + Math.Pow(p.Z - a.Z - t * dy, 2));
        }
        if (segment.Kind != SegmentKind.Arc) return LineDistance(point, segment.Start, segment.End);
        if (!SketchDrawing.Arc(segment, out var center, out var radius, out var start, out var sweep))
            return Math.Min(LineDistance(point, segment.Start, segment.Middle), LineDistance(point, segment.Middle, segment.End));
        double distance = Math.Min(SketchDrawing.Distance(point, segment.Start), SketchDrawing.Distance(point, segment.End));
        double angle = Math.Atan2(point.Z - center.Z, point.Rho - center.Rho);
        if (ArcContains(start, sweep, angle)) distance = Math.Min(distance, Math.Abs(SketchDrawing.Distance(point, center) - radius));
        return distance;
    }
    void DrawSegment(Graphics g, Pen pen, SketchSegment segment)
    {
        if (segment.Kind == SegmentKind.Arc && SketchDrawing.Arc(segment, out var center, out var radius, out var start, out var sweep))
        {
            var c = Screen(center); float r = (float)(radius * _scale);
            if (r > 0 && float.IsFinite(r))
                g.DrawArc(pen, c.X - r, c.Y - r, 2 * r, 2 * r, (float)(-start * 180 / Math.PI), (float)(-sweep * 180 / Math.PI));
            return;
        }
        if (segment.Kind == SegmentKind.Arc)
            g.DrawLines(pen, [Screen(segment.Start), Screen(segment.Middle), Screen(segment.End)]);
        else g.DrawLine(pen, Screen(segment.Start), Screen(segment.End));
    }
    static (double Minimum, double Maximum)? ProfileRadii(ReviewDraft draft)
    {
        var points = draft.Segments.SelectMany(SegmentBoundsPoints).ToArray();
        return points.Length == 0 ? null : (points.Min(p => p.Rho), points.Max(p => p.Rho));
    }
    PointF PlanPoint(double radius, double angleDeg)
    {
        double angle = angleDeg * Math.PI / 180;
        return Screen(radius * Math.Cos(angle), radius * Math.Sin(angle));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_hasView) Fit();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var title = new Font(Font.FontFamily, 10, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, 8.5f);
        using var ink = new SolidBrush(_ink);
        using var muted = new SolidBrush(_muted);
        var selectedDraft = Document.Drafts.FirstOrDefault(d => d.Id == Selection.DraftId);
        var selectedDiameter = selectedDraft == null ? null : NominalDiameter(Document, selectedDraft);
        string viewTitle = IsPlan ? "Plan" : ClientSize.Width < 340 ? "Section" : "Radial Section";
        if (selectedDiameter is {} dia) viewTitle += $" · Φ{dia:0.###}";
        g.DrawString(viewTitle, title, ink, 14, 12);
        string subtitle = IsPlan ? "X–Y" : ClientSize.Width < 340 ? "mm" : "x →  y ↓  ·  mm";
        var subtitleSize = g.MeasureString(subtitle, small);
        g.DrawString(subtitle, small, muted, IsPlan ? ClientSize.Width - 55 : ClientSize.Width - subtitleSize.Width - 14, 15);
        g.DrawLine(Pens.Gainsboro, 12, 34, ClientSize.Width - 12, 34);
        var state = g.Save();
        g.SetClip(Plot);
        if (IsPlan) { DrawGrid(g, small); DrawHost(g, small); }
        else { DrawHost(g, small); DrawGrid(g, small); }
        DrawExisting(g);
        foreach (var draft in Document.Drafts) DrawDraft(g, draft);
        if (!IsPlan) { DrawPending(g); DrawInsertionPreview(g); DrawSectionAxes(g, small); }
        g.Restore(state);
        if (!IsPlan) DrawSectionRulers(g, small);
        if (!IsPlan && Document.Drafts.Any(d => d.Kind == DraftKind.Profile && d.Group?.Enabled == true))
            g.DrawString(Selection.MemberIndex>=0&&selectedDraft?.Kind==DraftKind.Profile?$"Bar {Selection.MemberIndex+1}": "Reference shape", small, muted, 14, 39);
        g.DrawString(ClientSize.Width < 360 ? "Wheel zoom · Middle pan" : "Click: select · Wheel: zoom · Middle: pan", small, muted, 13, ClientSize.Height - 25);
        if (!IsPlan && _hover != null)
        {
            var display = SectionCoordinates.ToDisplay(_hover, Document.Host.InnerRadiusMm);
            string coordinates = $"x {display.X:0.###}   y {display.Y:0.###} mm";
            SizeF size = g.MeasureString(coordinates, small);
            g.DrawString(coordinates, small, ink, Math.Max(12, ClientSize.Width - size.Width - 12), 39);
        }
        using var border = new Pen(Focused ? Color.FromArgb(81, 160, 194) : Color.FromArgb(221, 228, 233));
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    void DrawGrid(Graphics g, Font font)
    {
        if (!IsPlan) { DrawSectionGrid(g); return; }
        double target = 80 / _scale;
        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(.001, target))));
        double step = power * (target / power < 2 ? 2 : target / power < 5 ? 5 : 10);
        var lower = World(new Point((int)Plot.Left, (int)Plot.Bottom));
        var upper = World(new Point((int)Plot.Right, (int)Plot.Top));
        using var grid = new Pen(Color.FromArgb(239, 243, 246));
        using var label = new SolidBrush(Color.FromArgb(150, 163, 174));
        int count = 0;
        for (double x = Math.Ceiling(lower.Rho / step) * step; x <= upper.Rho && count++ < 100; x += step)
        {
            var p = Screen(x, 0); g.DrawLine(grid, p.X, Plot.Top, p.X, Plot.Bottom);
        }
        count = 0;
        for (double y = Math.Ceiling(lower.Z / step) * step; y <= upper.Z && count++ < 100; y += step)
        {
            var p = Screen(0, y); g.DrawLine(grid, Plot.Left, p.Y, Plot.Right, p.Y);
        }
    }

    double SectionGridStep()
    {
        double target = 86 / _scale;
        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(.001, target))));
        double ratio = target / power;
        return power * (ratio <= 1 ? 1 : ratio <= 2 ? 2 : ratio <= 5 ? 5 : 10);
    }

    (double Left, double Right, double Top, double Bottom) SectionDisplayBounds()
    {
        var topLeft = SectionCoordinates.ToDisplay(World(new Point((int)Plot.Left, (int)Plot.Top)), Document.Host.InnerRadiusMm);
        var bottomRight = SectionCoordinates.ToDisplay(World(new Point((int)Plot.Right, (int)Plot.Bottom)), Document.Host.InnerRadiusMm);
        return (topLeft.X, bottomRight.X, topLeft.Y, bottomRight.Y);
    }

    void DrawSectionGrid(Graphics g)
    {
        double step = SectionGridStep(); var bounds = SectionDisplayBounds();
        using var pen = new Pen(Color.FromArgb(215, 226, 234));
        int count = 0;
        for (double x = Math.Ceiling(bounds.Left / step) * step; x <= bounds.Right && count++ < 100; x += step)
        {
            var p = Screen(SectionCoordinates.ToInternal(x, 0, Document.Host.InnerRadiusMm));
            g.DrawLine(pen, p.X, Plot.Top, p.X, Plot.Bottom);
        }
        count = 0;
        for (double y = Math.Ceiling(bounds.Top / step) * step; y <= bounds.Bottom && count++ < 100; y += step)
        {
            var p = Screen(SectionCoordinates.ToInternal(0, y, Document.Host.InnerRadiusMm));
            g.DrawLine(pen, Plot.Left, p.Y, Plot.Right, p.Y);
        }
    }

    void DrawSectionRulers(Graphics g, Font font)
    {
        double step = SectionGridStep(); var bounds = SectionDisplayBounds();
        using var text = new SolidBrush(_muted); using var tick = new Pen(Color.FromArgb(157, 176, 190));
        float lastRight = float.NegativeInfinity; int count = 0;
        for (double x = Math.Ceiling(bounds.Left / step) * step; x <= bounds.Right && count++ < 100; x += step)
        {
            var p = Screen(SectionCoordinates.ToInternal(x, 0, Document.Host.InnerRadiusMm));
            string value = (x == 0 ? 0 : x).ToString("0.###"); var size = g.MeasureString(value, font);
            float left = p.X - size.Width / 2;
            if (left < lastRight + 9 || left < Plot.Left - 8 || left + size.Width > Plot.Right + 8) continue;
            g.DrawLine(tick, p.X, Plot.Top - 3, p.X, Plot.Top);
            g.DrawString(value, font, text, left, Plot.Top - size.Height - 5); lastRight = left + size.Width;
        }
        float lastBottom = float.NegativeInfinity; count = 0;
        for (double y = Math.Ceiling(bounds.Top / step) * step; y <= bounds.Bottom && count++ < 100; y += step)
        {
            var p = Screen(SectionCoordinates.ToInternal(0, y, Document.Host.InnerRadiusMm));
            string value = (y == 0 ? 0 : y).ToString("0.###"); var size = g.MeasureString(value, font);
            float top = p.Y - size.Height / 2;
            if (top < lastBottom + 6 || top < Plot.Top || top + size.Height > Plot.Bottom) continue;
            g.DrawLine(tick, Plot.Left - 3, p.Y, Plot.Left, p.Y);
            g.DrawString(value, font, text, Math.Max(3, Plot.Left - size.Width - 7), top); lastBottom = top + size.Height;
        }
    }

    void DrawSectionAxes(Graphics g, Font font)
    {
        var origin = Screen(SectionCoordinates.ToInternal(0, 0, Document.Host.InnerRadiusMm));
        using var axes = new Pen(Color.FromArgb(88, 119, 139), 1.3f) { EndCap = LineCap.ArrowAnchor };
        using var marker = new Pen(Color.FromArgb(48, 77, 98), 1.5f);
        using var text = new SolidBrush(Color.FromArgb(48, 77, 98));
        if (origin.Y >= Plot.Top && origin.Y <= Plot.Bottom && origin.X < Plot.Right - 24)
        {
            g.DrawLine(axes, Math.Max(Plot.Left, origin.X), origin.Y, Plot.Right - 11, origin.Y);
            g.DrawString("x", font, text, Plot.Right - 15, origin.Y - 21);
        }
        if (origin.X >= Plot.Left && origin.X <= Plot.Right && origin.Y < Plot.Bottom - 24)
        {
            g.DrawLine(axes, origin.X, Math.Max(Plot.Top, origin.Y), origin.X, Plot.Bottom - 11);
            g.DrawString("y", font, text, origin.X + 7, Plot.Bottom - 25);
        }
        if (!Plot.Contains(origin)) return;
        g.FillEllipse(Brushes.White, origin.X - 3.5f, origin.Y - 3.5f, 7, 7);
        g.DrawEllipse(marker, origin.X - 3.5f, origin.Y - 3.5f, 7, 7);
        g.DrawString("(0, 0)", font, text, origin.X + 8, origin.Y - 23);
    }

    void DrawHost(Graphics g, Font font)
    {
        var h = Document.Host;
        using var fill = new SolidBrush(Color.FromArgb(235, 241, 245));
        using var edge = new Pen(Color.FromArgb(132, 150, 164), 1.3f);
        using var axis = new Pen(Color.FromArgb(159, 176, 189), 1) { DashStyle = DashStyle.Dash };
        using var label = new SolidBrush(_muted);
        if (IsPlan)
        {
            var o = Screen(0, 0); float outer = (float)(h.OuterRadiusMm * _scale), inner = (float)(h.InnerRadiusMm * _scale);
            if (outer > 0 && outer < 1e7)
            {
                g.FillEllipse(fill, o.X - outer, o.Y - outer, 2 * outer, 2 * outer);
                if (inner > 0) g.FillEllipse(Brushes.White, o.X - inner, o.Y - inner, 2 * inner, 2 * inner);
                g.DrawEllipse(edge, o.X - outer, o.Y - outer, 2 * outer, 2 * outer);
                if (inner > 0) g.DrawEllipse(edge, o.X - inner, o.Y - inner, 2 * inner, 2 * inner);
                g.DrawLine(axis, o.X - outer - 10, o.Y, o.X + outer + 10, o.Y);
                g.DrawLine(axis, o.X, o.Y - outer - 10, o.X, o.Y + outer + 10);
                g.DrawString("O", font, label, o.X + 4, o.Y + 4);
                g.DrawString("X", font, label, o.X + outer + 2, o.Y + 3);
                g.DrawString("Y", font, label, o.X + 4, o.Y - outer - 15);
            }
        }
        else
        {
            if(h.SectionBoundary.Count>=3)
            {
                var polygon=h.SectionBoundary.Select(Screen).ToArray();g.FillPolygon(fill,polygon);g.DrawPolygon(edge,polygon);
            }
            else
            {
                var a = Screen(h.InnerRadiusMm, 0); var b = Screen(h.OuterRadiusMm, -h.HeightMm);
                var rect = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                g.FillRectangle(fill, rect); g.DrawRectangle(edge, rect.X, rect.Y, rect.Width, rect.Height);
            }
        }
        DrawOpenings(g,font);
    }

    void DrawOpenings(Graphics g,Font font)
    {
        var h=Document.Host;if(!h.OpeningGeometryVerified||h.HostOpenings.Count==0)return;
        using var pen=new Pen(Color.FromArgb(170,108,40),1.4f){DashStyle=DashStyle.Dash};
        using var label=new SolidBrush(pen.Color);
        double angle=0;
        var selected=Document.Drafts.FirstOrDefault(d=>d.Id==Selection.DraftId);
        if(selected?.Kind==DraftKind.Profile)
            angle=PreviewMembers(selected).FirstOrDefault(m=>m.Index==Math.Max(0,Selection.MemberIndex)).AngleDeg*Math.PI/180;
        foreach(var hole in h.HostOpenings)
        {
            double dx=hole.AxisEnd.X-hole.AxisStart.X,dy=hole.AxisEnd.Y-hole.AxisStart.Y;
            double length=Math.Sqrt(dx*dx+dy*dy);if(length<=0)continue;dx/=length;dy/=length;
            double r=hole.RadiusMm;
            if(IsPlan)
            {
                var polygon=new[]{Screen(hole.AxisStart.X-dy*r,hole.AxisStart.Y+dx*r),Screen(hole.AxisEnd.X-dy*r,hole.AxisEnd.Y+dx*r),
                    Screen(hole.AxisEnd.X+dy*r,hole.AxisEnd.Y-dx*r),Screen(hole.AxisStart.X+dy*r,hole.AxisStart.Y-dx*r)};
                g.DrawPolygon(pen,polygon);g.DrawString(hole.Id,font,label,polygon[0]);continue;
            }
            // Intersect the selected radial plane with the finite horizontal cylindrical void.
            double cosine=dx*Math.Cos(angle)+dy*Math.Sin(angle),sine=dx*Math.Sin(angle)-dy*Math.Cos(angle);
            if(Math.Abs(cosine)<1e-10)continue;
            double t0=hole.AxisStart.X*dx+hole.AxisStart.Y*dy,t1=hole.AxisEnd.X*dx+hole.AxisEnd.Y*dy;
            double low=Math.Max(0,Math.Min(t0/cosine,t1/cosine)),high=Math.Max(t0/cosine,t1/cosine);
            if(Math.Abs(sine)>1e-10)high=Math.Min(high,r/Math.Abs(sine));
            if(high<=low||h.SectionBoundary.Count<3)continue;
            var points=new List<PointF>();
            for(int i=0;i<=64;i++){double rho=low+(high-low)*i/64;points.Add(Screen(rho,hole.AxisStart.Z+Math.Sqrt(Math.Max(0,r*r-rho*rho*sine*sine))));}
            for(int i=64;i>=0;i--){double rho=low+(high-low)*i/64;points.Add(Screen(rho,hole.AxisStart.Z-Math.Sqrt(Math.Max(0,r*r-rho*rho*sine*sine))));}
            var state=g.Save();using var clip=new GraphicsPath();clip.AddPolygon(h.SectionBoundary.Select(Screen).ToArray());
            g.SetClip(clip,CombineMode.Intersect);g.FillPolygon(Brushes.White,points.ToArray());g.DrawPolygon(pen,points.ToArray());g.Restore(state);
            var at=Screen((low+high)/2,hole.AxisStart.Z);g.DrawString(hole.Id,font,label,at.X+3,at.Y+3);
        }
    }

    void DrawDraft(Graphics g, ReviewDraft draft)
    {
        bool selected = draft.Id == Selection.DraftId;
        var members = PreviewMembers(draft);
        var diameterPx = DiameterPixels(draft);
        float width = diameterPx ?? 1.4f;
        bool bad=Validation?.HasIssue(draft.Id)==true;
        using var pen = new Pen(bad?Color.FromArgb(210,48,48):selected ? _blue : Color.FromArgb(119, 157, 178), width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var copyPen = new Pen(selected ? Color.FromArgb(165, _blue) : Color.FromArgb(160, 119, 157, 178), width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var primaryFill = new SolidBrush(pen.Color);
        using var copyFill = new SolidBrush(copyPen.Color);
        if (draft.Kind == DraftKind.Ring)
        {
            var rings=RingPoints(draft,members,false).ToList();
            if(IsPlan)rings=rings.GroupBy(x=>x.Point.Rho).Select(group=>group.OrderByDescending(x=>Validation?.HasIssue(draft.Id,x.Member.Index)==true).First()).ToList();
            foreach (var (member, point) in rings)
            {
                bool memberBad=Validation?.HasIssue(draft.Id,member.Index)==true;
                using var memberPen=new Pen(memberBad?Color.FromArgb(210,48,48):(member.Index==0?selected?_blue:Color.FromArgb(119,157,178):copyPen.Color),width){StartCap=LineCap.Round,EndCap=LineCap.Round};
                using var memberFill=new SolidBrush(memberPen.Color);
                if (IsPlan)
                {
                    var o = Screen(0, 0); float r = (float)(point.Rho * _scale);
                    if (r > 0 && r < 1e7) g.DrawEllipse(memberPen, o.X - r, o.Y - r, r * 2, r * 2);
                }
                else
                {
                    var p = Screen(point);
                    if (diameterPx is {} diameter)
                        g.FillEllipse(memberFill, p.X - diameter / 2, p.Y - diameter / 2, diameter, diameter);
                    else
                    {
                        // A missing type has only a centre mark, never an invented physical section size.
                        var marker = memberPen;
                        g.DrawLine(marker, p.X - 3, p.Y, p.X + 3, p.Y);
                        g.DrawLine(marker, p.X, p.Y - 3, p.X, p.Y + 3);
                    }
                }
            }
            return;
        }
        if (IsPlan)
        {
            var radii = ProfileRadii(draft);
            if (radii is {} bounds)
            {
                foreach (var member in members)
                {
                    var a = PlanPoint(bounds.Minimum, member.AngleDeg); var b = PlanPoint(bounds.Maximum, member.AngleDeg);
                    using var memberPen=new Pen(Validation?.HasIssue(draft.Id,member.Index)==true?Color.FromArgb(210,48,48):member.Index==0?(selected?_blue:Color.FromArgb(119,157,178)):copyPen.Color,width){StartCap=LineCap.Round,EndCap=LineCap.Round};
                    using var memberFill=new SolidBrush(memberPen.Color);
                    if (Math.Abs(bounds.Maximum - bounds.Minimum) < 1e-9)
                    {
                        if (diameterPx is {} diameter)
                            g.FillEllipse(memberFill, a.X - diameter / 2, a.Y - diameter / 2, diameter, diameter);
                        else
                        {
                            g.DrawLine(memberPen, a.X - 3, a.Y, a.X + 3, a.Y);
                            g.DrawLine(memberPen, a.X, a.Y - 3, a.X, a.Y + 3);
                        }
                    }
                    else g.DrawLine(memberPen, a, b);
                }
            }
            if (selected && draft.InsertionPoint != null) BaseMarker(g, PlanPoint(draft.InsertionPoint.Rho, members.FirstOrDefault(m=>m.Index==Selection.MemberIndex).AngleDeg));
            return;
        }
        foreach (var segment in draft.Segments) DrawSegment(g, pen, segment);
        if (selected && draft.InsertionPoint != null) BaseMarker(g, Screen(draft.InsertionPoint));
    }

    void DrawExisting(Graphics g)
    {
        double angle=0;
        var source=Document.Drafts.FirstOrDefault(d=>d.Id==Selection.DraftId);
        if(source?.Kind==DraftKind.Profile)
        {
            var members=PreviewMembers(source);var member=members.FirstOrDefault(m=>m.Index==Selection.MemberIndex);
            angle=(Selection.MemberIndex>=0?member.AngleDeg:members[0].AngleDeg)*Math.PI/180;
        }
        double cosine=Math.Cos(angle),sine=Math.Sin(angle);
        var related=Validation?.Issues.Where(i=>i.ExistingElementId.HasValue).Select(i=>i.ExistingElementId!.Value).ToHashSet()??new HashSet<long>();
        foreach(var bar in Document.ExistingBars)
        {
            if(!double.IsFinite(bar.DiameterMm)||bar.DiameterMm<=0)continue;
            using var pen=new Pen(related.Contains(bar.ElementId)?Color.FromArgb(180,210,48,48):Color.FromArgb(105,125,136,144),(float)(bar.DiameterMm*_scale)){StartCap=LineCap.Round,EndCap=LineCap.Round};
            foreach(var curve in bar.Curves)
            {
                var points=DisplayPolyline(curve);
                for(int i=1;i<points.Count;i++)
                {
                    var a=points[i-1];var b=points[i];
                    if(IsPlan){g.DrawLine(pen,Screen(a.X,a.Y),Screen(b.X,b.Y));continue;}
                    double ar=a.X*cosine+a.Y*sine,br=b.X*cosine+b.Y*sine;
                    double at=-a.X*sine+a.Y*cosine,bt=-b.X*sine+b.Y*cosine;
                    // Display only the steel intersecting this radial cut, never a radial flattening of the whole model.
                    double half=bar.DiameterMm/2,lo=0,hi=1,delta=bt-at;
                    if(Math.Abs(delta)<1e-12){if(Math.Abs(at)>half)continue;}
                    else{double t0=(-half-at)/delta,t1=(half-at)/delta;lo=Math.Max(0,Math.Min(t0,t1));hi=Math.Min(1,Math.Max(t0,t1));if(hi<lo)continue;}
                    double r0=ar+(br-ar)*lo,r1=ar+(br-ar)*hi;if(Math.Max(r0,r1)<0)continue;
                    g.DrawLine(pen,Screen(r0,a.Z+(b.Z-a.Z)*lo),Screen(r1,a.Z+(b.Z-a.Z)*hi));
                }
            }
        }
    }
    List<Point3> DisplayPolyline(Curve3 curve)
    {
        if(curve.Kind!=SegmentKind.Arc)return [curve.Start,curve.End];
        static Point3 Sub(Point3 a,Point3 b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        static Point3 Cross(Point3 a,Point3 b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
        static double Dot(Point3 a,Point3 b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
        static Point3 Scale(Point3 a,double s)=>new(a.X*s,a.Y*s,a.Z*s);
        var u=Sub(curve.Middle,curve.Start);var v=Sub(curve.End,curve.Start);var normal=Cross(u,v);double nn=Dot(normal,normal);
        if(nn<1e-16)return [curve.Start,curve.Middle,curve.End];
        var t1=Scale(Cross(normal,u),Dot(v,v));var t2=Scale(Cross(v,normal),Dot(u,u));
        var center=new Point3(curve.Start.X+(t1.X+t2.X)/(2*nn),curve.Start.Y+(t1.Y+t2.Y)/(2*nn),curve.Start.Z+(t1.Z+t2.Z)/(2*nn));
        var basis=Sub(curve.Start,center);double radius=Math.Sqrt(Dot(basis,basis));if(radius<=0)return [curve.Start,curve.Middle,curve.End];
        var x=Scale(basis,1/radius);var n=Scale(normal,1/Math.Sqrt(nn));var y=Cross(n,x);
        double end=PositiveAngle(Math.Atan2(Dot(Sub(curve.End,center),y),Dot(Sub(curve.End,center),x)));
        double mid=PositiveAngle(Math.Atan2(Dot(Sub(curve.Middle,center),y),Dot(Sub(curve.Middle,center),x)));
        double sweep=mid<=end?end:end-2*Math.PI;
        int count=Math.Clamp((int)Math.Ceiling(Math.Abs(sweep)*Math.Sqrt(Math.Max(1,radius*_scale/.5))),8,1000);
        return Enumerable.Range(0,count+1).Select(i=>{double a=sweep*i/count;return new Point3(center.X+radius*(x.X*Math.Cos(a)+y.X*Math.Sin(a)),center.Y+radius*(x.Y*Math.Cos(a)+y.Y*Math.Sin(a)),center.Z+radius*(x.Z*Math.Cos(a)+y.Z*Math.Sin(a)));}).ToList();
    }

    void DrawInsertionPreview(Graphics g)
    {
        if (Document.ActiveTool != ReviewTool.InsertShape || _hover == null || InsertTemplate == null) return;
        float width = InsertDiameterMm is > 0 && double.IsFinite(InsertDiameterMm.Value) ? (float)(InsertDiameterMm.Value * _scale) : 1.4f;
        using var pen = new Pen(Color.FromArgb(145, _blue), width) { DashStyle = DashStyle.Dash, StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        foreach (var segment in ShapePlacement.CopySegments(InsertTemplate.Segments, _hover.Rho, _hover.Z))
            DrawSegment(g, pen, segment);
        BaseMarker(g, Screen(_hover), true);
    }

    void BaseMarker(Graphics g, PointF p, bool preview = false)
    {
        using var pen = new Pen(preview ? Color.FromArgb(175, _blue) : _blue, 1.7f);
        using var text = new SolidBrush(preview ? Color.FromArgb(175, _blue) : _blue);
        using var font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        g.DrawLine(pen, p.X - 7, p.Y, p.X + 7, p.Y); g.DrawLine(pen, p.X, p.Y - 7, p.X, p.Y + 7);
        g.FillEllipse(Brushes.White, p.X - 3.2f, p.Y - 3.2f, 6.4f, 6.4f); g.DrawEllipse(pen, p.X - 3.2f, p.Y - 3.2f, 6.4f, 6.4f);
        bool nearSectionOrigin = !IsPlan && PixelDistance(Point.Round(p), Screen(Document.Host.InnerRadiusMm, 0), Screen(Document.Host.InnerRadiusMm, 0)) < 30;
        g.DrawString("B", font, text, p.X + 9, nearSectionOrigin ? p.Y + 8 : p.Y - 19);
    }

    void DrawPending(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(198, 138, 41), 1.8f) { DashStyle = DashStyle.Dash };
        var points = Document.PendingPoints.Select(Screen).ToList();
        if (_hover != null && Document.ActiveTool is ReviewTool.Arc or ReviewTool.Polyline && points.Count > 0) points.Add(Screen(_hover));
        if (points.Count > 1) g.DrawLines(pen, points.ToArray());
        foreach (var p in Document.PendingPoints) Node(g, Screen(p), false);
    }

    void Node(Graphics g, PointF p, bool active, bool ring = false)
    {
        float r = active ? 5 : ring ? 4.5f : 3.5f;
        using var fill = new SolidBrush(active || ring ? _blue : Color.White);
        using var edge = new Pen(_blue, 1.5f);
        g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2); g.DrawEllipse(edge, p.X - r, p.Y - r, r * 2, r * 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.Button == MouseButtons.Middle) { _panning = true; _panStart = e.Location; Capture = true; Cursor = Cursors.SizeAll; return; }
        if (e.Button == MouseButtons.Right) { FinishRequested?.Invoke(); return; }
        if (e.Button != MouseButtons.Left || !Plot.Contains(e.Location)) return;
        if (IsPlan || Document.ActiveTool == ReviewTool.Select) { SelectionChanged?.Invoke(Hit(e.Location)); return; }
        var p = World(e.Location);
        SketchClicked?.Invoke(SnapForInput(p));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning) { _centerX -= (e.X - _panStart.X) / _scale; _centerY += (e.Y - _panStart.Y) / _scale; _panStart = e.Location; }
        var p = World(e.Location); _hover = Plot.Contains(e.Location) ? SnapForInput(p) : null;
        Cursor = _panning ? Cursors.SizeAll : !IsPlan && Document.ActiveTool != ReviewTool.Select ? Cursors.Cross : Cursors.Default;
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Middle) { _panning = false; Capture = false; Cursor = Cursors.Default; } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_panning) _hover = null; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e); var before = World(e.Location);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.18 : 1 / 1.18), .00001, 100);
        var after = World(e.Location); _centerX += before.Rho - after.Rho; _centerY += before.Z - after.Z; Invalidate();
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Delete or Keys.Escape or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Escape) { FinishRequested?.Invoke(); e.Handled = true; }
        if (e.KeyCode == Keys.Delete) { DeleteRequested?.Invoke(); e.Handled = true; }
    }

    ReviewSelection Hit(Point mouse)
    {
        double best = 11; ReviewSelection answer = new();
        foreach (var draft in Document.Drafts.AsEnumerable().Reverse())
        {
            var members = PreviewMembers(draft);
            double halfWidth = (DiameterPixels(draft) ?? 0) / 2;
            if (IsPlan)
            {
                var p = World(mouse); double distance = double.PositiveInfinity;
                int memberIndex=-1;
                if (draft.Kind == DraftKind.Ring)
                {
                    double mouseRadius = Math.Sqrt(p.Rho * p.Rho + p.Z * p.Z);
                    foreach (var (member, point) in RingPoints(draft, members, false).OrderByDescending(x=>Validation?.HasIssue(draft.Id,x.Member.Index)==true))
                    {
                        double candidate=Math.Abs(mouseRadius-point.Rho)*_scale;
                        if(candidate<distance-1e-7){distance=candidate;memberIndex=member.Index;}
                    }
                }
                else
                {
                    var radii = ProfileRadii(draft);
                    if (radii is {} bounds)
                        foreach (var member in members)
                        {
                            double candidate=PixelDistance(mouse,PlanPoint(bounds.Minimum,member.AngleDeg),PlanPoint(bounds.Maximum,member.AngleDeg));
                            if(candidate<distance){distance=candidate;memberIndex=member.Index;}
                        }
                    if (draft.InsertionPoint != null && members.Count > 0)
                    {
                        var b = PlanPoint(draft.InsertionPoint.Rho, members[0].AngleDeg);
                        double candidate=Math.Sqrt(Math.Pow(mouse.X-b.X,2)+Math.Pow(mouse.Y-b.Y,2));
                        if(candidate<distance){distance=candidate;memberIndex=members[0].Index;}
                    }
                }
                distance = Math.Max(0, distance - halfWidth);
                if (distance < best) { best = distance; answer = new() { DraftId = draft.Id,MemberIndex=memberIndex, PointRole = draft.Kind == DraftKind.Ring ? PointRole.Ring : PointRole.None }; }
                continue;
            }
            if (draft.Kind == DraftKind.Ring)
            {
                foreach (var (member, point) in RingPoints(draft, members, false))
                {
                    var p = Screen(point); double d = Math.Max(0, Math.Sqrt(Math.Pow(mouse.X - p.X, 2) + Math.Pow(mouse.Y - p.Y, 2)) - halfWidth);
                    if (d < best) { best = d; answer = new() { DraftId = draft.Id,MemberIndex=member.Index, PointRole = PointRole.Ring }; }
                }
                continue;
            }
            if (draft.InsertionPoint != null)
            {
                var b = Screen(draft.InsertionPoint);
                double distance = Math.Sqrt(Math.Pow(mouse.X - b.X, 2) + Math.Pow(mouse.Y - b.Y, 2));
                if (distance < best) { best = distance; answer = new() { DraftId = draft.Id,MemberIndex=draft.Id==Selection.DraftId?Selection.MemberIndex:FirstProblemMember(draft) }; }
            }
        }
        if (answer.DraftId != null || IsPlan) return answer;
        best = 8;
        var sectionPoint = World(mouse);
        foreach (var draft in Document.Drafts.Where(d => d.Kind == DraftKind.Profile).Reverse())
        for (int i = 0; i < draft.Segments.Count; i++)
        {
            double halfWidth = (DiameterPixels(draft) ?? 0) / 2;
            double d = Math.Max(0, CenterlineDistanceMm(draft.Segments[i], sectionPoint) * _scale - halfWidth);
            if (d < best) { best = d; answer = new() { DraftId = draft.Id,MemberIndex=draft.Id==Selection.DraftId?Selection.MemberIndex:FirstProblemMember(draft) }; }
        }
        return answer;
    }
    int FirstProblemMember(ReviewDraft draft)
    {
        var issue=Validation?.ForDraft(draft.Id).FirstOrDefault();
        return issue==null?0:issue.DraftId==draft.Id?issue.MemberIndex:issue.OtherMemberIndex;
    }
    static double PixelDistance(Point p, PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, square = dx * dx + dy * dy;
        double t = square < .0001 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / square, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }
}
