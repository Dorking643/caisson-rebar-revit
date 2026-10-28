using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CaissonWallUi;

/// <summary>Editable managed slab workspace. Model writes are delegated through API-context events.</summary>
public sealed class SlabReviewForm : Form
{
    SlabDocument _document;
    readonly SlabCanvas _plan=new(true),_section=new(false),_sectionOrthogonal=new(false){SectionAngleDeg=90,SectionLabel="B"};
    double _insertionDirection;
    readonly ListBox _types=new(){Dock=DockStyle.Fill,IntegralHeight=false};
    readonly ListBox _groups=new(){Dock=DockStyle.Fill,IntegralHeight=false,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=27};
    readonly ShapeLibraryList _shapes=new();
    readonly Label _summary=new(){AutoSize=true,Padding=new Padding(4,7,4,3),Text="2D Preview"};
    readonly Label _detail=new(){AutoSize=true,MaximumSize=new Size(270,0),ForeColor=Color.FromArgb(190,49,48),Padding=new Padding(3)};
    readonly Label _sourceNote=new(){AutoSize=false,AutoEllipsis=true,Dock=DockStyle.Fill,Height=42,MaximumSize=new Size(165,42),ForeColor=Color.FromArgb(151,105,37),Padding=new Padding(2,3,2,2),Cursor=Cursors.Hand};
    readonly ToolTip _sourceTip=new(){AutoPopDelay=15000,InitialDelay=350,ReshowDelay=150};
    readonly Label _host=new(){AutoSize=true,Padding=new Padding(3,3,3,7)};
    readonly Label _toolHint=new(){AutoSize=true,ForeColor=Color.FromArgb(100,118,130),Padding=new Padding(8,8,3,0)};
    readonly TextBox _name=new(){Dock=DockStyle.Fill};
    readonly ComboBox _shape=Choice(),_stretch=Choice(),_rangeMode=Choice(),_angleMode=Choice(),_distribution=Choice();
    readonly NumericUpDown _z=Number(),_direction=Number(-36000,36000),_start=Number(),_end=Number(),_spacing=Number(.001m),
        _count=Number(1,5000,0),_radius=Number(.001m),_radiusStep=Number(.001m),_inner=Number(0),_outer=Number(.001m),
        _startAngle=Number(-36000,36000),_angleStep=Number(.001m,360),_along=Number(),_boundaryRadius=Number(0),
        _coverTop=Number(0,10000),_coverBottom=Number(0,10000),_coverSide=Number(0,10000),_collisionTolerance=Number(0,100);
    readonly CheckBox _fit=new(){Text="Fit boundary",AutoSize=true},_extension=new(){Text="Extend above top",AutoSize=true},_enabled=new(){Text="Enabled",AutoSize=true},_positionConfirmed=new(){Text="Position verified",AutoSize=true};
    readonly GroupBox _parameters=new(){Text="Position · mm",Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(7)};
    readonly TableLayoutPanel _parameterTable=new(){Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Margin=new Padding(0)};
    readonly Dictionary<string,(Control Label,Control Input,int Row)> _rows=new();
    readonly Button _build=new(){Text="Create Rebar",AutoSize=true,Enabled=false,Padding=new Padding(12,4,12,4)};
    readonly Button _show=new(){Text="Show Model",AutoSize=true},_saveModel=new(){Text="Save Model",AutoSize=true},_refresh=new(){Text="Refresh",AutoSize=true};
    readonly Button _coverToggle=new(){Text="Cover ▸",AutoSize=true};
    readonly Button _clear=new(){Text="Clear All",AutoSize=true};
    readonly CheckBox _allowCollisionViolation=new(){Text="Collisions: advisory",AutoSize=true,Padding=new Padding(3,5,3,0)};
    readonly Label _collisionLabel=new(){Text="Tolerance mm",AutoSize=true,Padding=new Padding(8,8,0,0)};
    bool _coverExpanded;
    readonly Dictionary<string,Button> _tools=new();
    readonly System.Windows.Forms.Timer _checkTimer=new(){Interval=180},_saveTimer=new(){Interval=550};
    readonly Stack<string> _undo=new();
    readonly string _savePath;
    ValidationReport? _validation;
    string? _selectedId;
    int _selectedMember=-1;
    string _tool="select";
    bool _loading,_busy,_closed,_checking,_coverDirty,_fitSelectedAfterCheck;
    long _revision,_validatedRevision=-1;
    List<BarPath3> _pendingBars=new();
    List<ValidationIssue> _pendingIssues=new();
    List<ValidationIssue> _pendingAccepted=new();
    string? _lastCheckpoint;
    string? _dragBefore;
    SlabGroup? _dragOriginal;
    List<BarPath3>? _dragBars;
    List<BarPath3>? _dragStartBars;
    public SlabDocument Document=>_document;
    public string DraftPath=>_savePath;
    public ValidationReport? LatestValidation=>_validation;
    public SlabDocument ExportDocument()=>SlabDocument.FromJson(_document.ToJson());
    public string ExportDraftJson()=>_document.ToJson();
    public event Action? RefreshRequested;
    public event Action? BuildRequested;
    public event Action? ShowModelRequested;
    public event Action? SaveRequested;
    SlabGroup? Selected=>_document.Groups.FirstOrDefault(g=>g.Id==_selectedId);
    ReviewBarType? CurrentType=>_types.SelectedItem as ReviewBarType??_document.Types.FirstOrDefault();

