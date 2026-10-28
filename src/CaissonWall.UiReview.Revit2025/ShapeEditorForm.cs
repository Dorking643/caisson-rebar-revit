using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CaissonWallUi;

public sealed class ShapeEditorForm : Form
{
    readonly string _templateId;
    List<SketchSegment> _segments;
    List<SketchSegment>? _automaticSource;
    double? _automaticDiameter;
    List<SketchPoint> _pending = new();
    bool _hasBase;
    ShapeTool _tool = ShapeTool.Select;
    ShapePick _selection = new();
    int _filletFirst = -1, _filletSecond = -1;
    readonly Stack<ShapeEditorState> _undo = new();
    readonly ShapeCanvas _canvas = new();
    readonly TextBox _name = new() { Width = 220 };
    readonly ComboBox _pointRole = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92 };
    readonly NumericUpDown _u = Number(), _v = Number(), _length = Number(.001m, 10000000);
    readonly NumericUpDown _diameter = Number(.001m, 10000000);
    readonly FlowLayoutPanel _filletRow = Flow();
    readonly Label _filletLabel = LabelFor("Default fillet Ø mm");
    readonly FlowLayoutPanel _coordinateRow = Flow(), _lengthRow = Flow(), _arcReadoutRow = Flow();
    readonly Label _selectionTitle = new() { AutoSize = true, Padding = new Padding(4, 7, 10, 0), Text = "Select a point or segment" };
    readonly Label _radius = new() { AutoSize = true, Padding = new Padding(10, 7, 0, 0), ForeColor = Color.FromArgb(109, 127, 142) };
    readonly Label _arcReadout = new() { AutoSize = true, Padding = new Padding(4, 7, 0, 0), ForeColor = Color.FromArgb(109, 127, 142) };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(5, 9, 5, 0), ForeColor = Color.FromArgb(93, 116, 132) };
    readonly Button _applyPoint = new() { Text = "Apply", AutoSize = true };
    readonly Button _applyLength = new() { Text = "Apply Length", AutoSize = true };
    readonly Button _applyFillet = new() { Text = "Apply Fillet", AutoSize = true };
    readonly Button _save = new() { Text = "Save Shape", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    readonly Dictionary<ShapeTool, Button> _toolButtons = new();
    bool _refreshing;
    public ShapeTemplate? ResultTemplate { get; private set; }

    public ShapeEditorForm(ShapeTemplate? template = null)
    {
        _templateId = string.IsNullOrWhiteSpace(template?.Id) ? Guid.NewGuid().ToString("N") : template!.Id;
        _segments = Clone(template?.Segments ?? new());
        _hasBase = template != null && _segments.Count > 0;
        Text = "Shape Editor · u–v";
        Name = "CaissonWallShapeEditor";
        Font = new Font("Segoe UI", 9);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1100, 790);
        MinimumSize = new Size(820, 620);
        BackColor = Color.FromArgb(246, 248, 250);
        KeyPreview = true;
        _name.Text = string.IsNullOrWhiteSpace(template?.Name) ? "New Shape" : ReviewPresets.DisplayName(template!);
        _diameter.Value = (decimal)FilletGeometry.DefaultDiameterMm;
        BuildInterface();
        BindEvents();
        RoundDefaults(out string initialMessage);
        RefreshEditor(initialMessage.Length > 0 ? initialMessage : null);
        Shown += (_, _) => { ConstrainToScreen(); _canvas.Fit(); };
    }

    static NumericUpDown Number(decimal min = -10000000, decimal max = 10000000) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = 3, Increment = 1, Width = 112
    };
    static Label LabelFor(string text) => new() { AutoSize = true, Text = text, Padding = new Padding(4, 7, 2, 0) };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(4), Margin = new Padding(0) };
    static Button MakeButton(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, Margin = new Padding(3), Padding = new Padding(6, 1, 6, 1) };
        button.Click += action; return button;
    }
    static void Set(NumericUpDown control, double value) => control.Value = Math.Clamp((decimal)(double.IsFinite(value) ? value : 0), control.Minimum, control.Maximum);

    void BuildInterface()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
        var nameRow = Flow(); nameRow.Controls.Add(LabelFor("Name")); nameRow.Controls.Add(_name);
        nameRow.Controls.Add(new Label { Text = "Local coordinates · mm", AutoSize = true, Padding = new Padding(14, 7, 0, 0), ForeColor = Color.FromArgb(107, 127, 142) }); root.Controls.Add(nameRow, 0, 0);
        var tools = Flow();
        AddTool(tools, "Select", ShapeTool.Select); AddTool(tools, "Polyline", ShapeTool.Line); AddTool(tools, "Fillet", ShapeTool.Fillet); AddTool(tools, "Set Origin", ShapeTool.Base);
        tools.Controls.Add(MakeButton("Finish", (_, _) => Finish())); tools.Controls.Add(MakeButton("Undo", (_, _) => Undo())); tools.Controls.Add(MakeButton("Fit", (_, _) => _canvas.Fit()));
        root.Controls.Add(tools, 0, 1); root.Controls.Add(_canvas, 0, 2);
        var inspector = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 8, 0, 4), BackColor = Color.White };
        inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize)); inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize)); inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize)); inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _coordinateRow.Controls.Add(_selectionTitle); _coordinateRow.Controls.Add(_pointRole); _coordinateRow.Controls.Add(LabelFor("u")); _coordinateRow.Controls.Add(_u); _coordinateRow.Controls.Add(LabelFor("v")); _coordinateRow.Controls.Add(_v); _coordinateRow.Controls.Add(_applyPoint);
        _lengthRow.Controls.Add(LabelFor("Line length")); _lengthRow.Controls.Add(_length); _lengthRow.Controls.Add(_applyLength); _lengthRow.Controls.Add(_radius);
        _arcReadoutRow.Controls.Add(_arcReadout);
        _filletRow.Controls.Add(_filletLabel); _filletRow.Controls.Add(_diameter); _filletRow.Controls.Add(_applyFillet);
        inspector.Controls.Add(_coordinateRow, 0, 0); inspector.Controls.Add(_lengthRow, 0, 1); inspector.Controls.Add(_arcReadoutRow, 0, 2); inspector.Controls.Add(_filletRow, 0, 3); root.Controls.Add(inspector, 0, 3);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1 };
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bottom.Controls.Add(_status, 0, 0);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Padding = new Padding(4), Margin = new Padding(0)
        };
        actions.Controls.Add(_save); var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 2, 10, 2) }; actions.Controls.Add(cancel); bottom.Controls.Add(actions, 1, 0); root.Controls.Add(bottom, 0, 4);
        CancelButton = cancel;
        var tip = new ToolTip(); tip.SetToolTip(_u, "Horizontal coordinate from origin B in mm. Step: 1 mm; decimals allowed."); tip.SetToolTip(_v, "Vertical coordinate from origin B in mm. Positive upward.");
        tip.SetToolTip(_applyLength, "Keep the start and direction; move the end and any shared endpoint.");
        tip.SetToolTip(_diameter, "Centerline arc diameter in mm; not the bar diameter or inside bend diameter.");
    }

    void AddTool(FlowLayoutPanel container, string text, ShapeTool tool)
    {
        var button = MakeButton(text, (_, _) => SetTool(tool)); _toolButtons.Add(tool, button); container.Controls.Add(button);
    }
    void BindEvents()
    {
        _canvas.PointClicked += ClickPoint; _canvas.SelectionChanged += pick => { _selection = pick; RefreshEditor(); };
        _canvas.FilletLinePicked += PickFilletLine;
        _canvas.FinishRequested += Finish;
        _pointRole.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshing) return;
            string? text = _pointRole.SelectedItem?.ToString();
            _selection.Role = text == "Start" ? PointRole.Start : PointRole.End;
            RefreshEditor();
        };
        _applyPoint.Click += (_, _) => ApplyPoint(); _applyLength.Click += (_, _) => ApplyLength(); _save.Click += (_, _) => SaveShape();
        _applyFillet.Click += (_, _) => ApplyFillet();
        _diameter.ValueChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (_tool != ShapeTool.Fillet && _automaticSource != null)
            { RoundDefaults(out string message); RefreshEditor(message.Length > 0 ? message : null); }
            else RefreshEditor();
        };
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Z && !_name.ContainsFocus) { Undo(); e.SuppressKeyPress = true; }
        };
    }

    // Escape is a form-wide drawing command, including while an input box has focus.
    // Consume it before WinForms dispatches it to CancelButton; the visible Cancel
    // button remains the explicit way to discard and close the shape editor.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Escape)
        {
            EndDrawingWithoutPending();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void EndDrawingWithoutPending()
    {
        if (_tool != ShapeTool.Select || _pending.Count > 0) Checkpoint(true);
        _pending.Clear();
        _filletFirst = _filletSecond = -1;
        _tool = ShapeTool.Select;
        RoundDefaults(out string message);
        RefreshEditor(message.Length > 0 ? message : "Drawing finished");
    }

    SketchSegment? SelectedSegment => _selection.Segment >= 0 && _selection.Segment < _segments.Count ? _segments[_selection.Segment] : null;
    SketchPoint? SelectedPoint => SelectedSegment is not { } s ? null : _selection.Role switch { PointRole.Start => s.Start, PointRole.Middle when s.Kind == SegmentKind.Arc => s.Middle, PointRole.End => s.End, _ => null };
    void RefreshEditor(string? message = null)
    {
        _refreshing = true;
        foreach (var pair in _toolButtons)
        {
            pair.Value.BackColor = _tool == pair.Key ? Color.FromArgb(215, 237, 247) : SystemColors.Control;
            pair.Value.FlatStyle = FlatStyle.Flat; pair.Value.FlatAppearance.BorderColor = Color.FromArgb(197, 212, 222);
        }
        var segment = SelectedSegment;
        _pointRole.Items.Clear();
        if (segment != null)
        {
            _pointRole.Items.Add("Start"); _pointRole.Items.Add("End");
            if (_selection.Role is PointRole.None or PointRole.Middle) _selection.Role = PointRole.End;
            _pointRole.SelectedItem = _selection.Role == PointRole.Start ? "Start" : "End";
        }
        var point = SelectedPoint;
        _selectionTitle.Text = segment == null ? "Select a point or segment" : $"Segment {_selection.Segment + 1}";
        bool editable = CanEditSelectedLine() && _tool != ShapeTool.Fillet;
        _u.Enabled = _v.Enabled = _applyPoint.Enabled = _pointRole.Enabled = point != null && editable;
        if (point != null) { Set(_u, point.Rho); Set(_v, point.Z); }
        _length.Enabled = _applyLength.Enabled = editable;
        if (segment?.Kind == SegmentKind.Line) Set(_length, SketchDrawing.Distance(segment.Start, segment.End));
        _radius.Text = segment != null && !CanEditSelectedLine() ? "Connected to arc · Read-only" : "Keep start and direction";
        bool isArc = segment?.Kind == SegmentKind.Arc, filletMode = _tool == ShapeTool.Fillet;
        _coordinateRow.Visible = _lengthRow.Visible = !filletMode && !isArc;
        _arcReadoutRow.Visible = !filletMode && isArc;
        _arcReadout.Text = isArc ? SketchDrawing.Arc(segment!, out _, out double r, out _, out _) ? $"Centerline arc Ø{2 * r:0.###} mm · Read-only" : "Invalid arc" : "";
        _filletRow.Visible = true;
        _filletLabel.Text = filletMode ? "Arc diameter mm" : "Default fillet Ø mm";
        _applyFillet.Visible = filletMode;
        bool pairPicked = _filletFirst >= 0 && _filletSecond >= 0;
        string filletError = "";
        _applyFillet.Enabled = _tool == ShapeTool.Fillet && pairPicked && FilletGeometry.TryApply(_segments, _filletFirst, _filletSecond, (double)_diameter.Value, out _, out filletError);
        _save.Enabled = _segments.Count > 0 && !(_tool == ShapeTool.Fillet && _filletFirst >= 0);
        _status.Text = message ?? (_tool switch
        {
            ShapeTool.Base => "Select origin · Shape dimensions stay fixed",
            ShapeTool.Line => _hasBase ? "Continue from the last point · Finish to end" : "First point sets origin B",
            ShapeTool.Fillet => _filletFirst < 0 ? "Select the first line" : _filletSecond < 0 ? "Select the second line" : !string.IsNullOrEmpty(filletError) ? filletError : "Two lines selected · Enter diameter, then Apply Fillet",
            _ => "Origin B=(0,0)"
        });
        _canvas.Segments = _segments; _canvas.Pending = _pending; _canvas.Selection = _selection; _canvas.Tool = _tool; _canvas.HasBase = _hasBase;
        _canvas.FilletFirst = _filletFirst; _canvas.FilletSecond = _filletSecond; _canvas.Invalidate();
        _refreshing = false;
    }

    void SetTool(ShapeTool tool)
    {
        if (_tool == tool) return;
        Checkpoint(true);
        var first = _pending.FirstOrDefault()?.Copy();
        _tool = tool;
        _filletFirst = _filletSecond = -1;
        if (tool == ShapeTool.Line)
        {
            _pending = _segments.Count > 0 ? new() { _segments[^1].End.Copy() } : first != null ? new() { first } : new();
        }
        else _pending.Clear();
        RefreshEditor(); _canvas.Focus();
    }

    void ClickPoint(SketchPoint point)
    {
        if (_tool == ShapeTool.Base) { Rebase(point); return; }
        if (_tool != ShapeTool.Line) return;
        if (_segments.LastOrDefault()?.Kind == SegmentKind.Arc) { RefreshEditor("Arc endpoints are read-only; draw the polyline before adding fillets"); return; }
        Checkpoint();
        if (!_hasBase)
        {
            _hasBase = true; _canvas.Rebase(point); point = new(0, 0);
        }
        _pending.Add(point.Copy());
        if (_pending.Count < 2) { RefreshEditor(); return; }
        var segment = new SketchSegment { Kind = SegmentKind.Line, Start = _pending[0].Copy(), End = _pending[^1].Copy(), Middle = _pending[0].Copy() };
        if (!ValidSegment(segment, out string error))
        {
            _pending.RemoveAt(_pending.Count - 1); RefreshEditor(error); return;
        }
        _segments.Add(segment); _pending = new() { segment.End.Copy() };
        _selection = new() { Segment = _segments.Count - 1, Role = PointRole.End };
        RefreshEditor();
    }

    void Rebase(SketchPoint newBase)
    {
        Checkpoint();
        foreach (var point in _segments.SelectMany(Points).Concat(_pending)) { point.Rho -= newBase.Rho; point.Z -= newBase.Z; }
        _hasBase = true; _canvas.Rebase(newBase); _pending.Clear(); _filletFirst = _filletSecond = -1; _tool = ShapeTool.Select;
        RefreshEditor("Origin updated · Path dimensions unchanged");
    }

    void Finish()
    {
        Checkpoint(true); _pending.Clear(); _filletFirst = _filletSecond = -1; _tool = ShapeTool.Select;
        RoundDefaults(out string message); RefreshEditor(message.Length > 0 ? message : null);
    }

    bool RoundDefaults(out string message)
    {
        message = "";
        var source = Clone(_automaticSource ?? _segments);
        if (!FilletGeometry.TryApplyDefaults(source, (double)_diameter.Value, out var rounded, out int count, out message)) return false;
        if (count == 0) return true;
        bool same = rounded.Count == _segments.Count && rounded.Zip(_segments).All(p => p.First.Kind == p.Second.Kind
            && SketchDrawing.Same(p.First.Start, p.Second.Start) && SketchDrawing.Same(p.First.Middle, p.Second.Middle) && SketchDrawing.Same(p.First.End, p.Second.End));
        if (!same)
        {
            Checkpoint(true, _automaticDiameter); _automaticSource = source; _automaticDiameter = (double)_diameter.Value; _segments = rounded;
            _pending.Clear(); _selection = new(); _filletFirst = _filletSecond = -1;
        }
        message = $"{count} fillets added · Centerline Ø{_diameter.Value:0.###} mm";
        return true;
    }

    void PickFilletLine(int index)
    {
        if (_tool != ShapeTool.Fillet) return;
        if (index < 0 || index >= _segments.Count || _segments[index].Kind != SegmentKind.Line) { RefreshEditor("Select a straight segment"); return; }
        if (_filletFirst < 0 || _filletSecond >= 0) { _filletFirst = index; _filletSecond = -1; }
        else if (_filletFirst == index) { RefreshEditor("Select another line"); return; }
        else _filletSecond = index;
        _selection = new() { Segment = index, Role = PointRole.None };
        RefreshEditor();
    }

    void ApplyFillet()
    {
        if (_tool != ShapeTool.Fillet || _filletFirst < 0 || _filletSecond < 0) return;
        if (!FilletGeometry.TryApply(_segments, _filletFirst, _filletSecond, (double)_diameter.Value, out var result, out string error)) { RefreshEditor(error); return; }
        if (!ValidPath(result, out error)) { RefreshEditor(error); return; }
        Checkpoint();
        int arcIndex = Math.Min(_filletFirst, _filletSecond) + 1;
        _segments = result;
        _pending.Clear(); _filletFirst = _filletSecond = -1;
        _selection = new() { Segment = arcIndex, Role = PointRole.End };
        RefreshEditor("Fillet applied · Select two lines for another");
    }

    bool CanEditSelectedLine()
    {
        if (SelectedSegment is not { Kind: SegmentKind.Line } selected) return false;
        var arcs = _segments.Where(s => s.Kind == SegmentKind.Arc).ToArray();
        // Moving a shared node can also rotate its adjoining straight segment.
        // Lock edits that would change any line attached to an existing arc.
        foreach (var line in _segments.Where(s => s.Kind == SegmentKind.Line))
        {
            bool affected = new[] { line.Start, line.End }.Any(p => SketchDrawing.Same(p, selected.Start) || SketchDrawing.Same(p, selected.End));
            if (affected && arcs.Any(a => new[] { a.Start, a.End }.Any(p => SketchDrawing.Same(p, line.Start) || SketchDrawing.Same(p, line.End)))) return false;
        }
        return true;
    }

    void ApplyPoint()
    {
        if (!CanEditSelectedLine() || _tool == ShapeTool.Fillet) { RefreshEditor("Points and lines connected to arcs are read-only"); return; }
        var point = SelectedPoint; if (point == null) return;
        var next = Clone(_segments); MoveMatching(next, point, new((double)_u.Value, (double)_v.Value));
        if (!ValidPath(next, out string error)) { RefreshEditor(error); return; }
        Checkpoint(); _segments = next; SyncPending(); RefreshEditor();
    }
    void ApplyLength()
    {
        if (!CanEditSelectedLine() || _tool == ShapeTool.Fillet) { RefreshEditor("Points and lines connected to arcs are read-only"); return; }
        var segment = SelectedSegment; if (segment?.Kind != SegmentKind.Line) return;
        double old = SketchDrawing.Distance(segment.Start, segment.End), length = (double)_length.Value;
        if (old <= .000001 || length <= .000001) { RefreshEditor("Line length must be greater than 0"); return; }
        var point = new SketchPoint(segment.Start.Rho + (segment.End.Rho - segment.Start.Rho) * length / old, segment.Start.Z + (segment.End.Z - segment.Start.Z) * length / old);
        var next = Clone(_segments); MoveMatching(next, segment.End, point);
        if (!ValidPath(next, out string error)) { RefreshEditor(error); return; }
        Checkpoint(); _segments = next; SyncPending(); RefreshEditor();
    }
    void SyncPending() { if (_pending.Count == 1 && _segments.Count > 0) _pending[0] = _segments[^1].End.Copy(); }

    void SaveShape()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { RefreshEditor("Enter a shape name"); _name.Focus(); return; }
        if (_tool == ShapeTool.Fillet && _filletFirst >= 0) { RefreshEditor("Apply the fillet or finish the selection"); return; }
        if (!RoundDefaults(out string roundingMessage)) { RefreshEditor(roundingMessage); return; }
        if (!ValidPath(_segments, out string error)) { RefreshEditor(error); return; }
        ResultTemplate = new ShapeTemplate { Id = _templateId, Name = _name.Text.Trim(), Segments = Clone(_segments) };
        DialogResult = DialogResult.OK; Close();
    }

    void Checkpoint(bool retainAutomatic = false, double? previousDiameter = null)
    {
        _undo.Push(new ShapeEditorState { Segments = Clone(_segments), AutomaticSource = _automaticSource == null ? null : Clone(_automaticSource), AutomaticDiameter = _automaticDiameter, Pending = _pending.Select(p => p.Copy()).ToList(), HasBase = _hasBase, Tool = _tool, View = _canvas.View, FilletFirst = _filletFirst, FilletSecond = _filletSecond, Diameter = previousDiameter ?? (double)_diameter.Value });
        if (!retainAutomatic) { _automaticSource = null; _automaticDiameter = null; }
        if (_undo.Count > 100) { var keep = _undo.Take(100).Reverse().ToArray(); _undo.Clear(); foreach (var state in keep) _undo.Push(state); }
    }
    void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop(); _segments = state.Segments; _automaticSource = state.AutomaticSource; _automaticDiameter = state.AutomaticDiameter; _pending = state.Pending; _hasBase = state.HasBase; _tool = state.Tool; _canvas.View = state.View; _selection = new();
        _filletFirst = state.FilletFirst; _filletSecond = state.FilletSecond; _refreshing = true; Set(_diameter, state.Diameter); _refreshing = false; RefreshEditor();
    }
    void ConstrainToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea; int width = Math.Min(Width, area.Width - 24), height = Math.Min(Height, area.Height - 24);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }
    static List<SketchSegment> Clone(IEnumerable<SketchSegment> segments) => segments.Select(s => new SketchSegment { Kind = s.Kind, Start = s.Start.Copy(), Middle = s.Middle.Copy(), End = s.End.Copy() }).ToList();
    static IEnumerable<SketchPoint> Points(SketchSegment s) { yield return s.Start; if (s.Kind == SegmentKind.Arc) yield return s.Middle; yield return s.End; }
    static void MoveMatching(List<SketchSegment> segments, SketchPoint oldPoint, SketchPoint newPoint)
    {
        var matches = segments.SelectMany(Points).Where(p => SketchDrawing.Same(p, oldPoint)).ToArray();
        foreach (var p in matches) { p.Rho = newPoint.Rho; p.Z = newPoint.Z; }
    }
    static bool ValidSegment(SketchSegment s, out string error)
    {
        error = "";
        if (Points(s).Any(p => !double.IsFinite(p.Rho) || !double.IsFinite(p.Z))) { error = "Invalid coordinates"; return false; }
        if (SketchDrawing.Distance(s.Start, s.End) < .000001) { error = "Start and end must be distinct"; return false; }
        if (s.Kind == SegmentKind.Arc)
        {
            double chord = SketchDrawing.Distance(s.Start, s.End);
            double cross = (s.End.Rho - s.Start.Rho) * (s.Middle.Z - s.Start.Z) - (s.End.Z - s.Start.Z) * (s.Middle.Rho - s.Start.Rho);
            // Reject numerically degenerate drawing inputs; this is not a bar-bending rule.
            if (Math.Abs(cross) / chord < .001 || !SketchDrawing.Arc(s, out _, out _, out _, out _) || SketchDrawing.Distance(s.Start, s.Middle) < .000001 || SketchDrawing.Distance(s.End, s.Middle) < .000001)
            { error = "Arc points must be distinct and non-collinear"; return false; }
        }
        return true;
    }
    static bool ValidPath(IReadOnlyList<SketchSegment> segments, out string error)
    {
        error = ""; if (segments.Count == 0) { error = "Draw a path first"; return false; }
        for (int i = 0; i < segments.Count; i++)
        {
            if (!ValidSegment(segments[i], out error)) return false;
            if (i > 0 && !SketchDrawing.Same(segments[i - 1].End, segments[i].Start)) { error = "Path segments must be connected"; return false; }
        }
        return true;
    }
}

