using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CaissonWallUi;

public sealed class WallReviewForm : Form
{
    static string DraftFolder => RuntimePaths.WallDrafts;
    ReviewDocument _document;
    ReviewSelection _selection=new();
    readonly Stack<string> _undo=new();
    readonly ReviewCanvas _plan=new(true),_section=new(false);
    readonly ListBox _types=List(),_drafts=List();
    readonly ShapeLibraryList _shapes=new();
    readonly GroupBox _positionGroup=new(){Text="Position · mm",Dock=DockStyle.Fill,AutoSize=true,Padding=new Padding(7)};
    readonly NumericUpDown _positionX=PositionNumber("x →"),_positionY=PositionNumber("y ↓");
    readonly Button _applyPosition=new(){Text="Apply",AutoSize=true,Dock=DockStyle.Fill,Enabled=false};
    readonly Label _positionHint=new(){AutoSize=true,Dock=DockStyle.Fill,ForeColor=Color.FromArgb(172,71,52),MaximumSize=new Size(254,0)};
    string? _positionTargetId;
    readonly GroupBox _groupGroup=new(){Text="Distribution",Dock=DockStyle.Fill,AutoSize=true,Padding=new Padding(7)};
    readonly CheckBox _groupEnabled=new(){Text="Enable",AutoSize=true};
    readonly ComboBox _groupAxis=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,FormattingEnabled=true};
    readonly ComboBox _groupMode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,FormattingEnabled=true};
    readonly NumericUpDown _groupSpacing=PositionNumber("Spacing mm"),_groupCount=GroupNumber("Count",1,1000,1,0),
        _groupStartAngle=GroupNumber("Start °",-36000,36000,0,3),_groupAngleStep=GroupNumber("Step °",0.001m,360,30,3);
    readonly Button _applyGroup=new(){Text="Apply",AutoSize=true,Dock=DockStyle.Fill};
    readonly Label _groupHint=new(){AutoSize=true,Dock=DockStyle.Fill,MaximumSize=new Size(254,0)};
    readonly Label _axisLabel=LabelFor("Direction"),_modeLabel=LabelFor("Mode"),_spacingLabel=LabelFor("Spacing"),
        _countLabel=LabelFor("Count"),_startLabel=LabelFor("Start °"),_angleLabel=LabelFor("Step °");
    string? _groupTargetId;
    readonly Dictionary<ReviewTool,Button> _toolButtons=new();
    readonly System.Windows.Forms.Timer _saveTimer=new(){Interval=450};
    readonly System.Windows.Forms.Timer _checkTimer=new(){Interval=180};
    readonly Button _coverToggle=new(){Text="Cover",AutoSize=true,FlatStyle=FlatStyle.Flat};
    readonly FlowLayoutPanel _coverPanel=Flow();
    readonly NumericUpDown _coverInner=CoverNumber("Inner cover"),_coverOuter=CoverNumber("Outer cover"),_coverTop=CoverNumber("Top cover"),_coverBottom=CoverNumber("Bottom cover");
    readonly Button _applyCover=new(){Text="Apply",AutoSize=true};
    readonly CheckBox _allowCollisionViolation=new(){Text="Collisions: warn only",AutoSize=true};
    readonly Label _checkSummary=new(){Text="Checking…",AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(4,10,4,3)};
    readonly Label _issueDetails=new(){AutoSize=true,Dock=DockStyle.Fill,MaximumSize=new Size(254,0),ForeColor=Color.FromArgb(190,49,48),Padding=new Padding(4,4,4,4)};
    readonly Button _build=new(){Text="Create Rebar",AutoSize=true,Enabled=false,Padding=new Padding(14,4,14,4)};
    readonly CheckBox _allowTopExtension=new(){Text="Allow top extension",AutoSize=true};
    readonly string _draftFolder;
    ValidationReport? _validation;
    ValidationIssue? _inspectedIssue;
    int _issueIndex=-1;
    bool _checking,_building,_coverDirty,_checkRunning;
    long _checkRevision;
    readonly string _savePath;
    bool _refreshing;
    public string DraftPath=>_savePath;
    public string ExportDraftJson()=>_document.ToJson();
    public ValidationReport? LatestValidation=>_validation;
    public event Action? BuildRequested;

    public WallReviewForm(HostContext context,WallCheckSettings? settings=null,IEnumerable<ExistingBarPath>? existingBars=null,string? draftDirectory=null)
    {
        Text="Wall Reinforcement";Name="CaissonWallUiReview";
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        Font=new Font("Segoe UI",9);StartPosition=FormStartPosition.CenterScreen;
        ClientSize=new Size(1480,860);MinimumSize=new Size(1080,690);BackColor=Color.FromArgb(246,248,250);KeyPreview=true;
        _draftFolder=draftDirectory??DraftFolder;
        _document=LoadOrInitial(context,_draftFolder);ReviewPresets.Ensure(_document);
        if(settings!=null)_document.CheckSettings=settings;
        if(existingBars!=null)_document.ExistingBars=existingBars.ToList();
        _savePath=Path.Combine(_draftFolder,$"wall_ui_{DateTime.Now:yyyyMMdd_HHmmss}_{_document.SessionId[..8]}.json");
        BuildInterface();BindEvents();RefreshAll();
        _types.SelectedItem=_document.Types.FirstOrDefault(t=>t.Grade=="HRB400"&&t.DiameterMm==16)??_document.Types.First();
        Shown+=(_,_)=>{ConstrainToScreen();_plan.Fit();_section.Fit();SaveDraft(false);ScheduleChecks();};
        FormClosing+=(_,e)=>{
            _saveTimer.Stop();_checkTimer.Stop();_checkRevision++;
            try{WriteDraft();}
            catch(Exception ex){
                if(e.CloseReason is CloseReason.FormOwnerClosing or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall)return;
                e.Cancel=MessageBox.Show(this,"Save failed. Keep the window open to retry?\n"+ex.Message,"Save failed",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
            }
        };
        FormClosed+=(_,_)=>{_saveTimer.Dispose();_checkTimer.Dispose();};
    }
    static ListBox List()=>new(){Dock=DockStyle.Fill,IntegralHeight=false,BorderStyle=BorderStyle.FixedSingle};
    static Label LabelFor(string text)=>new(){Text=text,AutoSize=true,Padding=new Padding(3,7,2,0)};
    static NumericUpDown PositionNumber(string name)=>new(){Name=name,AccessibleName=name,DecimalPlaces=3,Increment=1,Minimum=-1000000000,Maximum=1000000000,Dock=DockStyle.Fill,Margin=new Padding(3,4,3,4)};
    static NumericUpDown GroupNumber(string name,decimal min,decimal max,decimal value,int decimals)=>new(){Name=name,AccessibleName=name,DecimalPlaces=decimals,Minimum=min,Maximum=max,Value=value,Dock=DockStyle.Fill,Margin=new Padding(3,4,3,4)};
    static NumericUpDown CoverNumber(string name)=>new(){Name=name,AccessibleName=name,DecimalPlaces=3,Increment=1,Minimum=0,Maximum=1000,Width=78,Margin=new Padding(2,4,10,3)};
    static FlowLayoutPanel Flow()=>new(){Dock=DockStyle.Fill,AutoSize=true,WrapContents=false,Padding=new Padding(4),Margin=new Padding(0)};
    static Button ButtonFor(string text,EventHandler action){var b=new Button{Text=text,UseMnemonic=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=new Padding(3),Padding=new Padding(6,3,6,3)};b.Click+=action;return b;}
    void ConstrainToScreen(){
        var a=Screen.FromControl(this).WorkingArea;int w=Math.Min(Width,a.Width-24),h=Math.Min(Height,a.Height-24);
        MinimumSize=new Size(Math.Min(MinimumSize.Width,w),Math.Min(MinimumSize.Height,h));
        Bounds=new Rectangle(a.Left+(a.Width-w)/2,a.Top+(a.Height-h)/2,w,h);
    }
    void BuildInterface()
    {
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(12)};Controls.Add(root);
        root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.AutoSize));root.RowStyles.Add(new(SizeType.Percent,100));root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(new Label{Text=$"Host: {_document.Host.HostId}",Dock=DockStyle.Fill,AutoSize=true,Padding=new Padding(4,2,4,6)},0,0);
        var tools=Flow();AddTool(tools,"Select",ReviewTool.Select);AddTool(tools,"Ring",ReviewTool.Ring);AddTool(tools,"Insert Shape",ReviewTool.InsertShape);tools.Controls.Add(ButtonFor("Clear All",(_,_)=>ClearPlacements()));tools.Controls.Add(_coverToggle);root.Controls.Add(tools,0,1);
        _coverPanel.Visible=false;_coverPanel.Controls.Add(LabelFor("Clear cover · mm"));
        foreach(var pair in new[]{("Inner",_coverInner),("Outer",_coverOuter),("Top",_coverTop),("Bottom",_coverBottom)}){_coverPanel.Controls.Add(LabelFor(pair.Item1));_coverPanel.Controls.Add(pair.Item2);}
        _coverPanel.Controls.Add(_applyCover);_coverPanel.Controls.Add(_allowCollisionViolation);root.Controls.Add(_coverPanel,0,2);
        var work=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=new Padding(0)};
        work.ColumnStyles.Add(new(SizeType.Absolute,230));work.ColumnStyles.Add(new(SizeType.Percent,50));work.ColumnStyles.Add(new(SizeType.Percent,50));work.ColumnStyles.Add(new(SizeType.Absolute,284));root.Controls.Add(work,0,3);
        var side=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0,0,8,0)};
        side.RowStyles.Add(new(SizeType.Percent,40));side.RowStyles.Add(new(SizeType.Percent,60));
        side.Controls.Add(ListGroup("Bar Types",_types,ButtonFor("Add",(_,_)=>EditType(true)),ButtonFor("Edit",(_,_)=>EditType(false)),ButtonFor("Apply",(_,_)=>AssignType())),0,0);
        side.Controls.Add(ListGroup("Shape Library",_shapes,ButtonFor("New",(_,_)=>EditShape(false)),ButtonFor("Edit & Save As",(_,_)=>EditShape(true))),0,1);
        work.Controls.Add(side,0,0);
        _plan.Margin=new Padding(0,4,5,0);_section.Margin=new Padding(5,4,5,0);
        work.Controls.Add(_plan,1,0);work.Controls.Add(_section,2,0);
        var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=new Padding(5,0,0,0)};
        right.RowStyles.Add(new(SizeType.Percent,100));right.RowStyles.Add(new(SizeType.AutoSize));right.RowStyles.Add(new(SizeType.AutoSize));right.RowStyles.Add(new(SizeType.AutoSize));
        right.Controls.Add(ListGroup("Placed Bars",_drafts),0,0);
        right.Controls.Add(_issueDetails,0,1);
        var position=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,RowCount=5,Margin=new Padding(0)};
        position.ColumnStyles.Add(new(SizeType.Absolute,42));position.ColumnStyles.Add(new(SizeType.Percent,100));
        position.Controls.Add(LabelFor("x"),0,0);position.Controls.Add(_positionX,1,0);
        position.Controls.Add(LabelFor("y"),0,1);position.Controls.Add(_positionY,1,1);
        position.Controls.Add(_applyPosition,1,2);position.Controls.Add(_positionHint,0,3);position.SetColumnSpan(_positionHint,2);
        position.Controls.Add(_allowTopExtension,0,4);position.SetColumnSpan(_allowTopExtension,2);
        _positionGroup.Controls.Add(position);right.Controls.Add(_positionGroup,0,2);work.Controls.Add(right,3,0);
        BuildGroupPanel();right.Controls.Add(_groupGroup,0,3);
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2};footer.ColumnStyles.Add(new(SizeType.Percent,100));footer.ColumnStyles.Add(new(SizeType.AutoSize));footer.Controls.Add(_checkSummary,0,0);footer.Controls.Add(_build,1,0);root.Controls.Add(footer,0,4);
        var menu=new ContextMenuStrip();var editShapeItem=menu.Items.Add("Edit Shape",null,(_,_)=>EditInsertedShape());menu.Items.Add("Delete",null,(_,_)=>DeleteSelected());menu.Opening+=(_,_)=>editShapeItem.Enabled=SelectedDraft?.Kind==DraftKind.Profile;_drafts.ContextMenuStrip=menu;
        var tips=new ToolTip();tips.SetToolTip(_shapes,"Double-click a shape to edit and save a new copy.");tips.SetToolTip(_drafts,"Select a bar. Delete removes it; Ctrl+Z undoes.");
        tips.SetToolTip(_section,"Click to insert; Esc to finish. Wheel to zoom, middle button to pan, double-click to fit.");
        tips.SetToolTip(_groupSpacing,"Millimetres. Positive follows the axis; negative reverses it.");tips.SetToolTip(_groupCount,"Count includes the original bar; preview limit: 1000.");
        tips.SetToolTip(_groupAngleStep,"Counterclockwise around the wall axis. The 360° endpoint is not repeated.");
        tips.SetToolTip(_types,"Select a bar, then a type to assign it. Both views show the actual diameter.");
        tips.SetToolTip(_checkSummary,"Click to locate the next issue. Red marks collisions, cover or path issues.");
        tips.SetToolTip(_build,"Create all validated bars in the Revit model.");
    }
    void BuildGroupPanel()
    {
        _groupAxis.Items.AddRange(new object[]{RingArrayAxis.X,RingArrayAxis.Y});_groupAxis.SelectedItem=RingArrayAxis.Y;
        _groupMode.Items.AddRange(new object[]{ProfileArrayMode.Count,ProfileArrayMode.AngleStep});_groupMode.SelectedItem=ProfileArrayMode.Count;
        _groupAxis.Format+=(_,e)=>{if(e.ListItem is RingArrayAxis axis)e.Value=axis==RingArrayAxis.X?"Along x":"Along y";};
        _groupMode.Format+=(_,e)=>{if(e.ListItem is ProfileArrayMode mode)e.Value=mode==ProfileArrayMode.Count?"By count":"By angle";};
        _groupSpacing.Value=150;
        var table=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,RowCount=9,Margin=new Padding(0)};
        table.ColumnStyles.Add(new(SizeType.Absolute,98));table.ColumnStyles.Add(new(SizeType.Percent,100));
        for(int i=0;i<9;i++)table.RowStyles.Add(new(SizeType.AutoSize));
        table.Controls.Add(_groupEnabled,0,0);table.SetColumnSpan(_groupEnabled,2);
        table.Controls.Add(_axisLabel,0,1);table.Controls.Add(_groupAxis,1,1);
        table.Controls.Add(_modeLabel,0,2);table.Controls.Add(_groupMode,1,2);
        table.Controls.Add(_spacingLabel,0,3);table.Controls.Add(_groupSpacing,1,3);
        table.Controls.Add(_startLabel,0,4);table.Controls.Add(_groupStartAngle,1,4);
        table.Controls.Add(_countLabel,0,5);table.Controls.Add(_groupCount,1,5);
        table.Controls.Add(_angleLabel,0,6);table.Controls.Add(_groupAngleStep,1,6);
        table.Controls.Add(_applyGroup,1,7);table.Controls.Add(_groupHint,0,8);table.SetColumnSpan(_groupHint,2);
        _groupGroup.Controls.Add(table);
    }
    static GroupBox ListGroup(string title,ListBox list,params Button[] buttons){
        var group=new GroupBox{Text=title,Dock=DockStyle.Fill,Padding=new Padding(7)};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=buttons.Length>0?2:1};layout.RowStyles.Add(new(SizeType.Percent,100));layout.Controls.Add(list,0,0);
        if(buttons.Length>0){
            layout.RowStyles.Add(new(SizeType.AutoSize));
            var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=buttons.Length,RowCount=1,Height=36,Margin=new Padding(0)};
            for(int i=0;i<buttons.Length;i++){
                float share=buttons.Length==2&&buttons[1].Text=="Edit & Save As"?(i==0?40:60):100f/buttons.Length;
                row.ColumnStyles.Add(new(SizeType.Percent,share));buttons[i].AutoSize=false;buttons[i].Dock=DockStyle.Fill;buttons[i].Padding=new Padding(0);row.Controls.Add(buttons[i],i,0);
            }
            layout.Controls.Add(row,0,1);
        }
        group.Controls.Add(layout);return group;
    }
    void AddTool(FlowLayoutPanel row,string name,ReviewTool tool){var b=ButtonFor(name,(_,_)=>SetTool(tool));_toolButtons[tool]=b;row.Controls.Add(b);}
    ReviewDraft? SelectedDraft=>_document.Drafts.FirstOrDefault(d=>d.Id==_selection.DraftId);
    ShapeTemplate? CurrentShape=>_shapes.SelectedItem as ShapeTemplate;
    ReviewBarType CurrentType=>(_types.SelectedItem as ReviewBarType)??_document.Types[0];
    void BindEvents()
    {
        _plan.SelectionChanged+=Select;_section.SelectionChanged+=Select;_section.SketchClicked+=InsertAt;
        _plan.FinishRequested+=()=>SetTool(ReviewTool.Select);_section.FinishRequested+=()=>SetTool(ReviewTool.Select);
        _plan.DeleteRequested+=DeleteSelected;_section.DeleteRequested+=DeleteSelected;
        _drafts.SelectedIndexChanged+=(_,_)=>{if(!_refreshing&&_drafts.SelectedItem is ReviewDraft d)Select(new(){DraftId=d.Id});};
        _drafts.DoubleClick+=(_,_)=>EditInsertedShape();
        _drafts.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Delete){DeleteSelected();e.SuppressKeyPress=true;}};
        _shapes.SelectedIndexChanged+=(_,_)=>{if(!_refreshing)Redraw();};
        _shapes.DoubleClick+=(_,_)=>EditShape(true);
        _types.SelectedIndexChanged+=(_,_)=>{if(_refreshing)return;if(_document.ActiveTool==ReviewTool.Select&&SelectedDraft is {} d&&_types.SelectedItem is ReviewBarType t&&d.TypeId!=t.Id)AssignType();else Redraw();};
        _drafts.FormattingEnabled=true;_drafts.Format+=(sender,e)=>{if(e.ListItem is ReviewDraft d&&d.Group.Enabled&&GroupGeometry.TryMembers(d,out var members,out _))e.Value=$"{d.Name} × {members.Count}";};
        _positionX.ValueChanged+=(_,_)=>PositionEdited();_positionY.ValueChanged+=(_,_)=>PositionEdited();
        _applyPosition.Click+=(_,_)=>ApplyPosition();
        _positionX.KeyDown+=PositionKeyDown;_positionY.KeyDown+=PositionKeyDown;
        _groupEnabled.CheckedChanged+=(_,_)=>GroupEdited();_groupAxis.SelectedIndexChanged+=(_,_)=>GroupEdited();_groupMode.SelectedIndexChanged+=(_,_)=>GroupEdited();
        foreach(var input in new[]{_groupSpacing,_groupCount,_groupStartAngle,_groupAngleStep}){input.ValueChanged+=(_,_)=>GroupEdited();input.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){ApplyGroup();e.Handled=true;e.SuppressKeyPress=true;}};}
        _applyGroup.Click+=(_,_)=>ApplyGroup();
        _allowTopExtension.CheckedChanged+=(_,_)=>{if(_refreshing||SelectedDraft is not {} d)return;Checkpoint();d.AllowTopExtension=_allowTopExtension.Checked;Changed();};
        _coverToggle.Click+=(_,_)=>{_coverPanel.Visible=!_coverPanel.Visible;};
        _applyCover.Click+=(_,_)=>ApplyCover();
        _allowCollisionViolation.CheckedChanged+=(_,_)=>{if(_refreshing)return;Checkpoint();_document.CheckSettings.AllowCollisionViolation=_allowCollisionViolation.Checked;Changed();};
        foreach(var input in new[]{_coverInner,_coverOuter,_coverTop,_coverBottom})input.ValueChanged+=(_,_)=>{if(!_refreshing){_coverDirty=true;_build.Enabled=false;_checkSummary.Text="Apply cover changes";}};
        foreach(var input in new[]{_coverInner,_coverOuter,_coverTop,_coverBottom})input.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){ApplyCover();e.Handled=true;e.SuppressKeyPress=true;}};
        _checkSummary.Click+=(_,_)=>SelectNextIssue();
        _build.Click+=(_,_)=>RequestBuild();
        _checkTimer.Tick+=async(_,_)=>{_checkTimer.Stop();await RunChecksAsync();};
        _plan.DoubleClick+=(_,_)=>_plan.Fit();_section.DoubleClick+=(_,_)=>{if(_document.ActiveTool==ReviewTool.Select)_section.Fit();};
        _saveTimer.Tick+=(_,_)=>{_saveTimer.Stop();SaveDraft(false);};
        KeyDown+=(_,e)=>{
            if(e.KeyCode==Keys.Escape){SetTool(ReviewTool.Select);e.SuppressKeyPress=true;}
            if(e.Control&&e.KeyCode==Keys.Z){Undo();e.SuppressKeyPress=true;}
            if(e.Control&&e.KeyCode==Keys.S){SaveDraft(true);e.SuppressKeyPress=true;}
        };
    }
    void Select(ReviewSelection selected)
    {
        _inspectedIssue=null;
        _selection=new(){DraftId=selected.DraftId,MemberIndex=selected.MemberIndex};SetTool(ReviewTool.Select);
        if(SelectedDraft is {} d){_types.SelectedItem=_document.Types.FirstOrDefault(t=>t.Id==d.TypeId);if(d.ShapeTemplateId!=null)_shapes.SelectedItem=_document.Shapes.FirstOrDefault(s=>s.Id==d.ShapeTemplateId);}
        RefreshAll();
    }
    void RefreshAll()
    {
        _refreshing=true;string? typeId=SelectedDraft?.TypeId??(_types.SelectedItem as ReviewBarType)?.Id,shapeId=CurrentShape?.Id;
        _types.Items.Clear();
        foreach(var t in _document.Types.OrderBy(t=>string.IsNullOrWhiteSpace(t.Grade)?1:0).ThenBy(t=>t.Grade=="HRB400"?0:t.Grade=="HPB300"?1:2).ThenBy(t=>t.Grade).ThenBy(t=>t.DiameterMm))_types.Items.Add(t);
        _types.SelectedItem=_document.Types.FirstOrDefault(t=>t.Id==typeId)??_types.Items.Cast<ReviewBarType>().FirstOrDefault();
        _shapes.Items.Clear();foreach(var s in _document.Shapes.OrderBy(s=>s.Id.StartsWith("preset-shape-")?0:1))_shapes.Items.Add(s);
        _shapes.SelectedItem=_document.Shapes.FirstOrDefault(s=>s.Id==shapeId)??_shapes.Items.Cast<ShapeTemplate>().FirstOrDefault();
        _drafts.Items.Clear();foreach(var d in _document.Drafts)_drafts.Items.Add(d);_drafts.SelectedItem=SelectedDraft;
        foreach(var pair in _toolButtons){pair.Value.BackColor=pair.Key==_document.ActiveTool?Color.FromArgb(216,237,246):SystemColors.Control;pair.Value.FlatStyle=FlatStyle.Flat;pair.Value.FlatAppearance.BorderColor=Color.FromArgb(202,215,225);}
        RefreshPosition();RefreshGroup();RefreshCover();RefreshCheckDisplay();_refreshing=false;Redraw();
    }
    void RefreshPosition()
    {
        var d=SelectedDraft;var point=d?.Kind==DraftKind.Ring?d.RingPoint:d?.InsertionPoint;
        _positionTargetId=d?.Id;_positionGroup.Visible=point!=null;_applyPosition.Enabled=false;_positionHint.Text="";
        if(point==null)return;
        var display=SectionCoordinates.ToDisplay(point,_document.Host.InnerRadiusMm);
        bool wasRefreshing=_refreshing;_refreshing=true;
        try{_positionX.Value=(decimal)display.X;_positionY.Value=(decimal)display.Y;}
        finally{_refreshing=wasRefreshing;}
    }
    void PositionEdited(){if(_refreshing)return;_positionHint.Text="";_applyPosition.Enabled=SelectedDraft?.Id==_positionTargetId&&_positionTargetId!=null;_build.Enabled=false;}
    void PositionKeyDown(object? sender,KeyEventArgs e){if(e.KeyCode!=Keys.Enter)return;ApplyPosition();e.Handled=true;e.SuppressKeyPress=true;}
    void ApplyPosition()
    {
        if(SelectedDraft is not {} d||d.Id!=_positionTargetId)return;
        var at=SectionCoordinates.ToInternal((double)_positionX.Value,(double)_positionY.Value,_document.Host.InnerRadiusMm);
        if(!double.IsFinite(at.Rho)||!double.IsFinite(at.Z)||at.Rho<0||(d.Kind==DraftKind.Ring&&at.Rho==0)){
            _positionHint.Text=d.Kind==DraftKind.Ring?"Ring radius must be greater than 0.":"Insertion point is outside the radial range.";return;
        }
        if(d.Kind==DraftKind.Profile&&d.ShapeSnapshot.Count==0){_positionHint.Text="Shape data is missing.";return;}
        if(d.Kind==DraftKind.Ring&&!GroupGeometry.TryMembers(new ReviewDraft{Kind=DraftKind.Ring,RingPoint=at,Group=d.Group},out _,out string groupError)){_positionHint.Text=groupError;return;}
        var current=d.Kind==DraftKind.Ring?d.RingPoint:d.InsertionPoint;
        if(current!=null&&SketchDrawing.Same(current,at)){_applyPosition.Enabled=false;RefreshCheckDisplay();return;}
        Checkpoint();ShapePlacement.Move(d,at);_document.ActiveTool=ReviewTool.Select;Changed();
    }
    GroupParameters ReadGroup()=>new(){Enabled=_groupEnabled.Checked,RingAxis=_groupAxis.SelectedItem is RingArrayAxis axis?axis:RingArrayAxis.Y,
        RingStepMm=(double)_groupSpacing.Value,Count=(int)_groupCount.Value,ProfileMode=_groupMode.SelectedItem is ProfileArrayMode mode?mode:ProfileArrayMode.Count,
        StartAngleDeg=(double)_groupStartAngle.Value,AngleStepDeg=(double)_groupAngleStep.Value};
    void RefreshGroup()
    {
        var d=SelectedDraft;_groupTargetId=d?.Id;_groupGroup.Visible=d!=null;_applyGroup.Enabled=false;_groupHint.Text="";
        if(d==null)return;
        var p=d.Group;bool wasRefreshing=_refreshing;_refreshing=true;
        try{
            _allowTopExtension.Visible=d.Kind==DraftKind.Profile;_allowTopExtension.Checked=d.AllowTopExtension;
            _groupEnabled.Checked=p.Enabled;_groupAxis.SelectedItem=p.RingAxis;_groupMode.SelectedItem=p.ProfileMode;
            _groupSpacing.Value=(decimal)p.RingStepMm;_groupCount.Value=Math.Clamp(p.Count,1,GroupGeometry.CountLimit);
            _groupStartAngle.Value=(decimal)(p.StartAngleDeg%360);_groupAngleStep.Value=Math.Clamp((decimal)p.AngleStepDeg,_groupAngleStep.Minimum,_groupAngleStep.Maximum);
            UpdateGroupFields();
        }finally{_refreshing=wasRefreshing;}
    }
    void UpdateGroupFields()
    {
        bool ring=SelectedDraft?.Kind==DraftKind.Ring,angle=!ring&&_groupMode.SelectedItem is ProfileArrayMode.AngleStep;
        _axisLabel.Visible=_groupAxis.Visible=ring;_spacingLabel.Visible=_groupSpacing.Visible=ring;
        _modeLabel.Visible=_groupMode.Visible=!ring;_startLabel.Visible=_groupStartAngle.Visible=!ring;
        _countLabel.Visible=_groupCount.Visible=!angle;_angleLabel.Visible=_groupAngleStep.Visible=angle;
        foreach(Control c in new Control[]{_groupAxis,_groupMode,_groupSpacing,_groupCount,_groupStartAngle,_groupAngleStep})c.Enabled=_groupEnabled.Checked;
        if(SelectedDraft is not {} d)return;
        bool valid=GroupGeometry.TryMembers(d,ReadGroup(),out var members,out string error);
        _groupHint.ForeColor=valid?Color.FromArgb(94,112,125):Color.FromArgb(172,71,52);
        _groupHint.Text=valid?(_groupEnabled.Checked?$"{members.Count} bars · includes original":"Single bar"):error;
    }
    void GroupEdited(){if(_refreshing)return;UpdateGroupFields();_applyGroup.Enabled=SelectedDraft?.Id==_groupTargetId&&_groupTargetId!=null;_build.Enabled=false;}
    void ApplyGroup()
    {
        if(SelectedDraft is not {} d||d.Id!=_groupTargetId)return;
        var next=ReadGroup();
        if(!GroupGeometry.TryMembers(d,next,out _,out string error)){_groupHint.Text=error;_groupHint.ForeColor=Color.FromArgb(172,71,52);return;}
        var options=ReviewDocument.JsonOptions;
        if(System.Text.Json.JsonSerializer.Serialize(d.Group,options)==System.Text.Json.JsonSerializer.Serialize(next,options)){_applyGroup.Enabled=false;RefreshCheckDisplay();return;}
        Checkpoint();d.Group=next;_document.ActiveTool=ReviewTool.Select;Changed();_plan.Fit();_section.Fit();
    }
    void Redraw(){_plan.Document=_section.Document=_document;_plan.Selection=_section.Selection=_selection;_plan.Validation=_section.Validation=_validation;_section.InsertTemplate=CurrentShape is {} shape&&ShapeDefaults.TryPrepare(shape,out var preview,out _)?preview:null;_section.InsertDiameterMm=CurrentType.DiameterMm;_plan.Invalidate();_section.Invalidate();}
    void SetTool(ReviewTool tool){if(tool==ReviewTool.InsertShape&&CurrentShape==null){EditShape(false);return;}if(tool is ReviewTool.Ring or ReviewTool.InsertShape)_selection=new();_document.ActiveTool=tool;_document.PendingPoints.Clear();_document.ActiveProfileId=null;RefreshAll();}
    void InsertAt(SketchPoint p)
    {
        if(p.Rho<0)return;
        if(_document.ActiveTool==ReviewTool.InsertShape){
            if(CurrentShape is not {} original)return;
            if(!ShapeDefaults.TryPrepare(original,out var s,out var error)){_checkSummary.Text=error+" · Edit the shape";return;}
            Checkpoint();
            var next=ShapePlacement.Insert(s,p,CurrentType.Id,$"{ReviewPresets.DisplayName(s)} · {_document.Drafts.Count(x=>x.Kind==DraftKind.Profile)+1}");
            _document.Drafts.Add(next);_selection=new(){DraftId=next.Id};Changed();return;
        }
        if(_document.ActiveTool!=ReviewTool.Ring)return;Checkpoint();
        var d=new ReviewDraft{Name=$"Ring {_document.Drafts.Count(x=>x.Kind==DraftKind.Ring)+1}",Kind=DraftKind.Ring,TypeId=CurrentType.Id,RingPoint=p.Copy(),Placement=new(){Mode="竖向"}};
        _document.Drafts.Add(d);_selection=new(){DraftId=d.Id};Changed();
    }
    void EditShape(bool basedOnSelected)
    {
        if(basedOnSelected&&CurrentShape==null)return;
        ShapeTemplate? start=null;
        if(basedOnSelected)start=ReviewPresets.NewFrom(CurrentShape!,NewShapeName(ReviewPresets.DisplayName(CurrentShape!)));
        using var editor=new ShapeEditorForm(start);
        if(editor.ShowDialog(this)!=DialogResult.OK||editor.ResultTemplate is not {} shape)return;
        Checkpoint();var saved=shape.Copy();saved.Id=start?.Id??Guid.NewGuid().ToString("N");
        _document.Shapes.Add(saved);_selection=new();Changed();_shapes.SelectedItem=saved;SetTool(ReviewTool.InsertShape);
    }
    string NewShapeName(string source){int n=1;string name;do{name=$"{source} ({n++})";}while(_document.Shapes.Any(s=>s.Name==name));return name;}
    void EditInsertedShape()
    {
        if(SelectedDraft is not {Kind:DraftKind.Profile,InsertionPoint:not null} draft)return;
        var template=new ShapeTemplate{Name=NewShapeName(draft.ShapeName??"Shape"),Segments=ShapePlacement.CopySegments(draft.ShapeSnapshot)};
        using var editor=new ShapeEditorForm(template);
        if(editor.ShowDialog(this)!=DialogResult.OK||editor.ResultTemplate is not {} shape)return;
        Checkpoint();var saved=shape.Copy();saved.Id=Guid.NewGuid().ToString("N");_document.Shapes.Add(saved);
        draft.ShapeSnapshot=ShapePlacement.CopySegments(saved.Segments);draft.ShapeTemplateId=saved.Id;draft.ShapeName=saved.Name;ShapePlacement.Move(draft,draft.InsertionPoint.Copy());Changed();
    }
    void AssignType(){if(SelectedDraft is not {} d||d.TypeId==CurrentType.Id)return;Checkpoint();d.TypeId=CurrentType.Id;Changed();}
    void DeleteSelected(){if(SelectedDraft is not {} d)return;Checkpoint();_document.Drafts.Remove(d);_selection=new();Changed();}
    void ClearPlacements()
    {
        if(_building||!Enabled)return;
        if(_document.Drafts.Count==0&&_document.PendingPoints.Count==0)return;
        Checkpoint();_document.Drafts.Clear();_document.PendingPoints.Clear();_document.ActiveProfileId=null;
        _document.ActiveTool=ReviewTool.Select;_selection=new();_inspectedIssue=null;_issueIndex=-1;
        _validation=null;Changed();RefreshChecks();
        _checkSummary.Text="Placed bars cleared · Ctrl+Z to undo";
    }
    void Checkpoint(){_undo.Push(_document.ToJson());if(_undo.Count>100){var keep=_undo.Take(100).Reverse().ToArray();_undo.Clear();foreach(var s in keep)_undo.Push(s);}}
    void Undo(){if(_undo.Count==0)return;var currentHost=_document.Host;var currentExisting=_document.ExistingBars;_document=ReviewDocument.FromJson(_undo.Pop());_document.Host=currentHost;_document.ExistingBars=currentExisting;_document.ActiveTool=ReviewTool.Select;if(SelectedDraft==null)_selection=new();Changed();}
    void Changed(){ScheduleChecks();RefreshAll();_saveTimer.Stop();_saveTimer.Start();}
    void RefreshCover()
    {
        var p=_document.CheckSettings;
        static decimal Bound(double value)=>double.IsFinite(value)?Math.Clamp((decimal)value,0,1000):0;
        _coverInner.Value=Bound(p.InnerCoverMm);_coverOuter.Value=Bound(p.OuterCoverMm);_coverTop.Value=Bound(p.TopCoverMm);_coverBottom.Value=Bound(p.BottomCoverMm);
        _allowCollisionViolation.Checked=p.AllowCollisionViolation;
        _coverDirty=false;
        _coverToggle.ForeColor=p.CoverConfirmed?SystemColors.ControlText:Color.FromArgb(190,49,48);
    }
    void ApplyCover()
    {
        Checkpoint();var p=_document.CheckSettings;
        p.InnerCoverMm=(double)_coverInner.Value;p.OuterCoverMm=(double)_coverOuter.Value;p.TopCoverMm=(double)_coverTop.Value;p.BottomCoverMm=(double)_coverBottom.Value;p.CoverConfirmed=true;
        Changed();
    }
    void ScheduleChecks()
    {
        ++_checkRevision;_checking=true;_validation=null;_build.Enabled=false;_checkSummary.Text="Checking…";
        _checkTimer.Stop();_checkTimer.Start();
    }
    async Task RunChecksAsync()
    {
        if(_checkRunning){_checkTimer.Start();return;}
        _checkRunning=true;
        long revision=_checkRevision;var copy=ReviewDocument.FromJson(_document.ToJson());
        try
        {
            var result=await Task.Run(()=>WallValidation.Check(copy));
            if(IsDisposed||revision!=_checkRevision)return;
            _validation=result;_inspectedIssue=null;_issueIndex=-1;_checking=false;RefreshCheckDisplay();Redraw();
        }
        catch(Exception ex){if(IsDisposed||revision!=_checkRevision)return;_checking=false;_validation=null;_checkSummary.Text="Check incomplete: "+ex.Message;_checkSummary.ForeColor=Color.FromArgb(190,49,48);_build.Enabled=false;}
        finally{_checkRunning=false;if(!IsDisposed&&revision!=_checkRevision&&_checking)_checkTimer.Start();}
    }
    public void RefreshChecks()
    {
        _checkTimer.Stop();++_checkRevision;_validation=WallValidation.Check(_document);_inspectedIssue=null;_issueIndex=-1;_checking=false;RefreshCheckDisplay();Redraw();
    }
    public void ReplaceContext(HostContext context,IEnumerable<ExistingBarPath>? existingBars=null)
    {
        _document.Host=context;if(existingBars!=null)_document.ExistingBars=existingBars.ToList();ScheduleChecks();RefreshAll();
    }
    public void SetCheckSettings(WallCheckSettings settings){_document.CheckSettings=settings;Changed();}
    public void ReplaceDraftDocument(ReviewDocument document)
    {
        _document=document;ReviewPresets.Ensure(_document);_selection=new();_undo.Clear();Changed();_plan.Fit();_section.Fit();
    }
    public void SetModelResult(bool success,string message)
    {
        _building=false;Enabled=true;RefreshChecks();_checkSummary.Text=message;_checkSummary.ForeColor=success?Color.FromArgb(42,117,80):Color.FromArgb(190,49,48);
    }
    void RefreshCheckDisplay()
    {
        _build.Enabled=!_checking&&!_building&&!_coverDirty&&!_applyPosition.Enabled&&!_applyGroup.Enabled&&_validation?.CanBuild==true&&BuildRequested!=null;
        if(_building){_checkSummary.Text="Creating…";return;}
        if(_checking){_checkSummary.Text="Checking…";_checkSummary.ForeColor=Color.FromArgb(94,112,125);_issueDetails.Text="";return;}
        if(_validation==null){_checkSummary.Text="Not checked";_issueDetails.Text="";return;}
        var issues=_validation.VisibleIssues.ToList();
        var contextIssue=issues.FirstOrDefault(i=>string.IsNullOrEmpty(i.DraftId));
        _checkSummary.Text=contextIssue?.Message??(issues.Count>0?$"{_validation.Bars.Count} bars · {issues.Count} {(_validation.CanBuild?"warnings":"issues")} · Click to locate":_validation.CanBuild?$"Passed · {_validation.Bars.Count} bars":"No bars to create");
        _checkSummary.ForeColor=issues.Count>0?Color.FromArgb(190,49,48):Color.FromArgb(42,117,80);
        if(SelectedDraft is not {} draft){_issueDetails.Text="";return;}
        var selectedIssues=_validation.ForDraft(draft.Id).ToList();
        int selectedMember=_selection.MemberIndex;
        var first=_inspectedIssue!=null&&selectedIssues.Contains(_inspectedIssue)?_inspectedIssue:selectedIssues.FirstOrDefault(i=>selectedMember<0||(i.DraftId==draft.Id?i.MemberIndex:i.OtherMemberIndex)==selectedMember)??selectedIssues.FirstOrDefault();
        string member=selectedMember>=0&&draft.Group.Enabled?$"Bar {selectedMember+1} · Edits affect the group\n":"";
        if(first==null){_issueDetails.ForeColor=contextIssue!=null?Color.FromArgb(190,49,48):Color.FromArgb(42,117,80);_issueDetails.Text=member+(contextIssue?.Message??"Passed");return;}
        int issueMember=first.DraftId==draft.Id?first.MemberIndex:first.OtherMemberIndex;
        string prefix=issueMember>=0?$"Bar {issueMember+1} · ":"";
        _issueDetails.ForeColor=Color.FromArgb(190,49,48);
        _issueDetails.Text=prefix+first.Message+(selectedIssues.Count>1?$"\n{selectedIssues.Count-1} more · Click below to locate":"");
    }
    void SelectNextIssue()
    {
        if(_validation==null)return;
        var eligible=_validation.VisibleIssues.Where(i=>!string.IsNullOrEmpty(i.DraftId)&&_document.Drafts.Any(d=>d.Id==i.DraftId)).OrderBy(i=>i.Kind==ValidationKind.HostOpening?0:1).ToList();
        if(eligible.Count==0){if(!_document.CheckSettings.CoverConfirmed){_coverPanel.Visible=true;_coverToggle.Text="Cover";}return;}
        _issueIndex=(_issueIndex+1)%eligible.Count;var issue=eligible[_issueIndex];Select(new(){DraftId=issue.DraftId,MemberIndex=issue.MemberIndex});_inspectedIssue=issue;RefreshCheckDisplay();
    }
    void RequestBuild()
    {
        if(_building||BuildRequested==null)return;
        if(_coverDirty||_applyPosition.Enabled||_applyGroup.Enabled){_checkSummary.Text="Apply parameter changes first";return;}
        RefreshChecks();if(_validation?.CanBuild!=true){SelectNextIssue();return;}
        _building=true;_build.Enabled=false;_checkSummary.Text="Creating…";Enabled=false;
        try{BuildRequested.Invoke();}catch(Exception ex){SetModelResult(false,"Creation failed: "+ex.Message);}
    }
    void EditType(bool add)
    {
        var type=add?new ReviewBarType{Grade="HRB400",Name="HRB400",DiameterMm=16}:CurrentType;
        using var dialog=new Form{Text=add?"Add Bar Type":"Edit Bar Type",StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ClientSize=new Size(380,170),Font=Font,AutoScaleMode=AutoScaleMode.Dpi};
        var grade=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=170};grade.Items.AddRange(ReviewPresets.Grades);grade.SelectedItem=type.Grade;if(grade.SelectedIndex<0)grade.SelectedItem="HRB400";
        var diameter=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=170};diameter.Items.AddRange(ReviewPresets.Diameters.Cast<object>().ToArray());diameter.SelectedItem=type.DiameterMm;if(diameter.SelectedIndex<0)diameter.SelectedItem=16d;
        var table=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Padding=new Padding(14)};table.ColumnStyles.Add(new(SizeType.Absolute,115));table.ColumnStyles.Add(new(SizeType.Percent,100));
        table.Controls.Add(LabelFor("Grade"),0,0);table.Controls.Add(grade,1,0);table.Controls.Add(LabelFor("Diameter mm"),0,1);table.Controls.Add(diameter,1,1);
        var ok=new Button{Text="OK",AutoSize=true};var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};var row=Flow();row.Controls.Add(ok);row.Controls.Add(cancel);table.Controls.Add(row,1,2);dialog.Controls.Add(table);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
        ok.Click+=(_,_)=>{
            if(grade.SelectedItem is not string g||diameter.SelectedItem is not double dia)return;
            if(g=="HPB300"&&dia>25){MessageBox.Show(dialog,"Select an HPB300 diameter from 6–25 mm.","Bar Size");return;}
            dialog.DialogResult=DialogResult.OK;
        };
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        string nextGrade=(string)grade.SelectedItem!;double nextDiameter=(double)diameter.SelectedItem!;
        var same=_document.Types.FirstOrDefault(t=>t.Id!=type.Id&&t.Grade==nextGrade&&t.DiameterMm==nextDiameter);
        if(add&&same!=null){_types.SelectedItem=same;return;}
        Checkpoint();type.Grade=type.Name=nextGrade;type.DiameterMm=nextDiameter;if(add)_document.Types.Add(type);Changed();_types.SelectedItem=type;
    }
    void WriteDraft(){
        _document.Stage="wall_rebar_checked";_document.SavedUtc=DateTime.UtcNow.ToString("O");Directory.CreateDirectory(_draftFolder);
        var tmp=_savePath+".tmp";File.WriteAllText(tmp,_document.ToJson(),new System.Text.UTF8Encoding(false));File.Move(tmp,_savePath,true);
    }
    void SaveDraft(bool explicitSave){try{WriteDraft();Text="Wall Reinforcement";}catch(Exception ex){Text="Wall Reinforcement · Save failed";if(explicitSave)MessageBox.Show(this,ex.Message,"Save failed");}}
    static ReviewDocument LoadOrInitial(HostContext context,string folder)
    {
        if(Directory.Exists(folder))foreach(var f in new DirectoryInfo(folder).GetFiles("wall_ui_*.json").OrderByDescending(f=>f.LastWriteTimeUtc).Take(100)){
            try{
                if(f.Length>10000000)continue;var d=ReviewDocument.FromJson(File.ReadAllText(f.FullName));
                bool same=!string.IsNullOrWhiteSpace(d.Host.DocumentKey)?d.Host.DocumentKey==context.DocumentKey:d.Host.DocumentTitle==context.DocumentTitle;
                if(!same||d.Host.HostId!=context.HostId)continue;
                ShapePlacement.Upgrade(d);d.Host=context;d.SessionId=Guid.NewGuid().ToString("N");d.CreatesRevitObjects=false;return d;
            }catch(System.Text.Json.JsonException){}catch(IOException){}catch(ArgumentException){}
        }
        return new ReviewDocument{Host=context};
    }
}