    public SlabReviewForm(SlabDocument document,string? draftDirectory=null)
    {
        _document=SlabDocument.FromJson(document.ToJson());
        Text="Slab Reinforcement";Name="CaissonSlabReview";Font=new Font("Segoe UI",9);
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.CenterScreen;
        ClientSize=new Size(1480,870);MinimumSize=new Size(1080,670);BackColor=Color.FromArgb(246,248,250);KeyPreview=true;
        ReviewPresets.EnsureTypes(_document.Types);
        var folder=draftDirectory??RuntimePaths.SlabDrafts;
        _savePath=Path.Combine(folder,$"slab_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");
        BuildInterface();BindEvents();RefreshAll();_lastCheckpoint=_document.ToJson();
        Shown+=(_,_)=>{ConstrainToScreen();_plan.Fit();_section.Fit();_sectionOrthogonal.Fit();Changed(false);};
        FormClosing+=(_,e)=>
        {
            if(_busy&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;_summary.Text="Working, please wait.";return;}
            CancelCanvasDrags();_checkTimer.Stop();_saveTimer.Stop();_revision++;
            try{WriteDraft();}catch(Exception ex)
            {
                if(e.CloseReason is CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall or CloseReason.TaskManagerClosing)return;
                e.Cancel=MessageBox.Show(this,"Could not save the draft. Keep the window open?\n"+ex.Message,"Save Draft",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
                if(e.Cancel){_checkTimer.Start();_saveTimer.Start();}
            }
        };
        FormClosed+=(_,_)=>{_closed=true;_revision++;_checkTimer.Dispose();_saveTimer.Dispose();_sourceTip.Dispose();};
    }
    static ComboBox Choice()=>new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    static NumericUpDown Number(decimal min=-10000000,decimal max=10000000,int decimals=3)=>new(){Minimum=min,Maximum=max,DecimalPlaces=decimals,Increment=1,Dock=DockStyle.Fill,Margin=new Padding(3,3,3,3)};
    static FlowLayoutPanel Flow()=>new(){Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Margin=new Padding(0),Padding=new Padding(2)};
    static Button ButtonFor(string text,Action click){var b=new Button{Text=text,AutoSize=true,UseMnemonic=false,Padding=new Padding(5,2,5,2),Margin=new Padding(2)};b.Click+=(_,_)=>click();return b;}
    static GroupBox ListGroup(string text,ListBox list,params Button[] buttons)
    {
        var box=new GroupBox{Text=text,Dock=DockStyle.Fill,Padding=new Padding(6)};
        var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};t.RowStyles.Add(new(SizeType.Percent,100));t.RowStyles.Add(new(SizeType.AutoSize));
        t.Controls.Add(list,0,0);if(buttons.Length>0){var f=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=buttons.Length,RowCount=1,Height=36,Margin=new Padding(0)};for(int i=0;i<buttons.Length;i++){float share=buttons.Length==2&&buttons[1].Text=="Edit & Save As"?(i==0?40:60):100f/buttons.Length;f.ColumnStyles.Add(new(SizeType.Percent,share));buttons[i].AutoSize=false;buttons[i].Dock=DockStyle.Fill;buttons[i].Padding=new Padding(0);f.Controls.Add(buttons[i],i,0);}t.Controls.Add(f,0,1);}box.Controls.Add(t);return box;
    }
    void ConstrainToScreen(){var a=Screen.FromControl(this).WorkingArea;int w=Math.Min(Width,a.Width-24),h=Math.Min(Height,a.Height-24);MinimumSize=new(Math.Min(MinimumSize.Width,w),Math.Min(MinimumSize.Height,h));Bounds=new(a.Left+(a.Width-w)/2,a.Top+(a.Height-h)/2,w,h);}
    void BuildInterface()
    {
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(10)};Controls.Add(root);
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));
        var header=Flow();header.Controls.Add(_host);root.Controls.Add(header,0,0);
        var tools=Flow();tools.WrapContents=false;foreach(var pair in new[]{("select","Select"),("ring","Ring"),("orthogonal","Insert Shape")})
        {var key=pair.Item1;var b=ButtonFor(pair.Item2,()=>SetTool(key));_tools.Add(key,b);tools.Controls.Add(b);}
        _clear.Click+=(_,_)=>ClearPlacements();tools.Controls.Add(_clear);
        var covers=_coverToggle;var coverPanel=Flow();coverPanel.Visible=false;
        foreach(var pair in new[]{("Top",_coverTop),("Bottom",_coverBottom),("Side",_coverSide)}){coverPanel.Controls.Add(new Label{Text=pair.Item1,AutoSize=true,Padding=new Padding(3,7,0,0)});pair.Item2.Width=68;pair.Item2.Dock=DockStyle.None;coverPanel.Controls.Add(pair.Item2);}
        coverPanel.Controls.Add(ButtonFor("Apply",ApplyCover));covers.Click+=(_,_)=>{coverPanel.Visible=!coverPanel.Visible;_coverExpanded=coverPanel.Visible;UpdateCoverCaption();};tools.Controls.Add(covers);root.Controls.Add(coverPanel,0,2);
        coverPanel.Controls.Add(_collisionLabel);_collisionTolerance.Dock=DockStyle.None;_collisionTolerance.Width=65;_collisionTolerance.AccessibleName="Tolerance mm";coverPanel.Controls.Add(_collisionTolerance);coverPanel.Controls.Add(_allowCollisionViolation);coverPanel.Controls.Add(_refresh);tools.Controls.Add(_toolHint);root.Controls.Add(tools,0,1);
        var work=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=new Padding(0)};
        work.ColumnStyles.Add(new(SizeType.Absolute,230));work.ColumnStyles.Add(new(SizeType.Percent,48));work.ColumnStyles.Add(new(SizeType.Percent,52));work.ColumnStyles.Add(new(SizeType.Absolute,284));root.Controls.Add(work,0,3);
        var left=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0,0,6,0)};left.RowStyles.Add(new(SizeType.Percent,42));left.RowStyles.Add(new(SizeType.Percent,58));
        left.Controls.Add(ListGroup("Bar Types",_types,ButtonFor("Add",()=>EditType(true)),ButtonFor("Edit",()=>EditType(false)),ButtonFor("Apply",AssignType)),0,0);
        left.Controls.Add(ListGroup("Shape Library",_shapes,ButtonFor("New",()=>EditShape(false)),ButtonFor("Edit & Save As",()=>EditShape(true))),0,1);work.Controls.Add(left,0,0);
        _plan.Margin=new Padding(0,4,4,0);work.Controls.Add(_plan,1,0);
        var sections=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0)};sections.RowStyles.Add(new(SizeType.Percent,50));sections.RowStyles.Add(new(SizeType.Percent,50));
        _section.Margin=new Padding(4,4,4,3);_sectionOrthogonal.Margin=new Padding(4,3,4,0);sections.Controls.Add(_section,0,0);sections.Controls.Add(_sectionOrthogonal,0,1);work.Controls.Add(sections,2,0);
        var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=new Padding(3,0,0,0)};right.RowStyles.Add(new(SizeType.Percent,31));right.RowStyles.Add(new(SizeType.Percent,69));right.RowStyles.Add(new(SizeType.AutoSize));
        right.Controls.Add(ListGroup("Placed Bars",_groups,ButtonFor("Delete",DeleteSelected),ButtonFor("Duplicate",DuplicateSelected)),0,0);
        var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};BuildParameters();scroll.Controls.Add(_parameters);right.Controls.Add(scroll,0,1);
        right.Controls.Add(_detail,0,2);work.Controls.Add(right,3,0);
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2};footer.ColumnStyles.Add(new(SizeType.Percent,100));footer.ColumnStyles.Add(new(SizeType.AutoSize));footer.Controls.Add(_summary,0,0);
        var actions=Flow();actions.WrapContents=false;actions.AutoSizeMode=AutoSizeMode.GrowAndShrink;actions.Controls.AddRange(new Control[]{_show,_saveModel,_build});footer.Controls.Add(actions,1,0);root.Controls.Add(footer,0,4);
        var tips=new ToolTip();tips.SetToolTip(_stretch,"Only this segment changes length; both end hooks retain their shape.");tips.SetToolTip(_fit,"Only the middle span varies within the group; end dimensions remain fixed.");tips.SetToolTip(_extension,"For starter bars explicitly allowed to extend above the slab.");
    }
    void Row(string key,string label,Control input)
    {
        int row=_parameterTable.RowCount++;_parameterTable.RowStyles.Add(new(SizeType.AutoSize));
        var l=new Label{Text=label,AutoSize=true,Padding=new Padding(2,7,2,0)};_parameterTable.Controls.Add(l,0,row);_parameterTable.Controls.Add(input,1,row);_rows.Add(key,(l,input,row));
    }
    void BuildParameters()
    {
        _parameterTable.ColumnStyles.Add(new(SizeType.Absolute,98));_parameterTable.ColumnStyles.Add(new(SizeType.Percent,100));
        _rangeMode.Items.AddRange(new object[]{"To end","By count"});_angleMode.Items.AddRange(new object[]{"By count","By angle"});_distribution.Items.AddRange(new object[]{"Radial","Orthogonal"});
        Row("name","Name",_name);Row("distribution","Distribution",_distribution);Row("enabled","",_enabled);Row("source","Review",_sourceNote);Row("confirmed","",_positionConfirmed);Row("z","Elevation Z",_z);Row("direction","Direction °",_direction);
        Row("range","Range",_rangeMode);Row("start","Start",_start);Row("end","End",_end);Row("spacing","Spacing",_spacing);Row("count","Count",_count);
        Row("radius","Radius",_radius);Row("rstep","Radial step",_radiusStep);Row("inner","Base radius",_inner);Row("outer","Outer radius",_outer);Row("startangle","Start °",_startAngle);Row("anglemode","Array",_angleMode);Row("anglestep","Step °",_angleStep);
        Row("shape","Shape",_shape);Row("stretch","Stretch",_stretch);Row("fit","",_fit);Row("along","Along",_along);Row("boundary","Boundary R",_boundaryRadius);Row("extension","",_extension);
        _fit.Text="Fit circular edge";Row("editshape","",ButtonFor("Edit Shape",EditGroupShape));Row("sourceDetails","",ButtonFor("Source Note",ShowSource));_parameters.Controls.Add(_parameterTable);
    }
    void BindEvents()
    {
        foreach(var canvas in new[]{_plan,_section,_sectionOrthogonal})
        {
            canvas.BarSelected+=SelectGroup;canvas.PointClicked+=p=>PickFromCanvas(p,canvas);canvas.FinishRequested+=()=>SetTool("select");canvas.DeleteRequested+=DeleteSelected;
            canvas.BarDragStarted+=BeginDrag;canvas.BarDragMoved+=MoveDrag;canvas.BarDragCompleted+=EndDrag;
            canvas.MouseUp+=(_,_)=>
            {
                bool pressed=canvas.IsDragGesture;long revision=_revision;
                if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)(()=>{if(_closed)return;Redraw();if(pressed&&revision==_revision){FitSelectedSections();_fitSelectedAfterCheck=_validatedRevision!=_revision;}}));
            };
            if(canvas!=_plan)canvas.MouseDown+=(_,_)=>_insertionDirection=canvas.SectionAngleDeg;
        }
        _groups.SelectedIndexChanged+=(_,_)=>{if(!_loading&&_groups.SelectedItem is SlabGroup g)SelectGroup(g.Id,-1);};_groups.DrawItem+=DrawGroup;
        _groups.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Delete){DeleteSelected();e.Handled=true;}};
        _types.DoubleClick+=(_,_)=>EditType(false);_shapes.DoubleClick+=(_,_)=>EditShape(true);
        _sourceNote.Click+=(_,_)=>ShowSource();
        _shape.SelectedIndexChanged+=(_,_)=>ShapeChanged();_stretch.SelectedIndexChanged+=(_,_)=>StretchChanged();
        _distribution.SelectedIndexChanged+=(_,_)=>DistributionChanged();
        foreach(var n in new[]{_z,_direction,_radius,_radiusStep,_inner,_outer,_startAngle,_angleStep,_along,_boundaryRadius})n.ValueChanged+=(_,_)=>ParametersChanged();
        _count.ValueChanged+=(_,_)=>{if(_loading||_busy||Selected is not {} g)return;Checkpoint();g.Count=(int)_count.Value;ParametersChanged();};
        foreach(var n in new[]{_start,_end,_spacing})n.ValueChanged+=(_,_)=>RangeChanged();
        _name.TextChanged+=(_,_)=>ParametersChanged();_rangeMode.SelectedIndexChanged+=(_,_)=>ParametersChanged();_angleMode.SelectedIndexChanged+=(_,_)=>ParametersChanged();_fit.CheckedChanged+=(_,_)=>FitChanged();_extension.CheckedChanged+=(_,_)=>ParametersChanged();_enabled.CheckedChanged+=(_,_)=>ParametersChanged();_positionConfirmed.CheckedChanged+=(_,_)=>ParametersChanged();
        foreach(var n in new[]{_coverTop,_coverBottom,_coverSide})n.ValueChanged+=(_,_)=>{if(_loading)return;_coverDirty=true;_build.Enabled=false;_summary.Text="Apply cover changes";};
        _collisionTolerance.ValueChanged+=(_,_)=>{if(_loading||_busy)return;Checkpoint();_document.CheckSettings.CollisionToleranceMm=(double)_collisionTolerance.Value;Changed();};
        _allowCollisionViolation.CheckedChanged+=(_,_)=>{if(_loading||_busy)return;Checkpoint();_document.CheckSettings.AllowCollisionViolation=_allowCollisionViolation.Checked;UpdateCollisionCaption();Changed();};
        _summary.Cursor=Cursors.Hand;_summary.Click+=(_,_)=>{var issue=_validation?.Issues.FirstOrDefault();if(issue!=null){if(issue.DraftId.Length>0)SelectGroup(issue.DraftId,issue.MemberIndex);_detail.Text=issue.Message;}};
        _build.Click+=(_,_)=>Request(BuildRequested,"Creating rebar…",true);_refresh.Click+=(_,_)=>Request(RefreshRequested,"Refreshing…",false);_show.Click+=(_,_)=>Request(ShowModelRequested,"Opening model view…",false);_saveModel.Click+=(_,_)=>Request(SaveRequested,"Saving model…",false);
        _checkTimer.Tick+=async(_,_)=>{_checkTimer.Stop();await CheckAsync();};_saveTimer.Tick+=(_,_)=>{_saveTimer.Stop();try{WriteDraft();}catch(Exception e){_summary.Text="Draft save failed: "+e.Message;}};
        KeyDown+=(_,e)=>{if(e.Control&&e.KeyCode==Keys.Z){if(_dragBefore!=null)CancelCanvasDrags();else Undo();e.Handled=true;e.SuppressKeyPress=true;}if(e.KeyCode==Keys.Escape){CancelCanvasDrags();SetTool("select");}};
    }
    void SetTool(string tool)
    {
        if(_busy)return;if(tool=="locate"&&Selected==null){_summary.Text="Select a bar group first.";return;}
        CancelCanvasDrags();_tool=tool;_toolHint.Text=tool=="select"?"":tool=="locate"?"Plan: position · Section: elevation":"Pick a point";
        foreach(var p in _tools)p.Value.BackColor=p.Key==tool?Color.FromArgb(215,237,247):SystemColors.Control;
        foreach(var canvas in new[]{_plan,_section,_sectionOrthogonal}){canvas.PlacementMode=tool!="select";canvas.Invalidate();}
    }
    void PickFromCanvas(Point3 p,SlabCanvas canvas)
    {
        if(canvas!=_plan)_insertionDirection=canvas.SectionAngleDeg;
        Pick(p,canvas==_plan);
    }
    void Pick(Point3 p,bool plan)
    {
        if(_busy||_tool=="select")return;
        if(_tool=="locate"&&Selected is {} selected)
        {
            Checkpoint();if(!plan)selected.Z=Math.Round(p.Z,3);
            else if(selected.Kind==SlabGroupKind.Ring)selected.Radius=Math.Sqrt(p.X*p.X+p.Y*p.Y);
            else if(selected.Kind==SlabGroupKind.Radial)selected.StartAngle=Math.Atan2(p.Y,p.X)*180/Math.PI;
            else{double a=selected.DirectionDeg*Math.PI/180;double value=-p.X*Math.Sin(a)+p.Y*Math.Cos(a);selected.EndOffset+=value-selected.StartOffset;selected.StartOffset=value;}
            RefreshInspector();SetTool("select");Changed();return;
        }
        if(CurrentType is not {} type){_summary.Text="Add a bar type first.";return;}
        if(_tool=="orthogonal"&&_shapes.SelectedItem is not ShapeTemplate){_summary.Text="Select a shape, then pick its base point.";return;}
        var kind=_tool=="ring"?SlabGroupKind.Ring:_tool=="radial"||(_tool=="orthogonal"&&Selected?.Kind==SlabGroupKind.Radial)?SlabGroupKind.Radial:SlabGroupKind.Orthogonal;
        Checkpoint();var g=new SlabGroup{Id=Guid.NewGuid().ToString("N"),TypeId=type.Id,Kind=kind};
        g.Name=KindName(g.Kind)+" "+(_document.Groups.Count+1);g.Z=plan?-(Math.Max(0,_document.CheckSettings.TopCoverMm)+type.DiameterMm/2):Math.Round(p.Z,3);g.Count=1;g.UseEndOffset=false;
        if(g.Kind==SlabGroupKind.Ring)g.Radius=plan?Math.Sqrt(p.X*p.X+p.Y*p.Y):Math.Sqrt(p.X*p.X+p.Y*p.Y);
        else if(g.Kind==SlabGroupKind.Radial){g.InnerRadius=Math.Sqrt(p.X*p.X+p.Y*p.Y);g.OuterRadius=Math.Max(g.InnerRadius+1,_document.Host.OuterRadiusMm-_document.CheckSettings.SideCoverMm-type.DiameterMm/2);g.StartAngle=g.InnerRadius>1e-6?Math.Atan2(p.Y,p.X)*180/Math.PI:_insertionDirection;g.FitToBoundary=false;}
        else{g.DirectionDeg=_insertionDirection;double a=g.DirectionDeg*Math.PI/180;g.StartOffset=Math.Round(-p.X*Math.Sin(a)+p.Y*Math.Cos(a),3);g.EndOffset=g.StartOffset;g.AlongOffset=Math.Round(p.X*Math.Cos(a)+p.Y*Math.Sin(a),3);g.FitToBoundary=false;}
        if(g.Kind!=SlabGroupKind.Ring&&_shapes.SelectedItem is ShapeTemplate shape&&!ApplyShape(g,shape,-1))return;
        _document.Groups.Add(g);_selectedId=g.Id;_selectedMember=-1;_fitSelectedAfterCheck=true;SetTool("select");RefreshAll();Changed();
    }
    static string KindName(SlabGroupKind kind)=>kind==SlabGroupKind.Ring?"Ring":kind==SlabGroupKind.Radial?"Radial":"Orthogonal";
    void CancelCanvasDrags(){foreach(var canvas in new[]{_plan,_section,_sectionOrthogonal})canvas.CancelDrag();}
    void BeginDrag(SlabDragGesture gesture)
    {
        if(_busy||_tool!="select"||_document.Groups.All(g=>g.Id!=gesture.GroupId)){CancelCanvasDrags();return;}
        _dragBefore=_document.ToJson();_dragOriginal=SlabDocument.FromJson(_dragBefore).Groups.First(g=>g.Id==gesture.GroupId);
        _dragStartBars=_dragBars??(_validation?.Bars??new()).Concat(_pendingBars).ToList();
        _revision++;_checkTimer.Stop();_saveTimer.Stop();_build.Enabled=false;_fitSelectedAfterCheck=false;
    }
    void MoveDrag(SlabDragGesture gesture)
    {
        if(_busy||_dragBefore==null||_dragOriginal is not {} original||original.Id!=gesture.GroupId)return;
        var group=_document.Groups.First(g=>g.Id==original.Id);
        var candidate=System.Text.Json.JsonSerializer.Deserialize<SlabGroup>(System.Text.Json.JsonSerializer.Serialize(original,SlabDocument.JsonOptions),SlabDocument.JsonOptions)!;
        var p=gesture.StartWorld;var delta=gesture.DeltaWorld;
        double Round(double value)=>Math.Round(value,3);
        candidate.Z=Round(original.Z+delta.Z);
        if(original.Kind==SlabGroupKind.Orthogonal)
        {
            double a=original.DirectionDeg*Math.PI/180,c=Math.Cos(a),s=Math.Sin(a);
            double along=delta.X*c+delta.Y*s,across=-delta.X*s+delta.Y*c;
            candidate.AlongOffset=Round(original.AlongOffset+along);
            candidate.StartOffset=Round(original.StartOffset+across);
            // Apply exactly the same shift to both bounds, preserving the array range and count.
            candidate.EndOffset=original.EndOffset+(candidate.StartOffset-original.StartOffset);
        }
        else
        {
            double r0=Math.Sqrt(p.X*p.X+p.Y*p.Y),x=p.X+delta.X,y=p.Y+delta.Y,r1=Math.Sqrt(x*x+y*y),dr=r1-r0;
            if(original.Kind==SlabGroupKind.Ring)candidate.Radius=Round(original.Radius+dr);
            else
            {
                if(gesture.IsPlan)
                {
                    if(r0<.001||r1<.001){_summary.Text="Drag away from the centre.";return;}
                    candidate.StartAngle=Round(original.StartAngle+Math.Atan2(p.X*y-p.Y*x,p.X*x+p.Y*y)*180/Math.PI);
                }
                else
                {
                    int count=SlabGeometry.TryGetRadialCount(original,out var n,out _)?n:Math.Max(1,original.Count);
                    double a=(original.StartAngle+Math.Max(0,gesture.MemberIndex)*(original.UseAngleStep?original.AngleStep:360d/count))*Math.PI/180;
                    dr=delta.X*Math.Cos(a)+delta.Y*Math.Sin(a);
                }
                candidate.InnerRadius=Round(original.InnerRadius+dr);
                candidate.OuterRadius=original.OuterRadius+(candidate.InnerRadius-original.InnerRadius);
            }
        }
        try
        {
            // Geometry-only preview: no collision search, draft save, or Revit write while the pointer moves.
            var paths=SlabGeometry.PreviewGroup(_document,candidate);
            group.Z=candidate.Z;group.AlongOffset=candidate.AlongOffset;group.StartOffset=candidate.StartOffset;group.EndOffset=candidate.EndOffset;
            group.Radius=candidate.Radius;group.InnerRadius=candidate.InnerRadius;group.OuterRadius=candidate.OuterRadius;group.StartAngle=candidate.StartAngle;
            _dragBars=(_dragStartBars??new()).Where(b=>b.DraftId!=group.Id).Concat(paths).ToList();
            RefreshInspector();Redraw();_summary.Text="Drag to position · Esc to cancel";
        }
        catch(ArgumentException e){_summary.Text=e.Message;}
    }
    void EndDrag(bool cancelled)
    {
        if(_dragBefore==null)return;
        string before=_dragBefore;_dragBefore=null;_dragOriginal=null;
        if(cancelled){_dragBars=_dragStartBars;_document=SlabDocument.FromJson(before);RefreshAll();}
        else if(_document.ToJson()!=before)_undo.Push(before);
        _dragStartBars=null;_lastCheckpoint=_document.ToJson();RefreshInspector();Changed(false);
    }
    void Checkpoint(){var now=_document.ToJson();if(_lastCheckpoint!=null&&_lastCheckpoint!=now)_undo.Push(_lastCheckpoint);_lastCheckpoint=now;}
    void Undo(){if(_busy||_undo.Count==0)return;_dragBars=null;_document=SlabDocument.FromJson(_undo.Pop());_lastCheckpoint=_document.ToJson();RefreshAll();Changed(false);}
    void Changed(bool checkpoint=true)
    {
        if(checkpoint){_dragBars=null;var now=_document.ToJson();if(_lastCheckpoint!=null&&_lastCheckpoint!=now)_undo.Push(_lastCheckpoint);_lastCheckpoint=now;}
        _revision++;_build.Enabled=false;_summary.Text="Checking…";_checkTimer.Stop();_checkTimer.Start();_saveTimer.Stop();_saveTimer.Start();_groups.Invalidate();Redraw();
    }
    async Task CheckAsync()
    {
        if(_closed||_busy||_dragBefore!=null)return;if(_checking){_checkTimer.Start();return;}_checking=true;long version=_revision;
        try
        {
            var copy=ExportDocument();var result=await Task.Run(()=>
            {
                var main=SlabGeometry.Check(copy);var pendingBars=new List<BarPath3>();var pendingIssues=new List<ValidationIssue>();var pendingAccepted=new List<ValidationIssue>();
                foreach(var group in copy.Groups.Where(g=>!g.Enabled))
                {
                    var single=SlabDocument.FromJson(copy.ToJson());single.Groups=single.Groups.Where(g=>g.Enabled||g.Id==group.Id).ToList();single.Groups.First(g=>g.Id==group.Id).Enabled=true;
                    var own=SlabGeometry.Check(single);pendingBars.AddRange(own.Bars.Where(b=>b.DraftId==group.Id));pendingIssues.AddRange(own.Issues.Where(i=>i.DraftId==group.Id||i.OtherDraftId==group.Id));pendingAccepted.AddRange(own.AcceptedCollisions.Concat(own.AcceptedCoverViolations).Where(i=>i.DraftId==group.Id||i.OtherDraftId==group.Id));
                }
                return(main,pendingBars,pendingIssues,pendingAccepted);
            });
            if(!_closed&&!IsDisposed&&version==_revision){_pendingBars=result.pendingBars;_pendingIssues=result.pendingIssues;_pendingAccepted=result.pendingAccepted;SetValidation(result.main);}
        }
        catch(Exception e){if(!_closed&&!IsDisposed&&version==_revision)SetValidation(new(){IsComplete=false,Issues=new(){new(){Kind=ValidationKind.Incomplete,Message=e.Message}}});}
        finally{_checking=false;if(!_closed&&!IsDisposed&&version!=_revision)_checkTimer.Start();}
    }
    public void RefreshChecks(){if(!_closed)Changed(false);}
    public void SetValidation(ValidationReport validation)
    {
        if(_closed||IsDisposed||_dragBefore!=null)return;_dragBars=null;_validation=validation;_validatedRevision=_revision;_build.Enabled=!_busy&&!_coverDirty&&validation.CanBuild;
        int pending=_document.Groups.Where(g=>!g.Enabled).Sum(EstimatedCount);
        string quantities=$"{validation.Bars.Count} bars / {pending} pending";
        _summary.ForeColor=Color.FromArgb(45,66,81);
        int acceptedCount=validation.AcceptedCollisions.Count+validation.AcceptedCoverViolations.Count;
        _summary.Text=_coverDirty?"Apply cover changes":validation.CanBuild?quantities+(acceptedCount>0?$" · {acceptedCount} accepted":" · Checks passed"):validation.Issues.Count>0?quantities+$" · {validation.Issues.Count} issues":quantities;
        UpdateDetail();_groups.Invalidate();Redraw();
        if(_fitSelectedAfterCheck&&!new[]{_plan,_section,_sectionOrthogonal}.Any(c=>c.IsDragGesture)){_fitSelectedAfterCheck=false;FitSelectedSections();}
    }
    public void SetDocument(SlabDocument document)
    {
        CancelCanvasDrags();_dragBars=null;_document=SlabDocument.FromJson(document.ToJson());ReviewPresets.EnsureTypes(_document.Types);_lastCheckpoint=_document.ToJson();_undo.Clear();_coverDirty=false;_pendingBars.Clear();_pendingIssues.Clear();_pendingAccepted.Clear();SetBusy(false);RefreshAll();Changed(false);
    }
    public void SetBusy(bool busy,string message="")
    {
        if(_closed||IsDisposed)return;if(busy)CancelCanvasDrags();_busy=busy;_parameters.Enabled=!busy;_types.Enabled=_shapes.Enabled=_groups.Enabled=!busy;
        foreach(var canvas in new[]{_plan,_section,_sectionOrthogonal})canvas.Enabled=!busy;
        foreach(var b in _tools.Values)b.Enabled=!busy;_clear.Enabled=_refresh.Enabled=_show.Enabled=_saveModel.Enabled=_collisionTolerance.Enabled=_allowCollisionViolation.Enabled=!busy;
        _build.Enabled=!busy&&!_coverDirty&&_validatedRevision==_revision&&_validation?.CanBuild==true;if(message.Length>0)_summary.Text=message;
    }
    public void SetBuildResult(bool success,string message){SetBusy(false);_summary.Text=message;_summary.ForeColor=success?Color.FromArgb(35,98,77):Color.FromArgb(190,49,48);}
    void Request(Action? action,string message,bool build)
    {
        if(_busy||_dragBefore!=null)return;if(action==null){_summary.Text="Revit actions are not connected.";return;}
        if(build&&(_coverDirty||_validatedRevision!=_revision||_validation?.CanBuild!=true)){_summary.Text="Resolve the check items first.";return;}
        try{WriteDraft();SetBusy(true,message);action.Invoke();}catch(Exception e){SetBuildResult(false,e.Message);}
    }
    void SelectGroup(string id,int member)
    {
        if(_busy)return;_selectedId=id;_selectedMember=member;RefreshInspector();
        _loading=true;_groups.SelectedItem=Selected;_loading=false;
        UpdateDetail();Redraw();if(!new[]{_plan,_section,_sectionOrthogonal}.Any(c=>c.IsDragGesture)){FitSelectedSections();_fitSelectedAfterCheck=_validatedRevision!=_revision;}
    }
    void UpdateDetail()
    {
        bool Own(ValidationIssue issue)=>issue.DraftId==_selectedId||issue.OtherDraftId==_selectedId;
        var issue=(_validation?.Issues??new()).Concat(_pendingIssues).FirstOrDefault(Own);
        var advisory=(_validation?.AcceptedCollisions??new()).Concat(_validation?.AcceptedCoverViolations??new()).Concat(_pendingAccepted).FirstOrDefault(Own);
        _detail.ForeColor=issue!=null?Color.FromArgb(190,49,48):Color.FromArgb(151,105,37);
        _detail.Text=issue?.Message??advisory?.Message??"";
    }
    void FitSelectedSections(){_section.FitSectionToSelected();_sectionOrthogonal.FitSectionToSelected();}
    void RefreshAll()
    {
        _loading=true;var typeId=CurrentType?.Id;var shapeId=(_shapes.SelectedItem as ShapeTemplate)?.Id;
        _types.Items.Clear();_types.Items.AddRange(_document.Types.OrderBy(t=>t.Grade).ThenBy(t=>t.DiameterMm).Cast<object>().ToArray());_types.SelectedItem=_document.Types.FirstOrDefault(t=>t.Id==typeId)??_document.Types.FirstOrDefault();
        _shapes.Items.Clear();_shapes.Items.AddRange(_document.Shapes.Cast<object>().ToArray());_shapes.SelectedItem=_document.Shapes.FirstOrDefault(s=>s.Id==shapeId);
        _groups.Items.Clear();_groups.Items.AddRange(_document.Groups.Cast<object>().ToArray());_groups.SelectedItem=Selected;
        _shape.Items.Clear();_shape.Items.Add("Straight");_shape.Items.AddRange(_document.Shapes.Cast<object>().ToArray());
        Set(_coverTop,_document.CheckSettings.TopCoverMm);Set(_coverBottom,_document.CheckSettings.BottomCoverMm);Set(_coverSide,_document.CheckSettings.SideCoverMm);Set(_collisionTolerance,_document.CheckSettings.CollisionToleranceMm);_allowCollisionViolation.Checked=_document.CheckSettings.AllowCollisionViolation;UpdateCoverCaption();UpdateCollisionCaption();
        _host.Text=$"Host: {_document.Host.HostId}";
        _loading=false;RefreshInspector();SetTool(_tool);Redraw();
    }
    static void Set(NumericUpDown n,double v)=>n.Value=Math.Clamp((decimal)(double.IsFinite(v)?Math.Clamp(v,-1e10,1e10):0),n.Minimum,n.Maximum);
    void RefreshInspector()
    {
        bool was=_loading;_loading=true;var g=Selected;_parameters.Visible=g!=null;_parameters.Enabled=g!=null&&!_busy;
        if(g!=null)
        {
            _name.Text=g.Name;_distribution.SelectedIndex=g.Kind==SlabGroupKind.Radial?0:g.Kind==SlabGroupKind.Orthogonal?1:-1;_enabled.Checked=g.Enabled;_positionConfirmed.Checked=g.PositionConfirmed;_sourceNote.Text=BriefSource(g.SourceNote);_sourceTip.SetToolTip(_sourceNote,g.SourceNote);Set(_z,g.Z);Set(_direction,g.DirectionDeg);Set(_start,g.StartOffset);Set(_end,g.EndOffset);Set(_spacing,g.Spacing);Set(_count,g.Count);
            if(g.Kind==SlabGroupKind.Orthogonal&&SlabGeometry.TryGetOrthogonalRange(g,out var n,out var last,out _)){if(!g.UseEndOffset)Set(_end,last);Set(_count,n);}
            if(g.Kind==SlabGroupKind.Radial&&g.UseAngleStep&&SlabGeometry.TryGetRadialCount(g,out var radialCount,out _))Set(_count,radialCount);
            Set(_radius,g.Radius);Set(_radiusStep,g.RadiusStep);Set(_inner,g.InnerRadius);Set(_outer,g.OuterRadius);Set(_startAngle,g.StartAngle);Set(_angleStep,g.AngleStep);Set(_along,g.AlongOffset);Set(_boundaryRadius,g.GroupBoundaryRadiusMm);
            _rangeMode.SelectedIndex=g.UseEndOffset?0:1;_angleMode.SelectedIndex=g.UseAngleStep?1:0;_fit.Checked=g.FitToBoundary;_extension.Checked=g.AllowTopExtension;
            _shape.SelectedItem=_document.Shapes.FirstOrDefault(s=>s.Id==g.ShapeTemplateId) as object??_shape.Items[0];
            _types.SelectedItem=_document.Types.FirstOrDefault(t=>t.Id==g.TypeId);RefreshStretch(g);RefreshRows(g);
        }
        _loading=was;
    }
    void RefreshRows(SlabGroup g)
    {
        bool orth=g.Kind==SlabGroupKind.Orthogonal,ring=g.Kind==SlabGroupKind.Ring,rad=g.Kind==SlabGroupKind.Radial,shape=g.ShapeSnapshot.Count>0;
        var visible=new HashSet<string>{"name","enabled","z"};
        if(!g.Enabled||!g.PositionConfirmed){visible.Add("source");visible.Add("confirmed");}
        if(!string.IsNullOrWhiteSpace(g.SourceNote))visible.Add("sourceDetails");
        bool computedCount=orth||(rad&&g.UseAngleStep);_count.ReadOnly=computedCount;_count.Increment=computedCount?0:1;_count.TabStop=!computedCount;
        if(orth){visible.UnionWith(new[]{"distribution","direction","start","end","spacing","count","fit","along","editshape"});if(shape)visible.Add("stretch");if(g.GroupBoundaryRadiusMm>0)visible.Add("boundary");if(g.AllowTopExtension)visible.Add("extension");}
        if(ring)visible.UnionWith(new[]{"radius","rstep","count"});
        if(rad){visible.UnionWith(new[]{"distribution","inner","startangle","anglemode","count","editshape"});if(!shape)visible.Add("outer");if(g.UseAngleStep)visible.Add("anglestep");if(g.AllowTopExtension)visible.Add("extension");}
        foreach(var (key,p) in _rows){bool show=visible.Contains(key);p.Label.Visible=p.Input.Visible=show;_parameterTable.RowStyles[p.Row].SizeType=show&&key!="source"?SizeType.AutoSize:SizeType.Absolute;_parameterTable.RowStyles[p.Row].Height=show&&key=="source"?48:0;}
    }
    static string BriefSource(string source)
    {
        string text=source.Trim();if(text.Length==0)return "Position or shape needs review";
        int end=text.IndexOfAny(new[]{'。','！','？','；','\r','\n'});if(end>=0)text=text[..(end+1)].Trim();
        return text.Length>26?text[..25]+"…":text;
    }
    void ShowSource(){if(Selected is {} g&&!string.IsNullOrWhiteSpace(g.SourceNote))MessageBox.Show(this,g.SourceNote,"Source Note",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    void RefreshStretch(SlabGroup g)
    {
        _stretch.Items.Clear();_stretch.Items.Add(new SegmentItem(-1,"Select segment"));
        for(int i=0;i<g.ShapeSnapshot.Count;i++){var s=g.ShapeSnapshot[i];if(s.Kind==SegmentKind.Line&&Math.Abs(s.Start.Z-s.End.Z)<.001)_stretch.Items.Add(new SegmentItem(i,$"Segment {i+1} · {Math.Abs(s.End.Rho-s.Start.Rho):0.###}"));}
        _stretch.SelectedItem=_stretch.Items.Cast<SegmentItem>().FirstOrDefault(x=>x.Index==g.StretchSegmentIndex)??_stretch.Items[0];
    }
    void ParametersChanged()
    {
        if(_loading||_busy||Selected is not {} g)return;Checkpoint();
        g.Name=_name.Text;g.Enabled=_enabled.Checked;g.PositionConfirmed=_positionConfirmed.Checked;g.Z=(double)_z.Value;g.AllowTopExtension=_extension.Checked;
        if(g.Kind==SlabGroupKind.Orthogonal)
        {
            g.DirectionDeg=(double)_direction.Value;g.UseEndOffset=_rangeMode.SelectedIndex==0;g.FitToBoundary=_fit.Checked;g.AlongOffset=(double)_along.Value;g.GroupBoundaryRadiusMm=(double)_boundaryRadius.Value;
        }
        else if(g.Kind==SlabGroupKind.Ring){g.Radius=(double)_radius.Value;g.RadiusStep=(double)_radiusStep.Value;}
        else
        {
            g.InnerRadius=(double)_inner.Value;if(g.ShapeSnapshot.Count==0)g.OuterRadius=(double)_outer.Value;g.StartAngle=(double)_startAngle.Value;g.AngleStep=(double)_angleStep.Value;g.UseAngleStep=_angleMode.SelectedIndex==1;
            if(SlabGeometry.TryGetRadialCount(g,out var count,out _)){_loading=true;Set(_count,count);_loading=false;_selectedMember=Math.Min(_selectedMember,count-1);}
            _fitSelectedAfterCheck=true;
        }
        RefreshRows(g);Changed();
    }
    void DistributionChanged()
    {
        if(_loading||_busy||Selected is not {} g||g.Kind==SlabGroupKind.Ring||_distribution.SelectedIndex<0)return;
        var target=_distribution.SelectedIndex==0?SlabGroupKind.Radial:SlabGroupKind.Orthogonal;if(g.Kind==target)return;
        Checkpoint();string oldKindName=KindName(g.Kind);
        if(!SlabGeometry.TryChangeDistributionKind(_document,g,target,out var error)){RefreshInspector();_summary.Text=error;return;}
        // Rename only the untouched automatic label; source drawing names stay user-owned.
        if(g.Name.StartsWith(oldKindName+" ",StringComparison.Ordinal)&&int.TryParse(g.Name[(oldKindName.Length+1)..],out var number))g.Name=KindName(g.Kind)+" "+number;
        _selectedMember=-1;_fitSelectedAfterCheck=true;RefreshAll();Changed();
    }
    void RangeChanged()
    {
        if(_loading||_busy||Selected is not {Kind:SlabGroupKind.Orthogonal} g)return;Checkpoint();
        g.StartOffset=(double)_start.Value;g.EndOffset=(double)_end.Value;g.Spacing=(double)_spacing.Value;g.UseEndOffset=true;
        // Explicit range edits opt into the paper's fixed-end / variable-middle distribution.
        // Loading a legacy count group never calls this method or changes its saved parameters.
        if(!g.FitToBoundary)EnableBoundaryFit(g);
        if(SlabGeometry.TryGetOrthogonalRange(g,out var n,out _,out _)){g.Count=n;_selectedMember=Math.Min(_selectedMember,n-1);}
        RefreshInspector();Changed();
    }
    static void EnableBoundaryFit(SlabGroup g)
    {
        if(g.ShapeSnapshot.Count>0)
        {
            int index=g.StretchSegmentIndex>=0?g.StretchSegmentIndex:SlabGeometry.DefaultStretchSegmentIndex(g.ShapeSnapshot);
            SlabGeometry.TryNormalizeGroupShape(g,index,out _,out _);
            // An ambiguous/missing middle remains an invalid fitted path until explicitly selected.
            // It must not silently produce an array of fixed-length bars.
        }
        g.FitToBoundary=true;
    }
    void FitChanged()
    {
        if(_loading||_busy||Selected is not {Kind:SlabGroupKind.Orthogonal} g)return;Checkpoint();
        if(_fit.Checked&&!g.FitToBoundary)EnableBoundaryFit(g);else g.FitToBoundary=_fit.Checked;
        RefreshInspector();Changed();
    }
    void ShapeChanged()
    {
        if(_loading||_busy||Selected is not {} g)return;Checkpoint();if(!ApplyShape(g,_shape.SelectedItem as ShapeTemplate,-1))return;RefreshInspector();Changed();
    }
    void StretchChanged()
    {
        if(_loading||_busy||Selected is not {Kind:SlabGroupKind.Orthogonal} g||_stretch.SelectedItem is not SegmentItem s)return;Checkpoint();
        g.StretchSegmentIndex=s.Index;g.ReferenceSpan=0;
        if(g.FitToBoundary)SlabGeometry.TryNormalizeGroupShape(g,s.Index,out _,out _);
        else if(SlabGeometry.TryNormalizeShape(g.ShapeSnapshot,s.Index,out _,out var span,out _))g.ReferenceSpan=span;
        RefreshInspector();Changed();
    }
    bool ApplyShape(SlabGroup g,ShapeTemplate? shape,int preferred)
    {
        if(shape!=null){if(!ShapeDefaults.TryPrepare(shape,out var prepared,out var error)){_summary.Text=error+" · Edit the shape";return false;}shape=prepared;}
        g.ShapeTemplateId=shape?.Id??"";g.ShapeSnapshot=shape==null?new():ShapePlacement.CopySegments(shape.Segments);g.StretchSegmentIndex=-1;g.ReferenceSpan=0;
        if(shape==null||g.Kind!=SlabGroupKind.Orthogonal)return true;
        int index=preferred>=0?preferred:SlabGeometry.DefaultStretchSegmentIndex(shape.Segments);
        g.StretchSegmentIndex=index;
        if(g.FitToBoundary)SlabGeometry.TryNormalizeGroupShape(g,index,out _,out _);
        else if(SlabGeometry.TryNormalizeShape(shape.Segments,index,out _,out var span,out _))g.ReferenceSpan=span;
        return true;
    }
    void ApplyCover()
    {
        if(_busy)return;Checkpoint();_document.CheckSettings.TopCoverMm=(double)_coverTop.Value;_document.CheckSettings.BottomCoverMm=(double)_coverBottom.Value;_document.CheckSettings.SideCoverMm=(double)_coverSide.Value;_document.CheckSettings.CoverConfirmed=true;_coverDirty=false;Changed();
    }
    void UpdateCoverCaption()=>_coverToggle.Text="Cover"+(_coverExpanded?" ▾":" ▸");
    void UpdateCollisionCaption(){_collisionLabel.Text=_document.CheckSettings.AllowCollisionViolation?"Reference mm":"Tolerance mm";_collisionTolerance.AccessibleName=_collisionLabel.Text;}
    void AssignType(){if(_busy||Selected is not {} g||CurrentType is not {} type)return;Checkpoint();g.TypeId=type.Id;Changed();}
    void EditType(bool add)
    {
        if(_busy)return;var old=CurrentType;if(!add&&old==null)return;
        using var dialog=new Form{Text=add?"Add Bar Type":"Bar Types",Font=Font,StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(300,145),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        var grade=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=155};grade.Items.AddRange(ReviewPresets.Grades);grade.SelectedItem=old?.Grade;if(grade.SelectedIndex<0)grade.SelectedIndex=0;
        var diameter=Number(3,100);diameter.Width=155;diameter.Dock=DockStyle.None;Set(diameter,old?.DiameterMm??16);
        var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(12)};table.Controls.Add(new Label{Text="Grade",AutoSize=true},0,0);table.Controls.Add(grade,1,0);table.Controls.Add(new Label{Text="Diameter mm",AutoSize=true},0,1);table.Controls.Add(diameter,1,1);
        var ok=new Button{Text="OK",DialogResult=DialogResult.OK,AutoSize=true};table.Controls.Add(ok,1,2);dialog.AcceptButton=ok;dialog.Controls.Add(table);if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        Checkpoint();var type=add?new ReviewBarType():old!;type.Grade=grade.SelectedItem!.ToString()!;type.Name=type.Grade;type.DiameterMm=(double)diameter.Value;if(add)_document.Types.Add(type);RefreshAll();_types.SelectedItem=type;Changed();
    }
    void EditShape(bool fromSelection)
    {
        if(_busy)return;var old=fromSelection?_shapes.SelectedItem as ShapeTemplate:null;
        if(fromSelection&&old==null){_summary.Text="Select a shape first.";return;}
        using var dialog=new ShapeEditorForm(old);if(dialog.ShowDialog(this)!=DialogResult.OK||dialog.ResultTemplate is not {} result)return;
        Checkpoint();result.Id=Guid.NewGuid().ToString("N");_document.Shapes.Add(result);RefreshAll();_shapes.SelectedItem=result;Changed();
    }
    void EditGroupShape()
    {
        if(_busy||Selected is not {} g||g.Kind==SlabGroupKind.Ring)return;
        var source=g.ShapeSnapshot.Count>0?new ShapeTemplate{Name=g.Name+" Shape",Segments=ShapePlacement.CopySegments(g.ShapeSnapshot)}:new ShapeTemplate{Name="Straight",Segments=new(){new(){Kind=SegmentKind.Line,Start=new(-500,0),End=new(500,0)}}};
        using var dialog=new ShapeEditorForm(source);if(dialog.ShowDialog(this)!=DialogResult.OK||dialog.ResultTemplate is not {} result)return;
        Checkpoint();result.Id=Guid.NewGuid().ToString("N");_document.Shapes.Add(result);ApplyShape(g,result,-1);RefreshAll();Changed();
    }
    void DeleteSelected(){if(_busy||Selected is not {} g)return;Checkpoint();_document.Groups.Remove(g);_selectedId=null;_selectedMember=-1;RefreshAll();Changed();}
    void ClearPlacements()
    {
        if(_busy||_document.Groups.Count==0)return;
        Checkpoint();_document.Groups.Clear();_selectedId=null;_selectedMember=-1;_fitSelectedAfterCheck=false;
        _validation=null;_pendingBars.Clear();_pendingIssues.Clear();_pendingAccepted.Clear();
        SetTool("select");RefreshAll();Changed();SetValidation(SlabGeometry.Check(ExportDocument()));
        _summary.Text="Placed bars cleared · Ctrl+Z to undo";
    }
    void DuplicateSelected()
    {
        if(_busy||Selected is not {} g)return;Checkpoint();var temp=ExportDocument();var copy=temp.Groups.First(x=>x.Id==g.Id);copy.Id=Guid.NewGuid().ToString("N");copy.Name=g.Name+" Copy";_document.Groups.Add(copy);_selectedId=copy.Id;RefreshAll();Changed();
    }
    void Redraw()
    {
        var selected=Selected;double x=0,y=0,angleA=0,angleB=90,offsetA=0,offsetB=0;
        if(selected?.Kind==SlabGroupKind.Orthogonal)
        {
            int member=Math.Max(0,_selectedMember);
            if(_selectedMember<0&&selected.Spacing>0&&SlabGeometry.TryGetOrthogonalRange(selected,out var count,out _,out _))
                member=(int)Math.Clamp(Math.Round(-selected.StartOffset/selected.Spacing),0,count-1);
            double a=selected.DirectionDeg*Math.PI/180,eta=selected.StartOffset+member*selected.Spacing;
            x=selected.AlongOffset*Math.Cos(a)-eta*Math.Sin(a);y=selected.AlongOffset*Math.Sin(a)+eta*Math.Cos(a);
            offsetA=y;offsetB=-x;
        }
        else if(selected?.Kind==SlabGroupKind.Radial)
        {
            int member=Math.Max(0,_selectedMember);int count=SlabGeometry.TryGetRadialCount(selected,out var n,out _)?n:Math.Max(1,selected.Count);
            member=Math.Min(member,count-1);angleA=selected.StartAngle+member*(selected.UseAngleStep?selected.AngleStep:360d/count);angleB=angleA+90;offsetB=-selected.InnerRadius;
        }
        if(!_section.IsDragGesture){_section.SectionAngleDeg=angleA;_section.SectionOffsetMm=offsetA;}
        if(!_sectionOrthogonal.IsDragGesture){_sectionOrthogonal.SectionAngleDeg=angleB;_sectionOrthogonal.SectionOffsetMm=offsetB;}
        _plan.SectionAngleDeg=angleA;_plan.SectionOffsetMm=offsetA;_plan.ShowOrthogonalCut=true;_plan.OrthogonalSectionAngleDeg=angleB;_plan.OrthogonalSectionOffsetMm=offsetB;
        foreach(var canvas in new[]{_plan,_section,_sectionOrthogonal})
        {
            canvas.RadiusMm=_document.Host.OuterRadiusMm;canvas.HeightMm=_document.Host.HeightMm;canvas.SectionBoundary=_document.Host.SectionBoundary;canvas.TopCoverMm=_document.CheckSettings.TopCoverMm;canvas.BottomCoverMm=_document.CheckSettings.BottomCoverMm;canvas.SideCoverMm=_document.CheckSettings.SideCoverMm;
            canvas.Bars=_dragBars??(_validation?.Bars??new()).Concat(_pendingBars).ToList();canvas.ExistingBars=_document.ExistingBars;canvas.SelectedGroupId=_selectedId;canvas.SelectedMemberIndex=_selectedMember;
            canvas.PendingGroupIds=_document.Groups.Where(g=>!g.Enabled).Select(g=>g.Id).ToHashSet();
            canvas.AcceptedCollisionGroupIds=(_validation?.AcceptedCollisions.Concat(_pendingAccepted).SelectMany(x=>new[]{x.DraftId,x.OtherDraftId})??Enumerable.Empty<string>()).Where(x=>x.Length>0).ToHashSet();
            canvas.AcceptedCoverGroupIds=(_validation?.AcceptedCoverViolations.SelectMany(x=>new[]{x.DraftId,x.OtherDraftId})??Enumerable.Empty<string>()).Where(x=>x.Length>0).ToHashSet();
            canvas.ProblemGroupIds=(_validation?.Issues.Concat(_pendingIssues).SelectMany(x=>new[]{x.DraftId,x.OtherDraftId})??Enumerable.Empty<string>()).Where(x=>x.Length>0).ToHashSet();canvas.Invalidate();
        }
    }
    void DrawGroup(object? sender,DrawItemEventArgs e)
    {
        if(e.Index<0||_groups.Items[e.Index] is not SlabGroup g)return;e.DrawBackground();bool bad=_validation?.HasIssue(g.Id)==true;
        if((e.State&DrawItemState.Selected)!=0){using var fill=new SolidBrush(Color.FromArgb(222,239,248));e.Graphics.FillRectangle(fill,e.Bounds);}
        int n=g.Enabled?_validation?.Bars.Count(x=>x.DraftId==g.Id)??0:EstimatedCount(g);string label=$"{g.Name} · {n} bars";if(!g.Enabled)label="[Review] "+label;
        bool accepted=_validation!=null&&_validation.AcceptedCollisions.Concat(_validation.AcceptedCoverViolations).Any(x=>x.DraftId==g.Id||x.OtherDraftId==g.Id);
        var color=bad?Color.FromArgb(190,49,48):accepted?Color.FromArgb(151,105,37):Color.FromArgb(38,60,76);
        TextRenderer.DrawText(e.Graphics,label,e.Font,e.Bounds,color,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);e.DrawFocusRectangle();
    }
    void WriteDraft()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);string temp=_savePath+".tmp";File.WriteAllText(temp,_document.ToJson(),new UTF8Encoding(false));File.Move(temp,_savePath,true);
    }
    static int EstimatedCount(SlabGroup g)
    {
        double value=g.Kind==SlabGroupKind.Orthogonal&&g.UseEndOffset?g.Spacing>0?Math.Floor((g.EndOffset-g.StartOffset)/g.Spacing+1e-9)+1:0:g.Kind==SlabGroupKind.Radial&&g.UseAngleStep?g.AngleStep>0?Math.Ceiling(360/g.AngleStep-1e-10):0:g.Count;
        return double.IsFinite(value)?(int)Math.Clamp(value,0,5000):0;
    }
    sealed record SegmentItem(int Index,string Text){public override string ToString()=>Text;}
}