internal enum ShapeTool { Select, Line, Fillet, Base }
internal sealed class ShapePick { public int Segment { get; set; } = -1; public PointRole Role { get; set; } }
internal sealed class ShapeEditorState
{
    public List<SketchSegment> Segments { get; set; } = new();
    public List<SketchSegment>? AutomaticSource { get; set; }
    public double? AutomaticDiameter { get; set; }
    public List<SketchPoint> Pending { get; set; } = new();
    public bool HasBase { get; set; }
    public ShapeTool Tool { get; set; }
    public int FilletFirst { get; set; } = -1;
    public int FilletSecond { get; set; } = -1;
    public double Diameter { get; set; } = FilletGeometry.DefaultDiameterMm;
    public (double X, double Y, double Scale) View { get; set; }
}

internal sealed class ShapeCanvas : Control
{
    public List<SketchSegment> Segments { get; set; } = new();
    public List<SketchPoint> Pending { get; set; } = new();
    public ShapePick Selection { get; set; } = new();
    public ShapeTool Tool { get; set; }
    public int FilletFirst { get; set; } = -1;
    public int FilletSecond { get; set; } = -1;
    public bool HasBase { get; set; }
    public event Action<SketchPoint>? PointClicked;
    public event Action<ShapePick>? SelectionChanged;
    public event Action<int>? FilletLinePicked;
    public event Action? FinishRequested;
    double _x, _y, _scale = .5;
    bool _initialized, _panning;
    Point _pan;
    SketchPoint? _hover;
    readonly Color _blue = Color.FromArgb(0, 121, 165);
    public (double X, double Y, double Scale) View { get => (_x, _y, _scale); set { _x = value.X; _y = value.Y; _scale = value.Scale; _initialized = true; Invalidate(); } }
    RectangleF Plot => new(45, 25, Math.Max(30, Width - 70), Math.Max(30, Height - 62));

    public ShapeCanvas()
    {
        Dock = DockStyle.Fill; BackColor = Color.White; DoubleBuffered = true; TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true); AccessibleName = "Shape local-coordinate canvas";
    }
    PointF Screen(SketchPoint p) => new((float)(Plot.Left + Plot.Width / 2 + (p.Rho - _x) * _scale), (float)(Plot.Top + Plot.Height / 2 - (p.Z - _y) * _scale));
    SketchPoint World(Point p) => new(_x + (p.X - Plot.Left - Plot.Width / 2) / _scale, _y - (p.Y - Plot.Top - Plot.Height / 2) / _scale);
    public void Rebase(SketchPoint p) { _x -= p.Rho; _y -= p.Z; Invalidate(); }
    public void Fit()
    {
        var points = Segments.SelectMany(SketchDrawing.Polyline).Concat(Pending).Append(new SketchPoint(0, 0)).ToArray();
        double minX = points.Min(p => p.Rho), maxX = points.Max(p => p.Rho), minY = points.Min(p => p.Z), maxY = points.Max(p => p.Z);
        double spanX = Math.Max(400, (maxX - minX) * 1.35), spanY = Math.Max(400, (maxY - minY) * 1.35);
        if (Segments.Count == 0 && Pending.Count == 0) spanX = spanY = 1000;
        _x = (minX + maxX) / 2; _y = (minY + maxY) / 2; _scale = Math.Clamp(Math.Min(Plot.Width / spanX, Plot.Height / spanY), .00001, 100); _initialized = true; Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (!_initialized) Fit();
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var small = new Font(Font.FontFamily, 8.5f); using var ink = new SolidBrush(Color.FromArgb(64, 88, 106));
        var state = g.Save(); g.SetClip(Plot);
        DrawGrid(g, small);
        using var normal = new Pen(_blue, 2.3f);
        for (int i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i]; var points = SketchDrawing.Polyline(segment).Select(Screen).ToArray();
            bool filletSide = Tool == ShapeTool.Fillet && (i == FilletFirst || i == FilletSecond);
            if (filletSide)
            {
                using var glow = new Pen(Color.FromArgb(55, 210, 139, 38), 9); g.DrawLines(glow, points);
                using var chosen = new Pen(Color.FromArgb(195, 123, 24), 3); g.DrawLines(chosen, points);
            }
            else
            {
                if (Selection.Segment == i) { using var glow = new Pen(Color.FromArgb(40, 0, 121, 165), 9); g.DrawLines(glow, points); }
                g.DrawLines(normal, points);
            }
            if (Tool != ShapeTool.Fillet)
            {
                Node(g, segment.Start, Selection.Segment == i && Selection.Role == PointRole.Start);
                Node(g, segment.End, Selection.Segment == i && Selection.Role == PointRole.End);
            }
        }
        DrawPending(g);
        if (HasBase)
        {
            var p = Screen(new(0, 0)); using var basePen = new Pen(Color.FromArgb(46, 67, 85), 1.8f);
            g.DrawLine(basePen, p.X - 7, p.Y, p.X + 7, p.Y); g.DrawLine(basePen, p.X, p.Y - 7, p.X, p.Y + 7);
            g.FillEllipse(Brushes.White, p.X - 3, p.Y - 3, 6, 6); g.DrawEllipse(basePen, p.X - 3, p.Y - 3, 6, 6);
            g.DrawString("B (0, 0)", Font, ink, p.X + 10, p.Y + 8);
        }
        g.Restore(state);
        g.DrawString("Wheel: zoom · Middle: pan", small, ink, 12, Height - 24);
        if (_hover != null)
        {
            string text = $"u {_hover.Rho:0.###}   v {_hover.Z:0.###} mm"; var size = g.MeasureString(text, small);
            g.DrawString(text, small, ink, Width - size.Width - 12, Height - 24);
        }
        using var border = new Pen(Focused ? Color.FromArgb(98, 168, 197) : Color.FromArgb(211, 224, 232)); g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
    void DrawGrid(Graphics g, Font font)
    {
        double target = 85 / _scale, power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(target, .001))));
        double step = power * (target / power < 2 ? 2 : target / power < 5 ? 5 : 10);
        var low = World(new((int)Plot.Left, (int)Plot.Bottom)); var high = World(new((int)Plot.Right, (int)Plot.Top));
        using var grid = new Pen(Color.FromArgb(235, 241, 245)); using var labels = new SolidBrush(Color.FromArgb(143, 161, 175));
        int count = 0;
        for (double x = Math.Ceiling(low.Rho / step) * step; x <= high.Rho && count++ < 100; x += step)
        {
            var p = Screen(new(x, 0)); g.DrawLine(grid, p.X, Plot.Top, p.X, Plot.Bottom); g.DrawString(x.ToString("0.##"), font, labels, p.X + 3, Plot.Bottom - 17);
        }
        count = 0;
        for (double y = Math.Ceiling(low.Z / step) * step; y <= high.Z && count++ < 100; y += step)
        {
            var p = Screen(new(0, y)); g.DrawLine(grid, Plot.Left, p.Y, Plot.Right, p.Y); g.DrawString(y.ToString("0.##"), font, labels, Plot.Left + 3, p.Y + 2);
        }
        var origin = Screen(new(0, 0)); using var axes = new Pen(Color.FromArgb(150, 172, 187), 1.1f) { DashStyle = DashStyle.Dash };
        g.DrawLine(axes, Plot.Left, origin.Y, Plot.Right, origin.Y); g.DrawLine(axes, origin.X, Plot.Top, origin.X, Plot.Bottom);
        g.DrawString("u", Font, labels, Plot.Right - 17, Math.Clamp(origin.Y - 22, Plot.Top, Plot.Bottom - 25));
        g.DrawString("v", Font, labels, Math.Clamp(origin.X + 7, Plot.Left, Plot.Right - 20), Plot.Top + 4);
    }
    void Node(Graphics g, SketchPoint point, bool active)
    {
        var p = Screen(point); float radius = active ? 5 : 3.5f; using var pen = new Pen(_blue, 1.4f); using var fill = new SolidBrush(active ? _blue : Color.White);
        g.FillEllipse(fill, p.X - radius, p.Y - radius, radius * 2, radius * 2); g.DrawEllipse(pen, p.X - radius, p.Y - radius, radius * 2, radius * 2);
    }
    void DrawPending(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(195, 137, 43), 1.8f) { DashStyle = DashStyle.Dash };
        if (Pending.Count == 0) return;
        var points = Pending.Select(p => p.Copy()).ToList();
        if (_hover != null && Tool == ShapeTool.Line) points.Add(_hover.Copy());
        var rendered = points.Select(Screen).ToArray();
        if (rendered.Length > 1) g.DrawLines(pen, rendered);
        foreach (var p in Pending) Node(g, p, false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.Button == MouseButtons.Middle) { _panning = true; _pan = e.Location; Capture = true; Cursor = Cursors.SizeAll; return; }
        if (e.Button == MouseButtons.Right) { FinishRequested?.Invoke(); return; }
        if (e.Button != MouseButtons.Left || !Plot.Contains(e.Location)) return;
        if (Tool == ShapeTool.Select) { SelectionChanged?.Invoke(Hit(e.Location)); return; }
        if (Tool == ShapeTool.Fillet) { FilletLinePicked?.Invoke(HitLine(e.Location)); return; }
        var p = World(e.Location); PointClicked?.Invoke(new(Math.Round(p.Rho, 0, MidpointRounding.AwayFromZero), Math.Round(p.Z, 0, MidpointRounding.AwayFromZero)));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning) { _x -= (e.X - _pan.X) / _scale; _y += (e.Y - _pan.Y) / _scale; _pan = e.Location; }
        var p = World(e.Location); _hover = new(Math.Round(p.Rho, 0, MidpointRounding.AwayFromZero), Math.Round(p.Z, 0, MidpointRounding.AwayFromZero));
        Cursor = _panning ? Cursors.SizeAll : Tool == ShapeTool.Select ? Cursors.Default : Cursors.Cross; Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Middle) { _panning = false; Capture = false; Cursor = Cursors.Default; } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_panning) _hover = null; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e); var before = World(e.Location); _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.18 : 1 / 1.18), .00001, 100); var after = World(e.Location); _x += before.Rho - after.Rho; _y += before.Z - after.Z; Invalidate();
    }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Enter) { FinishRequested?.Invoke(); e.Handled = true; } }
    int HitLine(Point mouse)
    {
        int result = -1; double closest = 10;
        for (int i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i]; if (segment.Kind != SegmentKind.Line) continue;
            double distance = DistanceToLine(mouse, Screen(segment.Start), Screen(segment.End));
            if (distance < closest) { closest = distance; result = i; }
        }
        return result;
    }
    ShapePick Hit(Point mouse)
    {
        double closest = 11; var result = new ShapePick();
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            foreach (var pair in new[] { (s.Start, PointRole.Start), (s.End, PointRole.End) })
            {
                var p = Screen(pair.Item1); double distance = Math.Sqrt(Math.Pow(mouse.X - p.X, 2) + Math.Pow(mouse.Y - p.Y, 2));
                if (distance < closest) { closest = distance; result = new() { Segment = i, Role = pair.Item2 }; }
            }
        }
        if (result.Segment >= 0) return result;
        closest = 8;
        for (int i = 0; i < Segments.Count; i++)
        {
            var points = SketchDrawing.Polyline(Segments[i]).Select(Screen).ToArray();
            for (int j = 1; j < points.Length; j++)
            {
                double d = DistanceToLine(mouse, points[j - 1], points[j]);
                if (d < closest) { closest = d; result = new() { Segment = i, Role = PointRole.None }; }
            }
        }
        return result;
    }
    static double DistanceToLine(Point p, PointF a, PointF b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, square = dx * dx + dy * dy;
        double t = square < .000001 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / square, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }
}
